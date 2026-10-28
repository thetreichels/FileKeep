namespace FileKeep.Core;

/// <summary>
/// Local <see cref="IBlobStore"/>: blobs at chunks/&lt;hh&gt;/&lt;rest&gt;.
/// Content addressing makes deduplication automatic — a blob that already
/// exists is never written twice.
/// </summary>
public sealed class LocalBlobStore : IBlobStore
{
    private readonly string _rootDir;
    private readonly Service.DisguisedLayout? _layout;

    /// <summary>
    /// Plain store: blobs at <c>rootDir/chunks/&lt;hh&gt;/&lt;rest&gt;</c>.
    /// With a layout (slice 2, disguised USB destinations), blobs wear
    /// derived names flat in <c>rootDir/&lt;cover&gt;</c> and are
    /// header-wrapped on write / stripped on read — mirroring
    /// <see cref="SmbBlobStore"/>. In the disguised case
    /// <paramref name="repositoryRoot"/> is the location root (the
    /// drive root for USB), NOT the cover folder itself.
    /// </summary>
    public LocalBlobStore(string repositoryRoot, Service.DisguisedLayout? layout = null)
    {
        _rootDir = Path.Combine(repositoryRoot, "chunks");
        _layout = layout;
    }

    public string PathFor(string chunkIdHex)
    {
        if (chunkIdHex.Length != 64 || !chunkIdHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));
        // P2-D14: canonical lowercase (chunk IDs are SHA-256 hex; a
        // mixed-case foreign ID must address the same blob).
        chunkIdHex = chunkIdHex.ToLowerInvariant();
        if (_layout is not null)
            return Path.Combine(
                Path.GetDirectoryName(_rootDir)!,
                _layout.CoverName,
                _layout.ChunkFileName(chunkIdHex));
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
        // Disguised destinations store header-wrapped blobs; plain ones raw.
        if (_layout is not null)
            blob = Service.DisguisedLayout.WrapContent(blob, path);

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
            }
            catch (IOException ex) when (IsAlreadyExists(ex))
            {
                // Lost the race with another writer that committed this
                // exact chunk concurrently. The chunk ID is the SHA-256
                // of the plaintext, so the winner's blob decrypts to the
                // same plaintext (the nonce travels in the blob) — this
                // is a dedup hit, not a failure. The cause is checked
                // at failure time, not by re-probing the filesystem: a
                // concurrent prune may delete the winner's blob between
                // a failed Move and any later existence check, which
                // used to turn this handled race into a throw.
            }
            // Best-effort only and never throws (see TryFlushDirectory),
            // so it sits outside the Move's catch.
            TryFlushDirectory(Path.GetDirectoryName(path)!);
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
        // Wrapping a span needs a copy; chunks are bounded (CDC max 16 MiB).
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
            }
            catch (IOException ex) when (IsAlreadyExists(ex))
            {
                // Lost the race with another writer that committed this
                // exact chunk concurrently. The chunk ID is the SHA-256
                // of the plaintext, so the winner's blob decrypts to the
                // same plaintext (the nonce travels in the blob) — this
                // is a dedup hit, not a failure. The cause is checked
                // at failure time, not by re-probing the filesystem: a
                // concurrent prune may delete the winner's blob between
                // a failed Move and any later existence check, which
                // used to turn this handled race into a throw.
            }
            // Best-effort only and never throws (see TryFlushDirectory),
            // so it sits outside the Move's catch.
            TryFlushDirectory(Path.GetDirectoryName(path)!);
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
    /// <summary>
    /// True when an <see cref="IOException"/> is the OS reporting that
    /// the move destination already exists — the lost-a-race-with-
    /// another-writer case. Verified empirically on both platforms
    /// (2026-10-27, bare File.Move onto an existing file): Windows
    /// reports Win32 ERROR_ALREADY_EXISTS (183) as 0x800700B7; Linux
    /// reports raw errno EEXIST (17) as 0x11. Checking the failure
    /// cause at failure time, instead of re-probing the filesystem,
    /// is what makes this race-proof: a concurrent prune may delete
    /// the winner's blob between a failed Move and any later
    /// existence check.
    /// </summary>
    private static bool IsAlreadyExists(IOException ex) =>
        ex.HResult == unchecked((int)0x800700B7) // Windows ERROR_ALREADY_EXISTS
        || ex.HResult == 0x11; // Linux EEXIST

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
            byte[] stored = File.ReadAllBytes(path);
            // Detect-and-strip: pre-header disguised blobs read back as-is.
            return _layout is not null
                ? Service.DisguisedLayout.UnwrapContent(stored, path)
                : stored;
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
            string dir = _layout is not null
                ? Path.Combine(Path.GetDirectoryName(_rootDir)!, _layout.CoverName)
                : _rootDir;
            if (!Directory.Exists(dir))
                return 0;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .LongCount(f => !f.Contains(".tmp-", StringComparison.Ordinal));
        }
    }
}
