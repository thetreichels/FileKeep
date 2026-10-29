using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace FileKeep.Core;

/// <summary>
/// Offline file content keys and chunk reassembly, in Core so the
/// portable reader (FileKeep.Reader, which must NOT reference
/// FileKeep.Service) can reconstruct files with the exact same
/// machinery as the Service. <see cref="FileKeep.Service.OfflineFileStore"/>
/// delegates DeriveContentKey, Store, and Retrieve to this type;
/// the reader's PUT-back path stores through the same Store, so
/// edited files chunk identically on the main machine and on the
/// reader. Chunk IDs are key-derived (HMAC-SHA-256 of the
/// plaintext chunk under the file-content key) — do NOT revert to
/// a plaintext hash (see the Service OfflineFileStore comment for
/// the 2026-10-27 Save As collision bug this prevents).
/// </summary>
public static class OfflineChunkStore
{
    /// <summary>Domain label for the file-content key subkey (DL-8 pattern).</summary>
    public const string ContentKeyDomainLabel = "FileKeep offline file content v1";

    /// <summary>Derives the file-content key from the vault root secret.</summary>
    public static byte[] DeriveContentKey(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] labelBytes = Encoding.UTF8.GetBytes(ContentKeyDomainLabel);
        byte[] input = new byte[secretBytes.Length + labelBytes.Length];
        try
        {
            Buffer.BlockCopy(secretBytes, 0, input, 0, secretBytes.Length);
            Buffer.BlockCopy(labelBytes, 0, input, secretBytes.Length, labelBytes.Length);
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    /// <summary>CDC target average chunk size (same default as backups).</summary>
    public const int AverageChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// Chunks + encrypts a stream into the store. Returns the chunk IDs
    /// (in order) and the total plaintext size. An empty stream yields
    /// no chunks and size zero.
    /// </summary>
    public static (List<string> ChunkIds, long TotalSize) Store(
        Stream input, IBlobStore store, byte[] contentKey)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(contentKey);
        if (contentKey.Length != 32) throw new ArgumentException("Content key must be 32 bytes.", nameof(contentKey));

        var chunker = new CdcChunker(AverageChunkSize / 4, AverageChunkSize, AverageChunkSize * 4);
        int maxChunk = AverageChunkSize * 4;
        byte[] accum = ArrayPool<byte>.Shared.Rent(maxChunk);
        var completed = new List<int>();
        var chunkIds = new List<string>();
        long total = 0;
        try
        {
            int accumLen = 0;
            void Drain(int chunkLen)
            {
                // Key-derived chunk ID (see the class comment): HMAC
                // of the plaintext chunk under the content key. The
                // ID doubles as the AES-GCM AAD, exactly as before,
                // and Retrieve reads it straight from the catalog, so
                // no other path changes.
                byte[] id32 = HMACSHA256.HashData(contentKey, accum.AsSpan(0, chunkLen));
                try
                {
                    byte[] blob = ChunkCrypto.Encrypt(accum.AsSpan(0, chunkLen), contentKey, id32);
                    try { store.Put(Convert.ToHexString(id32).ToLowerInvariant(), blob); }
                    finally { CryptographicOperations.ZeroMemory(blob); }
                    chunkIds.Add(Convert.ToHexString(id32).ToLowerInvariant());
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(id32);
                }
                total += chunkLen;
                int rest = accumLen - chunkLen;
                Buffer.BlockCopy(accum, chunkLen, accum, 0, rest);
                // The drained region is ciphertext now; the shifted
                // remainder still holds plaintext and stays until drained.
                accumLen = rest;
            }

            int read;
            while ((read = input.Read(accum, accumLen, accum.Length - accumLen)) > 0)
            {
                chunker.Feed(accum.AsSpan(accumLen, read), completed);
                accumLen += read;
                foreach (int chunkLen in completed)
                    Drain(chunkLen);
                completed.Clear();
            }
            chunker.Finish(completed);
            foreach (int chunkLen in completed)
                Drain(chunkLen);
            completed.Clear();
            return (chunkIds, total);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accum.AsSpan(0, accum.Length));
            ArrayPool<byte>.Shared.Return(accum);
        }
    }

    /// <summary>
    /// Reassembles a file's chunks into <paramref name="output"/>,
    /// verifying every chunk (AES-GCM fails closed on tampering) and
    /// zeroing each plaintext buffer after it is written. When
    /// <paramref name="stillUnlocked"/> returns false mid-stream,
    /// retrieval aborts with <see cref="OperationCanceledException"/>
    /// and no further bytes are produced. Throws
    /// <see cref="InvalidDataException"/> when a chunk is missing
    /// from <paramref name="store"/>.
    /// </summary>
    public static void Retrieve(
        IReadOnlyList<string> chunkIds, IBlobStore store, byte[] contentKey,
        Stream output, Func<bool>? stillUnlocked = null)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(contentKey);
        ArgumentNullException.ThrowIfNull(output);
        if (contentKey.Length != 32) throw new ArgumentException("Content key must be 32 bytes.", nameof(contentKey));

        foreach (string chunkId in chunkIds)
        {
            if (stillUnlocked is not null && !stillUnlocked())
                throw new OperationCanceledException("Offline Folders was locked during transfer.");
            byte[] blob = store.Get(chunkId);
            byte[] id32 = Hashing.HexToBytes(chunkId);
            try
            {
                byte[] plaintext = ChunkCrypto.Decrypt(blob, contentKey, id32);
                try { output.Write(plaintext, 0, plaintext.Length); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
                CryptographicOperations.ZeroMemory(id32);
            }
        }
    }
}
