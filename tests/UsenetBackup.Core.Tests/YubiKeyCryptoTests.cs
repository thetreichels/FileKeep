using System.Security.Cryptography;
using UsenetBackup.Core.Security;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// YubiKey challenge-response crypto: KEK derivation, passphrase
/// wrapping, and the setup/unlock flow against the simulated provider.
/// </summary>
public sealed class YubiKeyCryptoTests
{
    [Fact]
    public void DeriveKek_IsDeterministic()
    {
        byte[] hmac = new byte[20];
        RandomNumberGenerator.Fill(hmac);
        byte[] k1 = YubiKeyCrypto.DeriveKek(hmac);
        byte[] k2 = YubiKeyCrypto.DeriveKek(hmac);
        Assert.Equal(32, k1.Length);
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void DeriveKek_DiffersPerResponse()
    {
        byte[] k1 = YubiKeyCrypto.DeriveKek(new byte[20]);
        byte[] h2 = new byte[20]; h2[0] = 1;
        byte[] k2 = YubiKeyCrypto.DeriveKek(h2);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void DeriveKek_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => YubiKeyCrypto.DeriveKek(Array.Empty<byte>()));
    }

    [Fact]
    public void Wrap_Unwrap_RoundTrip()
    {
        byte[] kek = YubiKeyCrypto.DeriveKek(new byte[20]);
        const string passphrase = "correct horse battery staple";
        string wrapped = YubiKeyCrypto.Wrap(passphrase, kek);
        Assert.Equal(passphrase, YubiKeyCrypto.Unwrap(wrapped, kek));
    }

    [Fact]
    public void Wrap_UsesRandomNonce()
    {
        byte[] kek = YubiKeyCrypto.DeriveKek(new byte[20]);
        string w1 = YubiKeyCrypto.Wrap("secret", kek);
        string w2 = YubiKeyCrypto.Wrap("secret", kek);
        Assert.NotEqual(w1, w2);
        // Both still decrypt.
        Assert.Equal("secret", YubiKeyCrypto.Unwrap(w1, kek));
        Assert.Equal("secret", YubiKeyCrypto.Unwrap(w2, kek));
    }

    [Fact]
    public void Unwrap_WrongKek_Throws()
    {
        byte[] kek = YubiKeyCrypto.DeriveKek(new byte[20]);
        byte[] wrong = YubiKeyCrypto.DeriveKek(new byte[] { 1 }.Concat(new byte[19]).ToArray());
        string wrapped = YubiKeyCrypto.Wrap("secret", kek);
        Assert.ThrowsAny<CryptographicException>(() => YubiKeyCrypto.Unwrap(wrapped, wrong));
    }

    [Fact]
    public void Unwrap_TamperedBlob_Throws()
    {
        byte[] kek = YubiKeyCrypto.DeriveKek(new byte[20]);
        byte[] blob = Convert.FromBase64String(YubiKeyCrypto.Wrap("secret", kek));
        blob[blob.Length - 1] ^= 0x01; // flip a tag bit
        Assert.ThrowsAny<CryptographicException>(
            () => YubiKeyCrypto.Unwrap(Convert.ToBase64String(blob), kek));
    }

    [Fact]
    public void Unwrap_ShortBlob_Throws()
    {
        byte[] kek = YubiKeyCrypto.DeriveKek(new byte[20]);
        Assert.Throws<ArgumentException>(
            () => YubiKeyCrypto.Unwrap(Convert.ToBase64String(new byte[10]), kek));
    }

    [Fact]
    public void SimulatedProvider_MatchesRfc2202Vector()
    {
        // RFC 2202 test case 1: key = 0x0b * 20, data = "Hi There".
        var provider = new SimulatedYubiKeyProvider();
        byte[] key = Enumerable.Repeat((byte)0x0b, 20).ToArray();
        provider.ProgramSlot(2, key);
        byte[] response = provider.CalculateHmacSha1(
            System.Text.Encoding.ASCII.GetBytes("Hi There"), 2, TimeSpan.FromSeconds(1));
        byte[] expected = Convert.FromHexString("b617318655057264e28bc0b6fb378c8ef146be00");
        Assert.Equal(expected, response);
    }

    [Fact]
    public void SimulatedProvider_UnprogrammedSlot_Throws()
    {
        var provider = new SimulatedYubiKeyProvider();
        Assert.Throws<InvalidOperationException>(() =>
            provider.CalculateHmacSha1(new byte[8], 2, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Setup_Unlock_RoundTrip()
    {
        var provider = new SimulatedYubiKeyProvider();
        const string passphrase = "my-test-passphrase";
        var (challenge, wrapped) = YubiKeySetup.Setup(provider, passphrase, slot: 2);
        Assert.False(string.IsNullOrEmpty(challenge));
        Assert.False(string.IsNullOrEmpty(wrapped));
        Assert.Equal(YubiKeyCrypto.ChallengeLength, Convert.FromBase64String(challenge).Length);

        string unlocked = YubiKeySetup.Unlock(provider, challenge, wrapped, slot: 2);
        Assert.Equal(passphrase, unlocked);
    }

    [Fact]
    public void Unlock_ReprogrammedSlot_Fails()
    {
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped) = YubiKeySetup.Setup(provider, "secret", slot: 2);
        // User wipes/reprograms the key: the wrapped copy is unrecoverable.
        provider.ProgramSlot(2, RandomNumberGenerator.GetBytes(20));
        Assert.ThrowsAny<CryptographicException>(
            () => YubiKeySetup.Unlock(provider, challenge, wrapped, slot: 2));
    }

    [Fact]
    public void Setup_NoDevice_Throws()
    {
        var empty = new EmptyProvider();
        Assert.Throws<InvalidOperationException>(() => YubiKeySetup.Setup(empty, "x"));
    }

    [Fact]
    public void Setup_RejectsBadSlot()
    {
        var provider = new SimulatedYubiKeyProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => YubiKeySetup.Setup(provider, "x", slot: 3));
    }

    private sealed class EmptyProvider : IYubiKeyProvider
    {
        public IReadOnlyList<YubiKeyInfo> FindDevices() => Array.Empty<YubiKeyInfo>();
        public byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout) =>
            throw new NotImplementedException();
        public void ProgramHmacSha1Slot(int slot, byte[] secret, bool requireTouch) =>
            throw new NotImplementedException();
    }
}
