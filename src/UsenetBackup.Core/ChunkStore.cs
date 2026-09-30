using System.Security.Cryptography;

namespace UsenetBackup.Core;

/// <summary>
/// Content-addressed encrypted chunk storage: chunks/&lt;hh&gt;/&lt;rest&gt;.
/// Chunk ID = SHA-256(plaintext chunk). Deduplication falls out naturally:
/// a chunk that already exists is never written twice.
/// </summary>
public sealed class ChunkStore
{
    private readonly string _rootDir;
    private readonly byte[] _key;

    public ChunkStore(string repositoryRoot, byte[] key)
    {
        _rootDir = Path.Combine(repositoryRoot, "chunks");
        _key = key;
    }

    public string PathFor(string chunkIdHex)
    {
        if (chunkIdHex.Length != 64)
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));
        return Path.Combine(_rootDir, chunkIdHex[..2], chunkIdHex[2..]);
    }

    public bool Exists(string chunkIdHex) => File.Exists(PathFor(chunkIdHex));

    /// <summary>Stores a plaintext chunk if absent. Returns its chunk ID (hex).</summary>
    public string Write(ReadOnlySpan<byte> plaintext)
    {
        string id = Hashing.Sha256Hex(plaintext);
        string path = PathFor(id);
        if (File.Exists(path))
            return id;

        byte[] blob = ChunkCrypto.Encrypt(plaintext, _key, Hashing.HexToBytes(id));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write atomically so interrupted backups never leave half a chunk.
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
        return id;
    }

    /// <summary>
    /// Reads a chunk, decrypts it (AAD binds ciphertext to chunk ID), and
    /// re-hashes the plaintext. Throws on any tampering or corruption.
    /// </summary>
    public byte[] Read(string chunkIdHex)
    {
        string path = PathFor(chunkIdHex);
        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidDataException($"Chunk {chunkIdHex} is missing from the repository.", ex);
        }

        byte[] chunkId = Hashing.HexToBytes(chunkIdHex);
        byte[] plaintext;
        try
        {
            plaintext = ChunkCrypto.Decrypt(blob, _key, chunkId);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                $"Chunk {chunkIdHex} failed authentication (wrong key or tampered data).", ex);
        }

        string actual = Hashing.Sha256Hex(plaintext);
        if (!actual.Equals(chunkIdHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Chunk {chunkIdHex} hash mismatch after decryption.");
        return plaintext;
    }

    public long StoredChunkCount()
    {
        if (!Directory.Exists(_rootDir))
            return 0;
        return Directory.EnumerateFiles(_rootDir, "*", SearchOption.AllDirectories)
            .LongCount(f => !f.EndsWith(".tmp-", StringComparison.Ordinal));
    }
}
