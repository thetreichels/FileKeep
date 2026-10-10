namespace UsenetBackup.Core;

/// <summary>
/// Dumb, content-addressed blob storage. Blobs are the encrypted chunk
/// bytes (nonce || ciphertext || tag); the blob ID is the chunk ID =
/// lowercase hex of SHA-256(plaintext chunk). Encryption, authentication
/// and hash verification stay in <see cref="ChunkCrypto"/> / the
/// repository layer — a store never sees plaintext or keys.
/// </summary>
public interface IBlobStore
{
    /// <summary>True if the blob is already stored.</summary>
    bool Exists(string chunkIdHex);

    /// <summary>
    /// Stores a blob. If the blob is already present the call is a no-op
    /// (deduplication); implementations should make this cheap.
    /// </summary>
    void Put(string chunkIdHex, byte[] blob);

    /// <summary>
    /// Stores a blob from a span, for hot loops that encrypt into a pooled
    /// buffer. The default implementation copies into an array; stores that
    /// can write a span directly (e.g. <see cref="LocalBlobStore"/>) override
    /// this to avoid the copy.
    /// </summary>
    void Put(string chunkIdHex, ReadOnlySpan<byte> blob) => Put(chunkIdHex, blob.ToArray());

    /// <summary>
    /// Returns the stored blob. Throws <see cref="InvalidDataException"/>
    /// when the blob is missing.
    /// </summary>
    byte[] Get(string chunkIdHex);

    /// <summary>
    /// Number of blobs the store knows about. For remote stores this is
    /// the count of recorded uploads, not a live server inventory.
    /// </summary>
    long StoredCount { get; }
}
