namespace UsenetBackup.Core;

/// <summary>
/// <see cref="IBlobStore"/> that writes chunks directly to an SMB share,
/// in the same <c>chunks/&lt;hh&gt;/&lt;rest&gt;</c> layout as
/// <see cref="LocalBlobStore"/>. Used for streaming backups (e.g. whole-disk
/// images) where staging the full payload locally is infeasible — chunks go
/// straight to the network share as they are produced, with only a small
/// in-memory buffer held locally.
/// </summary>
public sealed class SmbBlobStore : IBlobStore
{
    private readonly string _chunksDir;
    private readonly SmbShare _share;

    /// <summary>The share path (UNC or local) this store writes to.</summary>
    public string SharePath => _share.UncPath;

    public SmbBlobStore(SmbShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        _chunksDir = share.Combine("chunks");
        _share = share;
    }

    public string PathFor(string chunkIdHex)
    {
        if (chunkIdHex.Length != 64 || !chunkIdHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));
        return Path.Combine(_chunksDir, chunkIdHex[..2], chunkIdHex[2..]);
    }

    public bool Exists(string chunkIdHex) => File.Exists(PathFor(chunkIdHex));

    public void Put(string chunkIdHex, byte[] blob)
    {
        string path = PathFor(chunkIdHex);
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write atomically so interrupted backups never leave half a blob.
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tmp, blob);
            try
            {
                File.Move(tmp, path);
            }
            catch (IOException)
            {
                // Lost the race with another writer; our tmp is redundant.
                if (!File.Exists(path))
                    throw;
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    public void Put(string chunkIdHex, ReadOnlySpan<byte> blob)
    {
        string path = PathFor(chunkIdHex);
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(blob);
            try
            {
                File.Move(tmp, path);
            }
            catch (IOException)
            {
                if (!File.Exists(path))
                    throw;
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    public byte[] Get(string chunkIdHex)
    {
        string path = PathFor(chunkIdHex);
        if (!File.Exists(path))
            throw new InvalidDataException($"Chunk not found on SMB share: {chunkIdHex}.");
        return File.ReadAllBytes(path);
    }

    public long StoredCount
    {
        get
        {
            if (!Directory.Exists(_chunksDir))
                return 0;
            return Directory.GetFiles(_chunksDir, "*", SearchOption.AllDirectories).Length;
        }
    }
}
