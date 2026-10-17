using System.Security.Cryptography;
using System.Text;

namespace UsenetBackup.Core.Security;

/// <summary>
/// Wraps the repository passphrase with a key derived from a YubiKey
/// HMAC-SHA1 challenge-response, so that opening a restore session requires
/// a physical touch while scheduled backups keep using the DPAPI copy
/// unattended.
/// </summary>
/// <remarks>
/// <para>Key hierarchy:</para>
/// <para>
/// 1. The YubiKey holds an HMAC-SHA1 secret in one of its OTP slots (programmed
///    once at setup, touch-required). The secret itself never leaves the key.
/// 2. For a fixed per-install random challenge, the key returns a 20-byte
///    HMAC-SHA1 response after a touch.
/// 3. <see cref="DeriveKek"/> runs HKDF-SHA256 over that response to produce
///    a 256-bit key-encryption key (KEK). The HMAC response exists only in
///    process memory and is wiped after use.
/// 4. <see cref="Wrap"/> encrypts the passphrase with AES-256-GCM under the
///    KEK. Only the (nonce || ciphertext || tag) blob is stored.
/// </para>
/// <para>
/// Losing the YubiKey (or wiping its slot) makes the wrapped copy
/// unrecoverable by design; the DPAPI copy keeps unattended backups working.
/// </para>
/// </remarks>
public static class YubiKeyCrypto
{
    /// <summary>HKDF-SHA256 salt domain separator. Versioned so the KDF can evolve.</summary>
    public static readonly byte[] KekSalt = Encoding.ASCII.GetBytes("FileKeep-YubiKey-KEK-v1");

    /// <summary>HKDF-SHA256 info string for passphrase wrapping.</summary>
    public static readonly byte[] KekInfo = Encoding.ASCII.GetBytes("passphrase-wrap");

    /// <summary>Challenge length in bytes (stored in the service config).</summary>
    public const int ChallengeLength = 32;

    /// <summary>AES-GCM nonce length in bytes.</summary>
    public const int NonceLength = 12;

    /// <summary>AES-GCM authentication tag length in bytes.</summary>
    public const int TagLength = 16;

    /// <summary>
    /// Derives the 256-bit key-encryption key from a YubiKey HMAC-SHA1
    /// challenge response via HKDF-SHA256.
    /// </summary>
    public static byte[] DeriveKek(byte[] hmacResponse)
    {
        if (hmacResponse is null || hmacResponse.Length == 0)
            throw new ArgumentException("HMAC response must not be empty.", nameof(hmacResponse));
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, hmacResponse, 32, KekSalt, KekInfo);
    }

    /// <summary>
    /// Encrypts <paramref name="passphrase"/> under <paramref name="kek"/>
    /// with AES-256-GCM. Returns base64(nonce || ciphertext || tag).
    /// </summary>
    public static string Wrap(string passphrase, byte[] kek)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));
        if (kek is null || kek.Length != 32)
            throw new ArgumentException("KEK must be 32 bytes.", nameof(kek));

        byte[] plainBytes = Encoding.UTF8.GetBytes(passphrase);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] cipher = new byte[plainBytes.Length];
        byte[] tag = new byte[TagLength];
        try
        {
            using var gcm = new AesGcm(kek, TagLength);
            gcm.Encrypt(nonce, plainBytes, cipher, tag);
            byte[] blob = new byte[NonceLength + cipher.Length + TagLength];
            Buffer.BlockCopy(nonce, 0, blob, 0, NonceLength);
            Buffer.BlockCopy(cipher, 0, blob, NonceLength, cipher.Length);
            Buffer.BlockCopy(tag, 0, blob, NonceLength + cipher.Length, TagLength);
            return Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    /// <summary>
    /// Decrypts a blob produced by <see cref="Wrap"/>. Throws
    /// <see cref="CryptographicException"/> if the KEK is wrong or the blob
    /// was tampered with.
    /// </summary>
    public static string Unwrap(string wrappedBase64, byte[] kek)
    {
        if (string.IsNullOrEmpty(wrappedBase64))
            throw new ArgumentException("Wrapped blob must not be empty.", nameof(wrappedBase64));
        if (kek is null || kek.Length != 32)
            throw new ArgumentException("KEK must be 32 bytes.", nameof(kek));

        byte[] blob;
        try { blob = Convert.FromBase64String(wrappedBase64); }
        catch (FormatException ex) { throw new ArgumentException("Wrapped blob is not valid base64.", ex); }
        if (blob.Length < NonceLength + TagLength + 1)
            throw new ArgumentException("Wrapped blob is too short.", nameof(wrappedBase64));

        byte[] nonce = new byte[NonceLength];
        byte[] tag = new byte[TagLength];
        int cipherLen = blob.Length - NonceLength - TagLength;
        byte[] cipher = new byte[cipherLen];
        byte[] plain = new byte[cipherLen];
        try
        {
            Buffer.BlockCopy(blob, 0, nonce, 0, NonceLength);
            Buffer.BlockCopy(blob, NonceLength, cipher, 0, cipherLen);
            Buffer.BlockCopy(blob, NonceLength + cipherLen, tag, 0, TagLength);
            using var gcm = new AesGcm(kek, TagLength);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    /// <summary>Generates a fresh random challenge for YubiKey setup.</summary>
    public static byte[] GenerateChallenge() => RandomNumberGenerator.GetBytes(ChallengeLength);
}
