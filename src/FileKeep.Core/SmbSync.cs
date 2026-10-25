namespace FileKeep.Core;

/// <summary>
/// Syncs a local backup repository to an SMB network share. Copies chunks
/// (content-addressed, so naturally idempotent — existing files are skipped),
/// manifests, parity files, and repo.json. The catalog database is NOT synced
/// (it's rebuildable locally, and SQLite over SMB has locking quirks).
///
/// The share ends up with a read-only-usable repo layout: a recovery wizard
/// can open it directly (chunks + manifests + repo.json are all that's needed
/// to list and restore backups).
///
/// Sync is resumable: interrupted runs pick up where they left off, since
/// every file copy is skipped when the destination already exists with the
/// same size and content (size alone is never trusted — DL-4).
/// </summary>
public sealed class SmbSync
{
    private readonly Action<string>? _log;

    public SmbSync(Action<string>? log = null)
    {
        _log = log;
    }

    public sealed class SyncResult
    {
        public int ChunksCopied { get; set; }
        public int ChunksSkipped { get; set; }
        public int ManifestsCopied { get; set; }
        public int ManifestsSkipped { get; set; }
        public int ParityCopied { get; set; }
        public long BytesCopied { get; set; }
    }

    /// <summary>
    /// Syncs the repo at <paramref name="repoRoot"/> to the connected share.
    /// <paramref name="progress"/> receives (filesDone, filesTotal) periodically.
    /// </summary>
    public SyncResult Sync(
        string repoRoot,
        SmbShare share,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(share);
        StorageGuard.EnsureSmbShareNotInRepo(repoRoot, share.UncPath);
        var result = new SyncResult();

        string chunksDir = Path.Combine(repoRoot, "chunks");
        string manifestsDir = Path.Combine(repoRoot, "manifests");
        string parityDir = Path.Combine(repoRoot, "parity");
        string repoJson = Path.Combine(repoRoot, "repo.json");

        // SF-19: refuse to "sync" a source that isn't a repository.
        // Without this, a wrong or stale repo path synced successfully
        // with 0 files — and even created an empty repo layout on the
        // share, making the wrong destination look backed up.
        if (!Directory.Exists(repoRoot))
            throw new DirectoryNotFoundException(
                $"Repository directory not found: '{repoRoot}'. Nothing was synced.");
        if (!File.Exists(repoJson))
            throw new InvalidDataException(
                $"'{repoRoot}' is not a FileKeep repository (missing repo.json). Nothing was synced.");

        // Enumerate everything first for progress reporting.
        var chunkFiles = Directory.Exists(chunksDir)
            ? Directory.GetFiles(chunksDir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        var manifestFiles = Directory.Exists(manifestsDir)
            ? Directory.GetFiles(manifestsDir, "*.json")
            : Array.Empty<string>();
        var parityFiles = Directory.Exists(parityDir)
            ? Directory.GetFiles(parityDir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        bool hasRepoJson = File.Exists(repoJson);
        int total = chunkFiles.Length + manifestFiles.Length + parityFiles.Length + (hasRepoJson ? 1 : 0);
        int done = 0;

        void Report()
        {
            progress?.Invoke(done, total);
        }

        string destChunks = share.Combine("chunks");
        string destManifests = share.Combine("manifests");
        string destParity = share.Combine("parity");
        Directory.CreateDirectory(destChunks);
        Directory.CreateDirectory(destManifests);
        if (parityFiles.Length > 0)
            Directory.CreateDirectory(destParity);

        foreach (string src in chunkFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(chunksDir, src);
            string dest = Path.Combine(destChunks, relative);
            if (CopyIfNeeded(src, dest, out long bytes))
            {
                result.ChunksCopied++;
                result.BytesCopied += bytes;
            }
            else
            {
                result.ChunksSkipped++;
            }
            done++;
            if (done % 25 == 0) Report();
        }

        foreach (string src in manifestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dest = Path.Combine(destManifests, Path.GetFileName(src));
            if (CopyIfNeeded(src, dest, out long bytes))
            {
                result.ManifestsCopied++;
                result.BytesCopied += bytes;
            }
            else
            {
                result.ManifestsSkipped++;
            }
            done++;
            if (done % 25 == 0) Report();
        }

        foreach (string src in parityFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(parityDir, src);
            string dest = Path.Combine(destParity, relative);
            if (CopyIfNeeded(src, dest, out long bytes))
            {
                result.ParityCopied++;
                result.BytesCopied += bytes;
            }
            done++;
            if (done % 25 == 0) Report();
        }

        if (hasRepoJson)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dest = share.Combine("repo.json");
            CopyIfNeeded(repoJson, dest, out long bytes);
            result.BytesCopied += bytes;
            done++;
        }
        Report();

        _log?.Invoke($"SMB sync to {share.UncPath}: {result.ChunksCopied} chunks copied " +
            $"({result.ChunksSkipped} already present), {result.ManifestsCopied} manifests, " +
            $"{result.ParityCopied} parity files, " +
            $"{FormatBytes(result.BytesCopied)} transferred.");
        return result;
    }

    /// <summary>
    /// Copies specific chunk blobs to the share (batched staging): each
    /// batch's chunks are copied right after their NNTP upload is
    /// confirmed, so the local copies can then be pruned. Every chunk is
    /// confirmed at the destination — present, with content matching the
    /// local blob — before this returns, because the caller prunes the
    /// local copies on this method's success: a chunk whose local blob
    /// is missing, or whose destination copy does not verify, throws
    /// rather than passing as delivered (DL-4). Returns the number
    /// copied (chunks already present and content-verified not counted).
    /// </summary>
    public int CopyChunks(IEnumerable<string> chunkIds, string repoRoot, SmbShare share)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(share);
        int copied = 0;
        string chunksDir = Path.Combine(repoRoot, "chunks");
        foreach (string id in chunkIds)
        {
            string src = Path.Combine(chunksDir, id[..2], id[2..]);
            if (!File.Exists(src))
                throw new FileNotFoundException(
                    $"Chunk {id} is missing from the local staging repo; " +
                    "it cannot be confirmed at the destination.", src);
            string dest = share.Combine("chunks", id[..2], id[2..]);
            if (CopyIfNeeded(src, dest, out _))
            {
                copied++;
                // Confirm the copy that just landed: a destination can
                // accept a write and still store different bytes.
                if (!FilesHaveSameContent(src, dest))
                    throw new IOException(
                        $"Chunk {id} did not verify at the destination after copying.");
            }
            // Not copied = already present with matching size AND hash
            // (CopyIfNeeded compared content) — confirmed.
        }
        return copied;
    }

    /// <summary>True when both files hold identical bytes (SHA-256).</summary>
    private static bool FilesHaveSameContent(string a, string b)
    {
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return string.Equals(Hashing.Sha256Hex(sa), Hashing.Sha256Hex(sb),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Copies src to dest if dest is missing or its content differs.
    /// Size alone is never trusted: a same-length destination counts as
    /// in sync only when its SHA-256 also matches, so a corrupt or stale
    /// same-size file is recopied, never "confirmed" (DL-4). Creates
    /// parent directories. Returns true if a copy happened.
    /// </summary>
    private static bool CopyIfNeeded(string src, string dest, out long bytesCopied)
    {
        var srcInfo = new FileInfo(src);
        bytesCopied = srcInfo.Length;
        var destInfo = new FileInfo(dest);
        if (destInfo.Exists && destInfo.Length == srcInfo.Length
            && FilesHaveSameContent(src, dest))
        {
            bytesCopied = 0;
            return false; // already in sync (size AND content)
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // Copy via temp + move for atomicity (a crash never leaves a half file
        // that a later sync would mistake for complete, since sizes differ).
        string tmp = dest + ".tmp-" + Guid.NewGuid().ToString("N"); // P2-C14: unique temp
        File.Copy(src, tmp, overwrite: true);
        try { File.Move(tmp, dest, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; } // P2-C15: don't leak the temp on failure
        return true;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GiB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MiB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KiB";
        return $"{bytes} B";
    }
}
