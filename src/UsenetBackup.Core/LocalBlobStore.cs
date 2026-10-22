namespace UsenetBackup.Core;

/// <summary>
/// Local <see cref="IBlobStore"/>: blobs at chunks/&lt;hh&gt;/&lt;rest&gt;.
/// Content addressing makes deduplication automatic — a blob that already
/// exists is never written twice.
/// </summary>
public sealed class LocalBlobStore : IBlobStore
{
    private readonly string _rootDir;

    public LocalBlobStore(string repositoryRoot)
    {
        _rootDir = Path.Combine(repositoryRoot, "chunks");
    }

    public string PathFor(string chunkIdHex)
    {
        if (chunkIdHex.Length != 64 || !chunkIdHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));
        // P2-D14: canonical lowercase (chunk IDs are SHA-256 hex; a
        // mixed-case foreign ID must address the same blob).
        chunkIdHex = chunkIdHex.ToLowerInvariant();
        return Path.Combine(_rootDir, chunkIdHex[..2], chunkIdHex[2..]);
    }

    public bool Exists(string chunkIdHex) => File.Exists(PathFor(chunkIdHex));

    /// <summary>
    /// Deletes a staged chunk blob. Used by batched staging to prune a
    /// batch's chunks after its upload is confirmed. Never deletes
    /// manifests, catalog, or journal — only chunk blobs.
    /// </summary>
    public void Delete(string chunkIdHex)
    {
        string path = PathFor(chunkIdHex);
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public void Put(string chunkIdHex, byte[] blob)
    {
        string path = PathFor(chunkIdHex);
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write atomically so interrupted backups never leave half a blob,
        // and flush to disk before the rename so a power loss cannot leave
        // the renamed blob's contents behind in a volatile cache (DL-9).
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
            catch (IOException) when (File.Exists(path))
            {
                // Another thread committed this exact chunk concurrently
                // (content-addressed: identical bytes). Treat as dedup hit.
            }
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    /// <summary>
    /// Span overload: writes the blob without an intermediate array copy,
    /// for backup loops that encrypt into a pooled buffer. Same atomic
    /// tmp-file + move semantics as <see cref="Put(string, byte[])"/>.
    /// </summary>
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
            {
                fs.Write(blob);
                fs.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(tmp, path);
                TryFlushDirectory(Path.GetDirectoryName(path)!);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another thread committed this exact chunk concurrently
                // (content-addressed: identical bytes). Treat as dedup hit.
            }
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    /// <summary>
    /// Best-effort fsync of the directory holding a just-renamed blob, so
    /// the rename (directory entry) is as durable as the file contents.
    /// Unix-only: Windows cannot open a directory as a stream (NTFS
    /// journals metadata instead), so this is a no-op there; any other
    /// failure is swallowed — durability here is strictly best-effort and
    /// must never fail a Put. Residual caveat: the entry of a subdirectory
    /// created moments earlier (the chunk's hh folder in its parent) is
    /// covered only by the filesystem's own journaling, not by this flush.
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
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException($"Chunk {chunkIdHex} is missing from the repository.", ex);
        }
    }

    public long StoredCount
    {
        get
        {
            if (!Directory.Exists(_rootDir))
                return 0;
            return Directory.EnumerateFiles(_rootDir, "*", SearchOption.AllDirectories)
                .LongCount(f => !f.Contains(".tmp-", StringComparison.Ordinal));
        }
    }
}
