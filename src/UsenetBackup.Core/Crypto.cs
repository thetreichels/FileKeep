using System.Security.Cryptography;

namespace UsenetBackup.Core;

/// <summary>
/// Boring, conventional cryptography. No invented algorithms.
/// Key derivation: PBKDF2-HMAC-SHA-512. Chunk encryption: AES-256-GCM
/// with the chunk ID as associated data (binds ciphertext to chunk ID).
/// </summary>
public static class KeyDerivation
{
    public const int KeySizeBytes = 32;
    public const int SaltSizeBytes = 32;

    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        if (salt.Length != SaltSizeBytes)
            throw new ArgumentException($"Salt must be {SaltSizeBytes} bytes.", nameof(salt));
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(iterations));
        return Rfc2898DeriveBytes.Pbkdf2(
            passphrase, salt, iterations, HashAlgorithmName.SHA512, KeySizeBytes);
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSizeBytes);
}

public static class ChunkCrypto
{
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    /// <summary>
    /// Encrypts one chunk. Blob layout: nonce (12) || ciphertext || tag (16).
    /// chunkId32 is the 32 raw bytes of SHA-256(plaintext), used as AAD.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> chunkId32)
    {
        if (key.Length != KeyDerivation.KeySizeBytes)
            throw new ArgumentException("Key must be 32 bytes.", nameof(key));
        if (chunkId32.Length != 32)
            throw new ArgumentException("Chunk ID must be 32 bytes.", nameof(chunkId32));

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSizeBytes];
        using var gcm = new AesGcm(key, TagSizeBytes);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, chunkId32);

        byte[] blob = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        nonce.CopyTo(blob.AsSpan(0, NonceSizeBytes));
        ciphertext.CopyTo(blob.AsSpan(NonceSizeBytes, ciphertext.Length));
        tag.CopyTo(blob.AsSpan(NonceSizeBytes + ciphertext.Length, TagSizeBytes));
        return blob;
    }

    /// <summary>
    /// Decrypts one chunk. Throws <see cref="CryptographicException"/> on any
    /// tampering (fails closed: no partial plaintext is returned).
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> key, ReadOnlySpan<byte> chunkId32)
    {
        if (key.Length != KeyDerivation.KeySizeBytes)
            throw new ArgumentException("Key must be 32 bytes.", nameof(key));
        if (chunkId32.Length != 32)
            throw new ArgumentException("Chunk ID must be 32 bytes.", nameof(chunkId32));
        if (blob.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("Chunk blob is too short to be valid.");

        int ctLen = blob.Length - NonceSizeBytes - TagSizeBytes;
        byte[] plaintext = new byte[ctLen];
        using var gcm = new AesGcm(key, TagSizeBytes);
        gcm.Decrypt(
            blob.Slice(0, NonceSizeBytes),
            blob.Slice(NonceSizeBytes, ctLen),
            blob.Slice(NonceSizeBytes + ctLen, TagSizeBytes),
            plaintext,
            chunkId32);
        return plaintext;
    }
}
