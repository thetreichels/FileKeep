using UsenetBackup.Core.Service;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Keeps Usenet backups alive as articles approach provider retention limits.
///
/// Three outcomes per backup:
///   1. Present and healthy — all sampled articles STAT-verified on the
///      server and retention remaining above threshold: no action.
///   2. Missing — one or more sampled articles absent: republish the missing
///      articles under NEW message IDs, STAT-verify each new article, update
///      the message index, and only then advance the retention timestamp.
///   3. Approaching expiration — articles present but retention remaining at
///      or below threshold: republish ALL articles under new message IDs
///      (servers reject duplicate IDs, so reposting the same ID does NOT
///      refresh retention), verify, update the index, then advance the
///      timestamp.
///
/// The "article" unit is the packed volume for volume-packed backups and
/// the chunk otherwise (see the manifest's volumes list).
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
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        CheckAndRepost(repo, _ => remote, warnDays, repostThresholdDays, sampleSize, dryRun,
            cancellationToken);

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
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        // P2-C7: the operation token used to be checked only between
        // REPOS by the caller — a stop during a long pass (hundreds of
        // STATs and reposts across many backups) waited for the whole
        // pass. The token is now checked per backup and per article.
        ArgumentNullException.ThrowIfNull(storeFactory);
        var report = new RetentionReport { DryRun = dryRun };
        var tracker = new UsenetUploadTracker(repo.RepoRoot);

        var expiring = tracker.GetExpiring(_getRetentionDays, warnDays);
        _log($"Retention check: {expiring.Count} backup(s) expiring within {warnDays} days" +
             (dryRun ? " (DRY RUN — no changes will be made)" : ""));

        foreach (var (record, expiresUtc, daysLeft) in expiring)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report.BackupsChecked++;
            try
            {
                // One store per provider record — never check provider B's
                // articles through provider A's connection.
                NntpBlobStore remote = storeFactory(record);
                var outcome = CheckBackup(repo, remote, tracker, record, daysLeft,
                    repostThresholdDays, sampleSize, report, dryRun, cancellationToken);
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
            catch (OperationCanceledException)
            {
                throw; // a stop is not a per-backup error (P2-C7)
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
        bool dryRun,
        CancellationToken cancellationToken = default)
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

        // The retention article unit: packed volumes when the backup was
        // uploaded with volume packing, individual chunks otherwise.
        bool volumeMode = manifest.Volumes is { Count: > 0 };
        List<string> articleIds = volumeMode
            ? manifest.Volumes!.Select(v => v.Id).ToList()
            : manifest.Files.SelectMany(f => f.Chunks).Distinct().ToList();
        Dictionary<string, IReadOnlyList<string>>? volumeChunks = volumeMode
            ? manifest.Volumes!.ToDictionary(
                v => v.Id, v => (IReadOnlyList<string>)v.ChunkIds)
            : null;

        if (articleIds.Count == 0)
        {
            _log($"Backup {record.BackupId}: no articles, skipping");
            return BackupOutcome.Healthy;
        }

        // Live STAT checks — never trust the local journal for retention.
        // SF-11: the sample window rotates daily per backup, so successive
        // checks sweep the whole list instead of re-checking the same
        // first window forever while the rest expires unobserved.
        var sample = SampleChunks(articleIds, sampleSize, StableSampleSeed(record.BackupId));
        var missingArticles = new List<string>();

        foreach (string articleId in sample)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report.ArticlesChecked++;
            bool exists;
            try
            {
                exists = remote.ExistsOnServer(articleId);
            }
            catch
            {
                exists = false; // Treat check failure as missing (conservative)
            }

            if (!exists)
            {
                missingArticles.Add(articleId);
                report.ArticlesMissing++;
            }
        }

        // SF-10: the manifest article is health-checked too. It used to
        // be invisible to retention: it could expire while every data
        // article refreshed, leaving the backup undiscoverable remotely
        // (discovery fetches the manifest by its deterministic ID).
        bool manifestMissing;
        report.ArticlesChecked++;
        try
        {
            manifestMissing = !remote.ManifestExistsOnServer(record.BackupId);
        }
        catch
        {
            manifestMissing = true; // conservative, like article STATs
        }
        if (manifestMissing)
            report.ArticlesMissing++;

        _log($"Backup {record.BackupId}: {sample.Count - missingArticles.Count}/{sample.Count} " +
             $"sampled articles present on server, {daysLeft} days retention remaining" +
             (manifestMissing ? "; manifest article MISSING" : ""));

        // Outcome 1: present and healthy — nothing to do.
        if (missingArticles.Count == 0 && !manifestMissing && daysLeft > repostThresholdDays)
            return BackupOutcome.Healthy;

        // Outcome 2 vs 3: missing articles, or merely approaching expiry.
        bool isMissing = missingArticles.Count > 0 || manifestMissing;
        // A missing sampled article proves the backup is degrading, but the
        // sample is only a window (default 10) into potentially hundreds of
        // articles. Republishing just the missing samples and then resetting
        // the backup-level timestamp would report a fresh backup age while
        // unexamined articles sit near expiry — the next scheduled check
        // would then defer action based on the dishonest timestamp.
        //
        // Correct behavior: any missing article triggers a FULL refresh.
        // Every article gets a fresh message identity and a fresh retention
        // clock, so the timestamp reset that follows is honest. Partial
        // refresh is never allowed to move the backup-level clock.
        List<string> toRepublish = articleIds;

        string reason = missingArticles.Count > 0
            ? $"{missingArticles.Count} sampled articles missing from server"
            : manifestMissing
                ? "manifest article missing from server"
                : $"only {daysLeft} days retention remaining (threshold: {repostThresholdDays})";
        _log($"{(dryRun ? "Would republish" : "Republishing")} {toRepublish.Count} article(s) " +
             $"for backup {record.BackupId}: {reason}");

        if (dryRun)
            return isMissing ? BackupOutcome.RefreshedMissing : BackupOutcome.RefreshedExpiring;

        // Republication must fully succeed before the retention clock moves.
        var failures = new List<string>();
        int republished = 0;
        foreach (string articleId in toRepublish)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // New message identity: the ONLY way to get a fresh retention
                // clock. The republish methods STAT-verify the new article
                // and update the message index before returning. Volume bytes
                // are rebuilt deterministically from local chunks — only the
                // message ID is new.
                string newMessageId;
                if (volumeMode)
                {
                    var chunkIds = volumeChunks![articleId];
                    byte[] volumeBytes;
                    if (chunkIds.Any(id => LocalChunkMissing(repo, id)))
                    {
                        // Some local chunks are gone (e.g. wiped after
                        // upload to save space). Volume bytes are
                        // deterministic, so a server download is
                        // byte-identical to a local rebuild.
                        _log($"  local chunks for volume {articleId[..12]}… missing, " +
                             "downloading volume from server for republish");
                        volumeBytes = remote.GetVolume(articleId);
                    }
                    else
                    {
                        // DL-7: verify every local blob (decrypt + hash)
                        // BEFORE packing. BuildVolume packs raw blobs with
                        // no integrity check, so a locally corrupted blob
                        // would otherwise be republished, the message index
                        // repointed to it, and the retention timestamp
                        // advanced — orphaning the healthy server article.
                        // A verification failure throws into the per-article
                        // failure handling below: nothing is posted for
                        // this volume, the index keeps the old identity,
                        // and the retention clock does not move.
                        foreach (string chunkId in chunkIds)
                            repo.VerifyChunk(chunkId);
                        volumeBytes = VolumePacker.BuildVolume(
                            chunkIds, repo.GetChunkBlob).Bytes;
                    }
                    newMessageId = remote.RepublishVolumeWithNewIdentity(articleId, volumeBytes);
                }
                else
                {
                    byte[] blob;
                    if (LocalChunkMissing(repo, articleId))
                    {
                        // SF-10: chunk mode had no server-download
                        // fallback (volume mode did), so for a pruned
                        // chunk-mode backup every republish threw on the
                        // missing local blob and the backup aged to
                        // expiry unrefreshed. Download the blob back —
                        // it is exactly the bytes republication posts.
                        _log($"  local chunk {articleId[..12]}… missing, " +
                             "downloading from server for republish");
                        blob = remote.Get(articleId);
                    }
                    else
                    {
                        // DL-7: the same guard as the volume path —
                        // verify the local blob before republishing it
                        // under a new identity, so a corrupted blob
                        // fails this article instead of replacing the
                        // healthy server copy.
                        repo.VerifyChunk(articleId);
                        blob = repo.GetChunkBlob(articleId);
                    }
                    newMessageId = remote.RepublishWithNewIdentity(articleId, blob);
                }
                republished++;
                report.ArticlesRepublished++;
                _log($"  republished {articleId[..12]}… as {newMessageId}");
            }
            catch (Exception ex)
            {
                failures.Add($"{articleId}: {ex.Message}");
                report.Errors.Add($"Republish {articleId}: {ex.Message}");
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
        // retention clock. A recovering machine (or WinRE) can only find
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

        // SF-10: refresh the discovery articles in the same transaction,
        // before the clock moves — a backup whose data is fresh but
        // whose manifest article expired is undiscoverable, which is no
        // better than expired data. The manifest is reposted only when
        // actually missing (its deterministic ID cannot be re-issued for
        // a present article); the single-set NFO is reposted the same
        // way; the monthly manifest index is republished as a new
        // version, which genuinely restarts its clock. A failure here
        // fails the refresh exactly like an index-publication failure.
        try
        {
            if (repo.RepublishManifest(record.BackupId, remote))
                _log("  reposted manifest article (was missing from server)");
            RepostNfoIfMissing(repo, remote, manifest, volumeMode);
            if (repo.RefreshManifestIndex(remote))
                _log("  republished monthly manifest index");
        }
        catch (Exception ex)
        {
            _log($"Backup {record.BackupId}: discovery-article refresh failed: " +
                 $"{ex.Message}; retention timestamp NOT advanced");
            report.Errors.Add($"Discovery refresh {record.BackupId}: {ex.Message}");
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
    /// True when a chunk's blob is not present in the local repository
    /// (both blob stores report a missing chunk as
    /// <see cref="InvalidDataException"/> from the size probe). Used to
    /// keep "pruned locally" (server download fallback) distinct from
    /// "present but corrupt" (verification failure) in the volume
    /// republish path.
    /// </summary>
    private static bool LocalChunkMissing(BackupRepository repo, string chunkId)
    {
        try
        {
            repo.GetChunkBlobSize(chunkId);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    /// <summary>
    /// SF-10: reposts the backup's NFO article if the server has lost
    /// it — only for the single-post-set shape (the non-batched upload
    /// path, and legacy manifests): one NFO whose text is rebuilt from
    /// the manifest exactly as the upload built it. Batched uploads
    /// post one NFO per staging batch with batch-specific dates and
    /// part counts the manifest does not record, so their NFOs cannot
    /// be faithfully rebuilt here and are skipped. The NFO is
    /// informational (newsreader metadata); remote discovery never
    /// reads it, so this is completeness, not recovery-critical.
    /// </summary>
    private void RepostNfoIfMissing(
        BackupRepository repo, NntpBlobStore remote, BackupManifest manifest, bool volumeMode)
    {
        if (!volumeMode || manifest.Volumes is not { Count: > 0 })
            return;
        int distinctSets = manifest.Volumes
            .Select(v => v.SetName).Distinct(StringComparer.Ordinal).Count();
        if (distinctSets > 1)
            return; // batched shape: per-batch NFOs are not reproducible here

        string nfoText = ArticleCodec.BuildNfoText(
            manifest.BackupId,
            manifest.CreatedUtc.ToString("yyyy-MM-dd"),
            manifest.Volumes.Count,
            repo.GetOrCreatePoster());
        if (remote.PostNfo(manifest.BackupId, batchIndex: 0, nfoText,
                ArticleCodec.SetName(manifest.BackupId), manifest.Volumes.Count + 1,
                recheckServer: true))
            _log("  reposted NFO article (was missing from server)");
    }

    /// <summary>
    /// FNV-1a over the backup ID and the current UTC day: a sample
    /// rotation seed that is stable within a day and across processes
    /// (unlike <see cref="HashCode"/>, which is per-process randomized),
    /// and different every day (SF-11).
    /// </summary>
    private static int StableSampleSeed(string backupId)
    {
        string s = backupId + ":" + DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
        unchecked
        {
            uint hash = 2166136261u;
            foreach (char c in s)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return (int)hash;
        }
    }

    /// <summary>
    /// Samples article IDs evenly across the list for STAT checks. The
    /// window is rotated by <paramref name="rotationSeed"/> (SF-11): a
    /// fixed window STAT-ed the same ≤sampleSize articles on every
    /// check forever, so an article outside it could expire unobserved
    /// while the backup reported Healthy.
    /// </summary>
    internal static List<string> SampleChunks(
        List<string> articleIds, int sampleSize, int rotationSeed = 0)
    {
        if (articleIds.Count <= sampleSize)
            return articleIds;

        var sample = new List<string>(sampleSize);
        double step = (double)articleIds.Count / sampleSize;
        int offset = ((rotationSeed % articleIds.Count) + articleIds.Count) % articleIds.Count;
        for (int i = 0; i < sampleSize; i++)
        {
            int idx = (offset + (int)(i * step)) % articleIds.Count;
            sample.Add(articleIds[idx]);
        }
        return sample;
    }
}
