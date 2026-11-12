namespace FileKeep.Core;

/// <summary>
/// Local <see cref="IBlobStore"/>: blobs at chunks/&lt;hh&gt;/&lt;rest&gt;.
/// Content addressing makes deduplication automatic — a blob that already
/// exists is never written twice. The atomic write core, the already-exists
/// race check, and the path helpers live in <see cref="BlobStoreIO"/>
/// (shared with <see cref="SmbBlobStore"/>, D1); this store only supplies
/// its canonicalization (lowercase, P2-D14) and its layout.
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
        BlobStoreIO.ValidateChunkId(chunkIdHex);
        // P2-D14: canonical lowercase (chunk IDs are SHA-256 hex; a
        // mixed-case foreign ID must address the same blob).
        if (_layout is not null)
            return BlobStoreIO.DisguisedChunkPath(
                Path.Combine(Path.GetDirectoryName(_rootDir)!, _layout.CoverName),
                _layout, chunkIdHex);
        return BlobStoreIO.PlainChunkPath(_rootDir, chunkIdHex,
            static id => id.ToLowerInvariant());
    }

    public bool Exists(string chunkIdHex) => File.Exists(PathFor(chunkIdHex));

    /// <summary>
    /// Deletes a staged chunk blob. Used by batched staging to prune a
    /// batch's chunks after its upload is confirmed. Never deletes
    /// manifests, catalog, or journal — only chunk blobs.
    /// </summary>
    public void Delete(string chunkIdHex) =>
        BlobStoreIO.DeleteFile(PathFor(chunkIdHex));

    public void Put(string chunkIdHex, byte[] blob)
    {
        string path = PathFor(chunkIdHex);
        if (File.Exists(path))
            return;
        // Disguised destinations store header-wrapped blobs; plain ones raw.
        if (_layout is not null)
            blob = Service.DisguisedLayout.WrapContent(blob, path);

        BlobStoreIO.PutFile(path, blob);
    }

    /// <summary>
    /// Atomically replaces a stored blob: tmp-write + flush + rename
    /// with overwrite, so a crash leaves the old or the new blob,
    /// never a missing chunk. Used by offline rekey, where
    /// <see cref="Put(string, byte[])"/> alone is a dedup no-op over
    /// the existing chunk ID (W-12).
    /// </summary>
    public void Replace(string chunkIdHex, byte[] blob)
    {
        string path = PathFor(chunkIdHex);
        // Disguised destinations store header-wrapped blobs; plain ones raw.
        if (_layout is not null)
            blob = Service.DisguisedLayout.WrapContent(blob, path);

        BlobStoreIO.ReplaceFile(path, blob);
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

        BlobStoreIO.PutFile(path, blob);
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
