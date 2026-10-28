using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using FileKeep.Core;
using FileKeep.Core.Service;

namespace FileKeep.Service;

/// <summary>
/// Offline file content storage (slice 2). A file's bytes are split
/// with the backup CDC chunker, each chunk is encrypted with the
/// backup chunk machinery (<see cref="ChunkCrypto"/>), and the
/// resulting blobs are stored content-addressed on the folder's
/// location through the location's own blob store — so a disguised
/// location's layout and header wrapping apply automatically, with
/// no second storage path.
///
/// Chunk IDs are key-derived: HMAC-SHA-256 of the plaintext chunk
/// under the profile's file-content key. They are NOT plaintext
/// hashes. A plaintext hash plus Put's skip-if-present dedup let two
/// profiles sharing a destination collide: the second profile's
/// store of identical content reused the first profile's blob,
/// encrypted under a key it does not hold, and every read-back
/// failed AES-GCM verification (2026-10-27 Save As bug). Key-derived
/// IDs keep dedup within one profile (same key, same content, same
/// ID) while different profiles get different IDs for identical
/// content. Do NOT revert to a plaintext hash. Rekey deliberately
/// preserves existing IDs (it re-encrypts in place with the catalog
/// ID as AAD), so rekeyed blobs keep legacy IDs and still read;
/// only new uploads use the key-derived scheme, and old catalogs
/// keep reading their old blobs unchanged.
///
/// Plaintext discipline: chunk plaintext buffers are zeroed as soon
/// as their ciphertext exists (store) or their bytes have been
/// written out (retrieve). No plaintext is ever written to disk by
/// this type: the only files it creates are encrypted blobs, written
/// by the underlying store. A file's content is addressable purely
/// from its catalog entry (chunk IDs) plus its location — the local
/// folder never needs to exist, which is the point of offline files.
/// </summary>
public static class OfflineFileStore
{
    /// <summary>Domain label for the file-content key subkey (DL-8 pattern).</summary>
    public const string ContentKeyDomainLabel = "FileKeep offline file content v1";

    /// <summary>CDC target average chunk size (same default as backups).</summary>
    public const int AverageChunkSize = 4 * 1024 * 1024;

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

    /// <summary>
    /// A location's blob store plus the connection it may hold (SMB).
    /// Dispose releases the connection; the store must not be used after.
    /// </summary>
    public sealed class OfflineLocationStore : IDisposable
    {
        public IBlobStore Store { get; }
        private readonly IDisposable? _connection;
        private bool _disposed;

        public OfflineLocationStore(IBlobStore store, IDisposable? connection = null)
        {
            Store = store;
            _connection = connection;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Dispose();
        }
    }

    /// <summary>
    /// Resolves the blob store for a folder's location. USB requires the
    /// drive to be connected; SMB connects with the location's stored
    /// credentials (or the FILEKEEP_SMB_PASSWORD env override, same as
    /// the scheduler). Throws <see cref="InvalidOperationException"/>
    /// with a user-facing message when the location cannot serve files.
    /// </summary>
    public static OfflineLocationStore ResolveStore(BackupLocation loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (loc.Type == "smb")
        {
            if (string.IsNullOrWhiteSpace(loc.Share))
                throw new InvalidOperationException("That location has no share configured.");
            string? password = Environment.GetEnvironmentVariable(BackupScheduler.SmbPasswordEnvVar);
            if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(loc.SmbPasswordProtected))
                password = Dpapi.Unprotect(loc.SmbPasswordProtected);
            var share = SmbShare.Connect(loc.Share, loc.SmbUser, password);
            try
            {
                var store = new SmbBlobStore(share, null, loc.Disguise);
                return new OfflineLocationStore(store, share);
            }
            catch
            {
                share.Dispose();
                throw;
            }
        }

        if (loc.Type == "usb")
        {
            if (string.IsNullOrWhiteSpace(loc.VolumeSerial))
                throw new InvalidOperationException("That location has no volume serial configured.");
            var layout = loc.Disguise;
            if (layout is not null)
            {
                // Disguised USB: files sit flat in the cover folder on
                // the drive root (mirrors the disguise slice's USB layout).
                string? coverPath = UsbDriveResolver.ResolveBackupPath(loc.VolumeSerial, layout.CoverName);
                if (coverPath is null)
                    throw new InvalidOperationException(
                        $"USB drive (serial {loc.VolumeSerial}) is not currently connected.");
                string driveRoot = Path.GetPathRoot(coverPath) ?? coverPath;
                return new OfflineLocationStore(new LocalBlobStore(driveRoot, layout));
            }
            string? targetDir = UsbDriveResolver.ResolveBackupPath(loc.VolumeSerial, loc.UsbFolder);
            if (targetDir is null)
                throw new InvalidOperationException(
                    $"USB drive (serial {loc.VolumeSerial}) is not currently connected.");
            return new OfflineLocationStore(new LocalBlobStore(targetDir));
        }

        throw new InvalidOperationException("Offline files are stored on USB/SMB locations only.");
    }

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
    /// <paramref name="stillUnlocked"/> is supplied and returns false
    /// mid-stream (card locked during a transfer), retrieval aborts
    /// with <see cref="OperationCanceledException"/> and no further
    /// bytes are produced.
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
