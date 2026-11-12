namespace FileKeep.Core;

/// <summary>
/// <see cref="IBlobStore"/> that writes chunks directly to an SMB share,
/// in the same <c>chunks/&lt;hh&gt;/&lt;rest&gt;</c> layout as
/// <see cref="LocalBlobStore"/>. Used for streaming backups (e.g. whole-disk
/// images) where staging the full payload locally is infeasible — chunks go
/// straight to the network share as they are produced, with only a small
/// in-memory buffer held locally. The atomic write core, the already-exists
/// race check, and the path helpers live in <see cref="BlobStoreIO"/>
/// (shared with <see cref="LocalBlobStore"/>, D1); this store only supplies
/// its canonicalization (identity — see <see cref="PathFor"/>) and its layout.
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
        BlobStoreIO.ValidateChunkId(chunkIdHex);
        if (_layout is not null)
            return BlobStoreIO.DisguisedChunkPath(
                _share.Combine(_disguiseSubfolder ?? _layout.CoverName),
                _layout, chunkIdHex);
        // D1: identity canonicalization — the ID is used exactly as given.
        // Unlike LocalBlobStore this store never lowercases: shares written
        // by older builds, foreign tools, or manual copies may hold
        // mixed-case chunk files, and on a case-sensitive server lowercasing
        // here would orphan them (Exists/Get would miss, Put would write a
        // duplicate lowercase copy). The disguised branch is unaffected —
        // ChunkFileName lowercases internally.
        return BlobStoreIO.PlainChunkPath(_chunksDir, chunkIdHex,
            static id => id);
    }

    public bool Exists(string chunkIdHex) => File.Exists(PathFor(chunkIdHex));

    /// <summary>Deletes a stored blob. Used to replace a corrupt copy
    /// with a verified re-download (SF-13). Only "already gone" is
    /// swallowed: a delete that fails for permissions, a lock, or an
    /// I/O error is a real failure the caller must see — silently
    /// keeping a corrupt blob while the caller believes it was
    /// removed is worse than throwing (W-11). Callers that want
    /// best-effort pruning catch this themselves (see
    /// <c>BackupScheduler.PruneStagedChunks</c>).</summary>
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

        BlobStoreIO.PutFile(path, blob);
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
