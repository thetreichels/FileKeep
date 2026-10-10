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
    /// Allocates the blob; for hot loops prefer <see cref="EncryptInto"/>,
    /// which writes into a caller-provided (e.g. pooled) buffer.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> chunkId32)
    {
        byte[] blob = new byte[NonceSizeBytes + plaintext.Length + TagSizeBytes];
        EncryptInto(plaintext, key, chunkId32, blob);
        return blob;
    }

    /// <summary>
    /// Encrypts one chunk directly into <paramref name="blob"/>, which must be
    /// exactly <c>NonceSizeBytes + plaintext.Length + TagSizeBytes</c> bytes.
    /// The nonce and tag are produced without heap allocation (the nonce is
    /// generated in place, the 16-byte tag lives on the stack), so a pooled
    /// buffer can be reused across chunks with zero per-chunk allocation.
    /// </summary>
    public static void EncryptInto(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> chunkId32, Span<byte> blob)
    {
        if (key.Length != KeyDerivation.KeySizeBytes)
            throw new ArgumentException("Key must be 32 bytes.", nameof(key));
        if (chunkId32.Length != 32)
            throw new ArgumentException("Chunk ID must be 32 bytes.", nameof(chunkId32));
        if (blob.Length != NonceSizeBytes + plaintext.Length + TagSizeBytes)
            throw new ArgumentException(
                $"Blob buffer must be exactly {NonceSizeBytes + plaintext.Length + TagSizeBytes} bytes.", nameof(blob));

        Span<byte> nonce = blob[..NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);
        Span<byte> tag = stackalloc byte[TagSizeBytes];
        using var gcm = new AesGcm(key, TagSizeBytes);
        // Encrypt straight into the blob's ciphertext region: no intermediate
        // ciphertext allocation and no second copy.
        gcm.Encrypt(nonce, plaintext, blob.Slice(NonceSizeBytes, plaintext.Length), tag, chunkId32);
        tag.CopyTo(blob.Slice(NonceSizeBytes + plaintext.Length, TagSizeBytes));
    }

    /// <summary>
    /// Decrypts one chunk. Throws <see cref="CryptographicException"/> on any
    /// tampering (fails closed: no partial plaintext is returned).
    /// Allocates the plaintext; for hot loops prefer <see cref="DecryptInto"/>,
    /// which decrypts into a caller-provided (e.g. pooled) buffer.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> key, ReadOnlySpan<byte> chunkId32)
    {
        int ctLen = CheckedCiphertextLength(blob);
        byte[] plaintext = new byte[ctLen];
        DecryptInto(blob, key, chunkId32, plaintext);
        return plaintext;
    }

    /// <summary>
    /// Decrypts one chunk into <paramref name="plaintext"/>, which must be
    /// exactly <c>blob.Length - NonceSizeBytes - TagSizeBytes</c> bytes.
    /// Throws <see cref="CryptographicException"/> on any tampering
    /// (fails closed: no partial plaintext is returned).
    /// </summary>
    public static void DecryptInto(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> key, ReadOnlySpan<byte> chunkId32, Span<byte> plaintext)
    {
        if (key.Length != KeyDerivation.KeySizeBytes)
            throw new ArgumentException("Key must be 32 bytes.", nameof(key));
        if (chunkId32.Length != 32)
            throw new ArgumentException("Chunk ID must be 32 bytes.", nameof(chunkId32));
        int ctLen = CheckedCiphertextLength(blob);
        if (plaintext.Length != ctLen)
            throw new ArgumentException(
                $"Plaintext buffer must be exactly {ctLen} bytes.", nameof(plaintext));

        using var gcm = new AesGcm(key, TagSizeBytes);
        gcm.Decrypt(
            blob.Slice(0, NonceSizeBytes),
            blob.Slice(NonceSizeBytes, ctLen),
            blob.Slice(NonceSizeBytes + ctLen, TagSizeBytes),
            plaintext,
            chunkId32);
    }

    private static int CheckedCiphertextLength(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("Chunk blob is too short to be valid.");
        return blob.Length - NonceSizeBytes - TagSizeBytes;
    }
}
