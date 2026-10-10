namespace UsenetBackup.Core;

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
/// same size.
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
        public int ParitySkipped { get; set; }
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
        var result = new SyncResult();

        string chunksDir = Path.Combine(repoRoot, "chunks");
        string manifestsDir = Path.Combine(repoRoot, "manifests");
        string parityDir = Path.Combine(repoRoot, "parity");
        string repoJson = Path.Combine(repoRoot, "repo.json");

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
            else
            {
                result.ParitySkipped++;
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
    /// Copies src to dest if dest is missing or differs in size. Creates
    /// parent directories. Returns true if a copy happened.
    /// </summary>
    private static bool CopyIfNeeded(string src, string dest, out long bytesCopied)
    {
        var srcInfo = new FileInfo(src);
        bytesCopied = srcInfo.Length;
        var destInfo = new FileInfo(dest);
        if (destInfo.Exists && destInfo.Length == srcInfo.Length)
        {
            bytesCopied = 0;
            return false; // already in sync
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // Copy via temp + move for atomicity (a crash never leaves a half file
        // that a later sync would mistake for complete, since sizes differ).
        string tmp = dest + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dest, overwrite: true);
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
