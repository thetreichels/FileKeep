using UsenetBackup.Core.Service;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Keeps Usenet backups alive as articles approach provider retention limits.
///
/// Lifecycle:
///   1. Scan upload tracker for backups approaching expiry
///   2. STAT-check a sample of articles to verify actual availability
///      (provider retention is advertised, not guaranteed)
///   3. Repost missing/expired articles from local chunks
///      (message-IDs are deterministic, so reposting refreshes in place —
///      no manifest index updates needed)
///   4. Update the upload tracker with the new upload date
///
/// This is a first-class subsystem with its own schedule, independent of
/// backup jobs. It is the component that realizes FileKeep's original
/// vision: "use Usenet's long retention as a backup cloud and keep the
/// backup alive."
/// </summary>
public sealed class RetentionManager
{
    private readonly Action<string> _log;
    private readonly Func<string, int> _getRetentionDays;

    /// <param name="log">Log sink.</param>
    /// <param name="getRetentionDays">Maps provider host → retention days.</param>
    public RetentionManager(Action<string> log, Func<string, int> getRetentionDays)
    {
        _log = log;
        _getRetentionDays = getRetentionDays;
    }

    public sealed class RetentionReport
    {
        public int BackupsChecked { get; set; }
        public int BackupsHealthy { get; set; }
        public int BackupsReposted { get; set; }
        public int ArticlesChecked { get; set; }
        public int ArticlesMissing { get; set; }
        public int ArticlesReposted { get; set; }
        public List<string> Errors { get; } = new();
    }

    /// <summary>
    /// Checks retention health for all tracked uploads and reposts
    /// articles that are missing or approaching expiry.
    /// </summary>
    /// <param name="repo">Local repository (source of chunks for reposting).</param>
    /// <param name="remote">NNTP store for STAT checks and reposting.</param>
    /// <param name="warnDays">
    /// Backups expiring within this many days are checked. Default 90.
    /// </param>
    /// <param name="repostThresholdDays">
    /// Backups with fewer than this many days of retention remaining are
    /// reposted proactively. Default 30. Set to 0 to repost only when
    /// articles are actually missing.
    /// </param>
    /// <param name="sampleSize">
    /// Number of articles to STAT-check per backup. Default 10.
    /// Checks are spread across the chunk list.
    /// </param>
    public RetentionReport CheckAndRepost(
        BackupRepository repo,
        NntpBlobStore remote,
        int warnDays = 90,
        int repostThresholdDays = 30,
        int sampleSize = 10)
    {
        var report = new RetentionReport();
        var tracker = new UsenetUploadTracker(repo.RepoRoot);

        var expiring = tracker.GetExpiring(_getRetentionDays, warnDays);
        _log($"Retention check: {expiring.Count} backup(s) expiring within {warnDays} days");

        foreach (var (record, expiresUtc, daysLeft) in expiring)
        {
            report.BackupsChecked++;
            try
            {
                bool reposted = CheckBackup(repo, remote, tracker, record, daysLeft,
                    repostThresholdDays, sampleSize, report);
                if (reposted)
                    report.BackupsReposted++;
                else
                    report.BackupsHealthy++;
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{record.BackupId}@{record.ProviderHost}: {ex.Message}");
                _log($"Retention check failed for {record.BackupId}: {ex.Message}");
            }
        }

        _log($"Retention check complete: {report.BackupsHealthy} healthy, " +
             $"{report.BackupsReposted} reposted, {report.ArticlesReposted} articles reposted, " +
             $"{report.Errors.Count} errors");
        return report;
    }

    private bool CheckBackup(
        BackupRepository repo,
        NntpBlobStore remote,
        UsenetUploadTracker tracker,
        UsenetUploadTracker.UploadRecord record,
        int daysLeft,
        int repostThresholdDays,
        int sampleSize,
        RetentionReport report)
    {
        // Load the manifest to get the chunk list
        BackupManifest manifest;
        try
        {
            manifest = repo.LoadManifest(record.BackupId);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot load manifest for retention check: {ex.Message}", ex);
        }

        var chunkIds = manifest.Files
            .SelectMany(f => f.Chunks)
            .Distinct()
            .ToList();

        if (chunkIds.Count == 0)
        {
            _log($"Backup {record.BackupId}: no chunks, skipping");
            return false;
        }

        // Sample articles across the chunk list for STAT checks
        var sample = SampleChunks(chunkIds, sampleSize);
        int missing = 0;

        foreach (string chunkId in sample)
        {
            report.ArticlesChecked++;
            bool exists;
            try
            {
                exists = remote.Exists(chunkId);
            }
            catch
            {
                exists = false; // Treat check failure as missing (conservative)
            }

            if (!exists)
            {
                missing++;
                report.ArticlesMissing++;
            }
        }

        _log($"Backup {record.BackupId}: {sample.Count - missing}/{sample.Count} sampled articles present, " +
             $"{daysLeft} days retention remaining");

        // Decide: repost if articles are actually missing, or if we're
        // within the proactive repost threshold
        bool shouldRepost = missing > 0 || daysLeft <= repostThresholdDays;
        if (!shouldRepost)
            return false;

        string reason = missing > 0
            ? $"{missing} sampled articles missing"
            : $"only {daysLeft} days retention remaining (threshold: {repostThresholdDays})";
        _log($"Reposting backup {record.BackupId}: {reason}");

        // Repost all chunks (not just the sample — if some are gone,
        // others may follow)
        int reposted = 0;
        foreach (string chunkId in chunkIds)
        {
            try
            {
                // Get the chunk bytes from local storage
                byte[] blob = repo.GetChunkBlob(chunkId);
                // Repost (Put is idempotent; skips if already present)
                remote.Put(chunkId, blob);
                reposted++;
                report.ArticlesReposted++;
            }
            catch (Exception ex)
            {
                report.Errors.Add($"Repost {chunkId}: {ex.Message}");
            }
        }

        // Update the upload tracker with the new upload date
        // (this resets the retention clock)
        tracker.RecordUpload(record.BackupId, record.ProviderHost, record.Newsgroup);

        _log($"Reposted {reposted}/{chunkIds.Count} articles for backup {record.BackupId}");
        return true;
    }

    /// <summary>
    /// Samples chunk IDs evenly across the list for STAT checks.
    /// </summary>
    private static List<string> SampleChunks(List<string> chunkIds, int sampleSize)
    {
        if (chunkIds.Count <= sampleSize)
            return chunkIds;

        var sample = new List<string>(sampleSize);
        double step = (double)chunkIds.Count / sampleSize;
        for (int i = 0; i < sampleSize; i++)
        {
            int idx = (int)(i * step);
            sample.Add(chunkIds[idx]);
        }
        return sample;
    }
}
