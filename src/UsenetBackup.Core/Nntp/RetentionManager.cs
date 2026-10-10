using UsenetBackup.Core.Service;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Keeps Usenet backups alive as articles approach provider retention limits.
///
/// Three outcomes per backup:
///   1. Present and healthy — all sampled articles STAT-verified on the
///      server and retention remaining above threshold: no action.
///   2. Missing — one or more sampled articles absent: republish the missing
///      chunks under NEW message IDs, STAT-verify each new article, update
///      the message index, and only then advance the retention timestamp.
///   3. Approaching expiration — articles present but retention remaining at
///      or below threshold: republish ALL chunks under new message IDs
///      (servers reject duplicate IDs, so reposting the same ID does NOT
///      refresh retention), verify, update the index, then advance the
///      timestamp.
///
/// The retention timestamp is advanced ONLY after every republished article
/// has been STAT-verified. A partial failure leaves the old timestamp in
/// place so the next run retries — FileKeep never claims retention it has
/// not proven.
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

    public enum BackupOutcome
    {
        Healthy,
        RefreshedMissing,
        RefreshedExpiring,
        Failed,
    }

    public sealed class RetentionReport
    {
        public int BackupsChecked { get; set; }
        public int BackupsHealthy { get; set; }
        public int BackupsRefreshed { get; set; }
        public int ArticlesChecked { get; set; }
        public int ArticlesMissing { get; set; }
        public int ArticlesRepublished { get; set; }
        public List<string> Errors { get; } = new();
        public bool DryRun { get; set; }
    }

    /// <summary>
    /// Checks retention health for all tracked uploads and refreshes
    /// articles that are missing or approaching expiry.
    /// </summary>
    /// <param name="repo">Local repository (source of chunks for republication).</param>
    /// <param name="remote">NNTP store for STAT checks and republication.</param>
    /// <param name="warnDays">Backups expiring within this many days are checked. Default 90.</param>
    /// <param name="repostThresholdDays">
    /// Backups with at most this many days of retention remaining are
    /// refreshed proactively. Default 30. Set to 0 to refresh only when
    /// articles are actually missing.
    /// </param>
    /// <param name="sampleSize">Articles to STAT-check per backup. Default 10.</param>
    /// <param name="dryRun">
    /// When true, reports what WOULD be done without posting anything,
    /// updating any index, or advancing any timestamp.
    /// </param>
    public RetentionReport CheckAndRepost(
        BackupRepository repo,
        NntpBlobStore remote,
        int warnDays = 90,
        int repostThresholdDays = 30,
        int sampleSize = 10,
        bool dryRun = false) =>
        CheckAndRepost(repo, _ => remote, warnDays, repostThresholdDays, sampleSize, dryRun);

    /// <summary>
    /// Checks retention health for all tracked uploads and refreshes
    /// articles that are missing or approaching expiry, resolving the
    /// NNTP store per tracker record.
    /// </summary>
    /// <param name="repo">Local repository (source of chunks for republication).</param>
    /// <param name="storeFactory">
    /// Resolves the NNTP store for a tracker record. Called once per
    /// expiring record and MUST return a store connected to that record's
    /// own provider: upload records are tracked per provider
    /// (backupId@host), so checking provider B's records through provider
    /// A's connection would refresh the wrong server while advancing B's
    /// timestamp. A record's timestamp advances only after its own
    /// provider's refresh succeeds. The manager takes no ownership of
    /// returned stores — the factory may return cached instances and the
    /// caller disposes them.
    /// </param>
    /// <param name="warnDays">Backups expiring within this many days are checked. Default 90.</param>
    /// <param name="repostThresholdDays">
    /// Backups with at most this many days of retention remaining are
    /// refreshed proactively. Default 30. Set to 0 to refresh only when
    /// articles are actually missing.
    /// </param>
    /// <param name="sampleSize">Articles to STAT-check per backup. Default 10.</param>
    /// <param name="dryRun">
    /// When true, reports what WOULD be done without posting anything,
    /// updating any index, or advancing any timestamp.
    /// </param>
    public RetentionReport CheckAndRepost(
        BackupRepository repo,
        Func<UsenetUploadTracker.UploadRecord, NntpBlobStore> storeFactory,
        int warnDays = 90,
        int repostThresholdDays = 30,
        int sampleSize = 10,
        bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(storeFactory);
        var report = new RetentionReport { DryRun = dryRun };
        var tracker = new UsenetUploadTracker(repo.RepoRoot);

        var expiring = tracker.GetExpiring(_getRetentionDays, warnDays);
        _log($"Retention check: {expiring.Count} backup(s) expiring within {warnDays} days" +
             (dryRun ? " (DRY RUN — no changes will be made)" : ""));

        foreach (var (record, expiresUtc, daysLeft) in expiring)
        {
            report.BackupsChecked++;
            try
            {
                // One store per provider record — never check provider B's
                // articles through provider A's connection.
                NntpBlobStore remote = storeFactory(record);
                var outcome = CheckBackup(repo, remote, tracker, record, daysLeft,
                    repostThresholdDays, sampleSize, report, dryRun);
                switch (outcome)
                {
                    case BackupOutcome.Healthy:
                        report.BackupsHealthy++;
                        break;
                    case BackupOutcome.RefreshedMissing:
                    case BackupOutcome.RefreshedExpiring:
                        report.BackupsRefreshed++;
                        break;
                    case BackupOutcome.Failed:
                        break; // error already recorded
                }
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{record.BackupId}@{record.ProviderHost}: {ex.Message}");
                _log($"Retention check failed for {record.BackupId}: {ex.Message}");
            }
        }

        _log($"Retention check complete: {report.BackupsHealthy} healthy, " +
             $"{report.BackupsRefreshed} refreshed, {report.ArticlesRepublished} articles republished, " +
             $"{report.Errors.Count} errors");
        return report;
    }

    private BackupOutcome CheckBackup(
        BackupRepository repo,
        NntpBlobStore remote,
        UsenetUploadTracker tracker,
        UsenetUploadTracker.UploadRecord record,
        int daysLeft,
        int repostThresholdDays,
        int sampleSize,
        RetentionReport report,
        bool dryRun)
    {
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
            return BackupOutcome.Healthy;
        }

        // Live STAT checks — never trust the local journal for retention.
        var sample = SampleChunks(chunkIds, sampleSize);
        var missingChunks = new List<string>();

        foreach (string chunkId in sample)
        {
            report.ArticlesChecked++;
            bool exists;
            try
            {
                exists = remote.ExistsOnServer(chunkId);
            }
            catch
            {
                exists = false; // Treat check failure as missing (conservative)
            }

            if (!exists)
            {
                missingChunks.Add(chunkId);
                report.ArticlesMissing++;
            }
        }

        _log($"Backup {record.BackupId}: {sample.Count - missingChunks.Count}/{sample.Count} " +
             $"sampled articles present on server, {daysLeft} days retention remaining");

        // Outcome 1: present and healthy — nothing to do.
        if (missingChunks.Count == 0 && daysLeft > repostThresholdDays)
            return BackupOutcome.Healthy;

        // Outcome 2 vs 3: missing articles, or merely approaching expiry.
        bool isMissing = missingChunks.Count > 0;
        // A missing sampled article proves the backup is degrading, but the
        // sample is only a window (default 10) into potentially hundreds of
        // articles. Republishing just the missing samples and then resetting
        // the backup-level timestamp would report a fresh backup age while
        // unexamined articles sit near expiry — the next scheduled check
        // would then defer action based on the dishonest timestamp.
        //
        // Correct behavior: any missing article triggers a FULL refresh.
        // Every chunk gets a fresh message identity and a fresh retention
        // clock, so the timestamp reset that follows is honest. Partial
        // refresh is never allowed to move the backup-level clock.
        List<string> toRepublish = chunkIds;

        string reason = isMissing
            ? $"{missingChunks.Count} sampled articles missing from server"
            : $"only {daysLeft} days retention remaining (threshold: {repostThresholdDays})";
        _log($"{(dryRun ? "Would republish" : "Republishing")} {toRepublish.Count} article(s) " +
             $"for backup {record.BackupId}: {reason}");

        if (dryRun)
            return isMissing ? BackupOutcome.RefreshedMissing : BackupOutcome.RefreshedExpiring;

        // Republication must fully succeed before the retention clock moves.
        var failures = new List<string>();
        int republished = 0;
        foreach (string chunkId in toRepublish)
        {
            try
            {
                byte[] blob = repo.GetChunkBlob(chunkId);
                // New message identity: the ONLY way to get a fresh retention
                // clock. RepublishWithNewIdentity STAT-verifies the new
                // article and updates the message index before returning.
                string newMessageId = remote.RepublishWithNewIdentity(chunkId, blob);
                republished++;
                report.ArticlesRepublished++;
                _log($"  republished {chunkId[..12]}… as {newMessageId}");
            }
            catch (Exception ex)
            {
                failures.Add($"{chunkId}: {ex.Message}");
                report.Errors.Add($"Republish {chunkId}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            // Do NOT advance the timestamp — the backup is not fully
            // refreshed. The old timestamp stands so the next run retries.
            _log($"Backup {record.BackupId}: {failures.Count}/{toRepublish.Count} republications " +
                 $"failed; retention timestamp NOT advanced");
            return BackupOutcome.Failed;
        }

        // Publish the updated message-identity index BEFORE advancing the
        // retention clock. A recovering machine (or WinPE) can only find
        // the refreshed articles through this index — if publication fails
        // while the clock has already advanced, cross-machine recovery
        // would use stale message IDs while the retention timestamp
        // claims success. The index publication is therefore part of the
        // required transaction: it must succeed before the clock moves.
        try
        {
            string indexJson = repo.MessageIndex.ToJson();
            int version = remote.PostMessageIndex(indexJson);
            _log($"  published message-identity index v{version}");
        }
        catch (Exception ex)
        {
            // Do NOT advance the timestamp — the refreshed identities are
            // not discoverable by other machines. The old timestamp stands
            // so the next run retries the full refresh including index
            // publication. (Republishing already-posted chunks again is
            // wasteful but safe: servers accept new message IDs and the
            // older articles expire naturally.)
            _log($"Backup {record.BackupId}: message-identity index publication failed: " +
                 $"{ex.Message}; retention timestamp NOT advanced");
            report.Errors.Add($"Index publish {record.BackupId}: {ex.Message}");
            return BackupOutcome.Failed;
        }

        // All republished articles STAT-verified AND the refreshed identity
        // index published — now it is safe to advance the retention clock.
        tracker.RecordUpload(record.BackupId, record.ProviderHost, record.Newsgroup);
        _log($"Backup {record.BackupId}: {republished}/{toRepublish.Count} articles republished, " +
             $"verified, and index published; retention clock reset");

        return isMissing ? BackupOutcome.RefreshedMissing : BackupOutcome.RefreshedExpiring;
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
