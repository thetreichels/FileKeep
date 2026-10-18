using System.Security.Cryptography;
using System.Text;
using UsenetBackup.Core.Security;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// YubiKey recovery path tests.
/// The raw-HID framing (Recovery/YubiKeyRecovery.cs) needs a physical key;
/// here we test the key-derivation contract it must satisfy: the KEK
/// derivation must match YubiKeyCrypto.DeriveKek exactly (the recovery
/// implementation delegates to it, verified by inspection).
/// </summary>
public sealed class YubiKeyRecoveryTests
{
    // ---- KEK derivation contract (must match YubiKeyCrypto.DeriveKek) ----

    [Fact]
    public void DeriveKek_Rfc4231Style_KnownAnswer()
    {
        // Independent known-answer: HKDF-SHA256 with the FileKeep salt/info.
        byte[] hmacResponse = Encoding.ASCII.GetBytes("Sample #2");
        byte[] got = YubiKeyCrypto.DeriveKek(hmacResponse);

        byte[] expected = HkdfSha256(
            hmacResponse,
            Encoding.ASCII.GetBytes("FileKeep-YubiKey-KEK-v1"),
            Encoding.ASCII.GetBytes("passphrase-wrap"),
            32);
        Assert.Equal(32, got.Length);
        Assert.Equal(expected, got);
    }

    private static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int len)
    {
        using var extract = new HMACSHA256(salt);
        byte[] prk = extract.ComputeHash(ikm);
        var okm = new byte[len];
        byte[] t = Array.Empty<byte>();
        int pos = 0;
        for (byte c = 1; pos < len; c++)
        {
            using var expand = new HMACSHA256(prk);
            byte[] input = new byte[t.Length + info.Length + 1];
            Buffer.BlockCopy(t, 0, input, 0, t.Length);
            Buffer.BlockCopy(info, 0, input, t.Length, info.Length);
            input[^1] = c;
            t = expand.ComputeHash(input);
            int n = Math.Min(t.Length, len - pos);
            Buffer.BlockCopy(t, 0, okm, pos, n);
            pos += n;
        }
        return okm;
    }

    // ---- Wrap/Unwrap round-trip (what the recovery flow depends on) ----

    [Fact]
    public void WrapUnwrap_Passphrase_RoundTrips()
    {
        byte[] hmac = RandomNumberGenerator.GetBytes(20);
        byte[] kek = YubiKeyCrypto.DeriveKek(hmac);
        try
        {
            string wrapped = YubiKeyCrypto.Wrap("correct horse battery staple", kek);
            Assert.False(string.IsNullOrEmpty(wrapped));
            // Wrapped blob must not contain the plaintext.
            string wrappedBytes = Encoding.Latin1.GetString(Convert.FromBase64String(wrapped));
            Assert.DoesNotContain("correct horse", wrappedBytes);
            string unwrapped = YubiKeyCrypto.Unwrap(wrapped, kek);
            Assert.Equal("correct horse battery staple", unwrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(hmac);
        }
    }

    [Fact]
    public void Unwrap_WrongKek_Throws()
    {
        byte[] hmac1 = RandomNumberGenerator.GetBytes(20);
        byte[] hmac2 = RandomNumberGenerator.GetBytes(20);
        byte[] kek1 = YubiKeyCrypto.DeriveKek(hmac1);
        byte[] kek2 = YubiKeyCrypto.DeriveKek(hmac2);
        try
        {
            string wrapped = YubiKeyCrypto.Wrap("secret", kek1);
            Assert.ThrowsAny<CryptographicException>(() =>
                YubiKeyCrypto.Unwrap(wrapped, kek2));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek1);
            CryptographicOperations.ZeroMemory(kek2);
        }
    }
}
