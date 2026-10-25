namespace FileKeep.Core;

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
    private readonly string? _disguiseSubfolder;
    private readonly Service.DisguisedLayout? _layout;

    /// <summary>The share path (UNC or local) this store writes to.</summary>
    public string SharePath => _share.UncPath;

    /// <summary>
    /// Plain store: chunks under <c>share\chunks\</c>. With a layout, chunks
    /// wear disguised names flat in <c>share\subfolder</c> (defaulting to
    /// the layout's cover name) — see <see cref="Service.DisguisedLayout"/>.
    /// </summary>
    public SmbBlobStore(SmbShare share, string? disguiseSubfolder = null,
        Service.DisguisedLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(share);
        _chunksDir = share.Combine("chunks");
        _share = share;
        _disguiseSubfolder = disguiseSubfolder;
        _layout = layout;
    }

    public string PathFor(string chunkIdHex)
    {
        if (chunkIdHex.Length != 64 || !chunkIdHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));
        if (_layout is not null)
            return Path.Combine(
                _share.Combine(_disguiseSubfolder ?? _layout.CoverName),
                _layout.ChunkFileName(chunkIdHex));
        return Path.Combine(_chunksDir, chunkIdHex[..2], chunkIdHex[2..]);
    }

    public bool Exists(string chunkIdHex) => File.Exists(PathFor(chunkIdHex));

    /// <summary>Deletes a stored blob. Used to replace a corrupt copy
    /// with a verified re-download (SF-13).</summary>
    public void Delete(string chunkIdHex)
    {
        try { File.Delete(PathFor(chunkIdHex)); }
        catch { /* best effort: a failed delete surfaces at the next verify */ }
    }

    public void Put(string chunkIdHex, byte[] blob)
    {
        string path = PathFor(chunkIdHex);
        if (File.Exists(path))
            return;
        // Disguised destinations store header-wrapped blobs; plain ones raw.
        if (_layout is not null)
            blob = Service.DisguisedLayout.WrapContent(blob, path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write atomically so interrupted backups never leave half a blob,
        // and flush to disk before the rename so a power loss cannot leave
        // the renamed blob's contents behind in a volatile cache (DL-9).
        // Over SMB the flush is best-effort — the server decides how much
        // of it reaches stable storage.
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(blob, 0, blob.Length);
                fs.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(tmp, path);
                TryFlushDirectory(Path.GetDirectoryName(path)!);
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
        // Wrapping a span needs a copy; streaming callers hand us the
        // blob anyway, and chunks are bounded (CDC max 16 MiB).
        byte[]? wrapped = _layout is not null
            ? Service.DisguisedLayout.WrapContent(blob.ToArray(), path)
            : null;
        if (wrapped is not null)
            blob = wrapped;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(blob);
                fs.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(tmp, path);
                TryFlushDirectory(Path.GetDirectoryName(path)!);
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

    /// <summary>
    /// Best-effort fsync of the directory holding a just-renamed blob, so
    /// the rename (directory entry) is as durable as the file contents.
    /// Unix-only: Windows cannot open a directory as a stream (NTFS/SMB
    /// handle metadata durability server-side), so this is a no-op there;
    /// any other failure is swallowed — durability here is strictly
    /// best-effort and must never fail a Put. Residual caveat: the entry
    /// of a subdirectory created moments earlier (the chunk's hh folder
    /// in its parent) is covered only by the filesystem's own journaling,
    /// not by this flush.
    /// </summary>
    private static void TryFlushDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            using var fs = new FileStream(dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Flush(flushToDisk: true);
        }
        catch
        {
            // Best effort only.
        }
    }

    public byte[] Get(string chunkIdHex)
    {
        string path = PathFor(chunkIdHex);
        if (!File.Exists(path))
            throw new InvalidDataException($"Chunk not found on SMB share: {chunkIdHex}.");
        byte[] stored = File.ReadAllBytes(path);
        // Detect-and-strip: pre-header disguised blobs read back as-is.
        return _layout is not null
            ? Service.DisguisedLayout.UnwrapContent(stored, path)
            : stored;
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
