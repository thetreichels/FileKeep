namespace UsenetBackup.Core.Service;

/// <summary>
/// Clock abstraction so the scheduler is unit-testable.
/// </summary>
public interface IClock
{
    DateTime Now { get; } // local time: schedules are expressed in local time
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime Now => DateTime.Now;
}

/// <summary>
/// A parsed job schedule: either daily at a wall-clock time, or every N minutes.
/// </summary>
public abstract class Schedule
{
    public abstract DateTime NextAfter(DateTime now);
    public abstract override string ToString();

    public sealed class Daily : Schedule
    {
        public int Hour { get; }
        public int Minute { get; }
        public Daily(int hour, int minute) { Hour = hour; Minute = minute; }

        public override DateTime NextAfter(DateTime now)
        {
            var today = new DateTime(now.Year, now.Month, now.Day, Hour, Minute, 0, now.Kind);
            return today > now ? today : today.AddDays(1);
        }

        public override string ToString() => $"daily {Hour:D2}:{Minute:D2}";
    }

    public sealed class Interval : Schedule
    {
        public int Minutes { get; }
        public Interval(int minutes) { Minutes = minutes; }

        public override DateTime NextAfter(DateTime now) => now.AddMinutes(Minutes);
        public override string ToString() => $"interval {Minutes}";
    }
}

/// <summary>
/// Parses the <c>schedule</c> field of a job: <c>"daily HH:mm"</c> or
/// <c>"interval N"</c> (minutes).
/// </summary>
public static class ScheduleParser
{
    public static Schedule Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(
                "Job schedule is empty (expected \"daily HH:mm\" or \"interval N\").");
        string[] parts = text.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0].Equals("daily", StringComparison.OrdinalIgnoreCase))
        {
            string[] hm = parts[1].Split(':');
            if (hm.Length == 2 &&
                int.TryParse(hm[0], out int h) && h is >= 0 and < 24 &&
                int.TryParse(hm[1], out int m) && m is >= 0 and < 60)
                return new Schedule.Daily(h, m);
            throw new InvalidOperationException(
                $"Invalid daily schedule '{text}' (expected \"daily HH:mm\", e.g. \"daily 02:00\").");
        }
        if (parts.Length == 2 && parts[0].Equals("interval", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1], out int minutes) && minutes > 0)
            return new Schedule.Interval(minutes);
        throw new InvalidOperationException(
            $"Invalid schedule '{text}' (expected \"daily HH:mm\" or \"interval N\" minutes).");
    }
}

/// <summary>
/// Outcome of one job run, kept in memory and served by the dashboard.
/// </summary>
public sealed class JobRunResult
{
    public required string JobName { get; init; }
    public required DateTime StartedLocal { get; init; }
    public required bool Success { get; init; }
    public string? BackupId { get; init; }
    public int Files { get; init; }
    public string? Error { get; init; }
    /// <summary>
    /// True when the backup succeeded but auto-upload to Usenet failed.
    /// The backup is safe locally; Usenet copy is missing.
    /// </summary>
    public bool AutoUploadFailed { get; init; }
    public string? AutoUploadError { get; init; }
    /// <summary>
    /// True when auto-verify ran and found corruption. The backup may be
    /// damaged — investigate immediately.
    /// </summary>
    public bool VerifyFailed { get; init; }
    public string? VerifyError { get; init; }
}

/// <summary>
/// Mutable per-job runtime state shared between the scheduler and the dashboard.
/// </summary>
public sealed class JobState
{
    public BackupJobConfig Config { get; }
    public Schedule Schedule { get; }
    public DateTime NextRunLocal { get; set; }
    public JobRunResult? LastResult { get; set; }
    public int ConsecutiveFailures { get; set; }

    public JobState(BackupJobConfig config, Schedule schedule, DateTime nextRunLocal)
    {
        Config = config;
        Schedule = schedule;
        NextRunLocal = nextRunLocal;
    }
}

/// <summary>
/// Runs backup jobs on their schedules. Jobs execute sequentially in schedule
/// order; a failing job is logged and retried at its next scheduled time.
/// </summary>
public sealed class BackupScheduler
{
    public const string PassphraseEnvVar = "USENETBACKUP_PASSPHRASE";
    public const string NntpPasswordEnvVar = "USENETBACKUP_NNTP_PASSWORD";
    public const string SmbPasswordEnvVar = "USENETBACKUP_SMB_PASSWORD";

    private readonly List<JobState> _jobs;
    private readonly IClock _clock;
    private readonly TimeSpan _pollInterval;
    private readonly Action<string>? _log;
    private readonly object _runGate = new(); // runs never overlap
    private IReadOnlyList<NntpConfig> _nntpProviders = Array.Empty<NntpConfig>();
    private ServiceConfig _config;
    private DateTime _lastRetentionCheck = DateTime.MinValue;

    /// <summary>
    /// Currently active upload progress tracker, or null if no upload in progress.
    /// Used by the dashboard to show progress bar and current speed.
    /// </summary>
    public UploadProgressTracker? ActiveUpload { get; private set; }

    public IReadOnlyList<JobState> Jobs
    {
        get
        {
            lock (_runGate)
                return _jobs.ToList();
        }
    }
    public DateTime StartedLocal { get; }

    public BackupScheduler(ServiceConfig config, IClock? clock = null,
        TimeSpan? pollInterval = null, Action<string>? log = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        _log = log;
        StartedLocal = _clock.Now;
        _config = config;
        _nntpProviders = config.EffectiveProviders;
        _jobs = config.Jobs.Select(j =>
        {
            var schedule = ScheduleParser.Parse(j.Schedule);
            return new JobState(j, schedule, schedule.NextAfter(_clock.Now));
        }).ToList();
    }

    /// <summary>
    /// Replaces the job list with a new configuration (from the Settings UI).
    /// Existing run state (last results, failure counts) is preserved for jobs
    /// whose names match; new jobs start fresh.
    /// </summary>
    public void ReloadJobs(ServiceConfig config)
    {
        lock (_runGate)
        {
            var now = _clock.Now;
            _nntpProviders = config.EffectiveProviders;
            _config = config;
            var oldByName = _jobs.ToDictionary(j => j.Config.Name,
                StringComparer.OrdinalIgnoreCase);
            _jobs.Clear();
            foreach (var jc in config.Jobs)
            {
                var schedule = ScheduleParser.Parse(jc.Schedule);
                var state = new JobState(jc, schedule, schedule.NextAfter(now));
                // Preserve run history for unchanged jobs.
                if (oldByName.TryGetValue(jc.Name, out var old))
                {
                    state.LastResult = old.LastResult;
                    state.ConsecutiveFailures = old.ConsecutiveFailures;
                    // Keep the earlier next-run if the schedule didn't change.
                    if (string.Equals(old.Config.Schedule, jc.Schedule,
                            StringComparison.Ordinal) && old.NextRunLocal > now)
                        state.NextRunLocal = old.NextRunLocal;
                }
                _jobs.Add(state);
            }
            Log($"reloaded {_jobs.Count} job(s) from configuration");
        }
    }

    /// <summary>
    /// Main loop: runs until <paramref name="stopping"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        Log("scheduler started");
        while (!stopping.IsCancellationRequested)
        {
            RunDueJobs(_clock.Now);
            RunRetentionIfDue(_clock.Now);
            try { await Task.Delay(_pollInterval, stopping); }
            catch (OperationCanceledException) { break; }
        }
        Log("scheduler stopped");
    }

    /// <summary>
    /// Runs retention checks if enabled and the interval has elapsed.
    /// Exposed for tests.
    /// </summary>
    public void RunRetentionIfDue(DateTime now)
    {
        if (!_config.RetentionEnabled)
            return;
        if (_config.RetentionCheckIntervalHours <= 0)
            return;

        TimeSpan interval = TimeSpan.FromHours(_config.RetentionCheckIntervalHours);
        lock (_runGate)
        {
            if (now - _lastRetentionCheck < interval)
                return;
            _lastRetentionCheck = now;
        }

        try
        {
            RunRetentionChecks();
        }
        catch (Exception ex)
        {
            Log($"retention check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Manual retention check for a single repo (dashboard operation).
    /// </summary>
    public void RunRetentionCheck(string repoPath, bool dryRun)
    {
        var providers = _nntpProviders;
        if (providers.Count == 0)
            throw new InvalidOperationException("No NNTP providers configured.");
        RunRetentionForRepo(repoPath, providers, dryRun);
    }

    /// <summary>
    /// Manual NNTP upload of a backup (dashboard operation).
    /// </summary>
    public void UploadBackup(string repoPath, string backupId)
    {
        string passphrase = Environment.GetEnvironmentVariable(PassphraseEnvVar) ?? "";
        if (string.IsNullOrEmpty(passphrase))
            throw new InvalidOperationException($"No passphrase (set {PassphraseEnvVar}).");
        // Reuse the per-job upload path with a synthetic job config.
        var job = new BackupJobConfig
        {
            Name = "manual-upload",
            Repo = repoPath,
            Source = "",
            AutoUpload = true,
        };
        UploadToTargets(job, backupId, passphrase);
    }

    /// <summary>
    /// Downloads chunks referenced by an NZB file from the first configured provider.
    /// </summary>
    public void DownloadFromNzb(string repoPath, string nzbPath, string passphrase)
    {
        var providers = _nntpProviders;
        if (providers.Count == 0)
            throw new InvalidOperationException("No NNTP providers configured.");
        var nzb = Nntp.NzbParser.ParseFile(nzbPath);
        if (nzb.Files.Count == 0)
            throw new InvalidOperationException("NZB contains no files.");
        var nntp = providers[0];
        string? nntpPassword = Environment.GetEnvironmentVariable(NntpPasswordEnvVar);
        if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(nntp.PasswordProtected))
            nntpPassword = Dpapi.Unprotect(nntp.PasswordProtected);

        using var repo = BackupRepository.Open(repoPath, passphrase);
        using var client = new Nntp.NntpClient(nntp.Host, nntp.Port, nntp.Ssl);
        client.Connect();
        if (!string.IsNullOrEmpty(nntp.Username))
            client.Authenticate(nntp.Username, nntpPassword ?? "");
        try
        {
            string providerKey = Nntp.ChunkMessageIndex.MakeProviderKey(nntp.Host, nntp.Newsgroup);
            using var remote = new Nntp.NntpBlobStore(
                client, nntp.Newsgroup, repo.RepoId, repo.CatalogPath,
                messageIndex: repo.MessageIndex, providerKey: providerKey);
            // Volume articles are volume-sized, not chunk-sized.
            remote.MaxArticleBytes = Math.Max(repo.MaxDownloadBytes, repo.VolumeSizeBytes * 2);
            // Fetch the latest published message-identity index before
            // downloading (retention may have refreshed article IDs).
            // Throws an explicit recovery error if an index was published
            // but cannot be retrieved or parsed — never silently falls back.
            remote.SyncMessageIndex(repo.MessageIndex);
            repo.DownloadNzb(nzb, remote, (done, total) => { });
        }
        finally
        {
            try { client.Quit(); } catch { }
        }
    }

    /// <summary>
    /// Runs RetentionManager.CheckAndRepost for each job's repository.
    /// </summary>
    private void RunRetentionChecks()
    {
        var providers = _nntpProviders;
        if (providers.Count == 0)
        {
            Log("retention check skipped: no NNTP providers configured");
            return;
        }

        // Group jobs by repo to avoid checking the same repo twice
        var repos = _jobs.Select(j => j.Config.Repo).Distinct().ToList();
        foreach (string repoPath in repos)
        {
            try
            {
                RunRetentionForRepo(repoPath, providers);
            }
            catch (Exception ex)
            {
                Log($"retention check failed for repo '{repoPath}': {ex.Message}");
            }
        }
    }

    private void RunRetentionForRepo(string repoPath, IReadOnlyList<NntpConfig> providers, bool dryRun = false)
    {
        string passphrase = Environment.GetEnvironmentVariable(PassphraseEnvVar) ?? "";
        if (string.IsNullOrEmpty(passphrase))
        {
            Log($"retention check skipped for '{repoPath}': no passphrase (set {PassphraseEnvVar})");
            return;
        }

        using var repo = BackupRepository.Open(repoPath, passphrase);
        // Share the repo's index instance with every per-provider store:
        // republication records new identities into this instance, and
        // CheckBackup publishes repo.MessageIndex — they must be the same
        // object, otherwise a second provider's refresh would publish a
        // stale index missing its new identities.
        var messageIndex = repo.MessageIndex;

        // Retention records are tracked per provider (backupId@host), so
        // each record must be checked and refreshed through its OWN
        // provider's connection: one pool+store per provider host, created
        // lazily and disposed at the end of the run. A record whose
        // provider is no longer configured fails visibly (recorded as an
        // error) instead of being silently checked through another
        // provider — which would refresh the wrong server while advancing
        // the missing provider's timestamp.
        var stores = new Dictionary<string, (Nntp.NntpConnectionPool Pool, Nntp.NntpBlobStore Store)>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            Nntp.NntpBlobStore StoreFor(Nntp.UsenetUploadTracker.UploadRecord record)
            {
                if (stores.TryGetValue(record.ProviderHost, out var entry))
                    return entry.Store;

                // Prefer an exact host+newsgroup match; fall back to host-only.
                NntpConfig? nntp = providers.FirstOrDefault(p =>
                        string.Equals(p.Host, record.ProviderHost, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.Newsgroup, record.Newsgroup, StringComparison.OrdinalIgnoreCase))
                    ?? providers.FirstOrDefault(p =>
                        string.Equals(p.Host, record.ProviderHost, StringComparison.OrdinalIgnoreCase));
                if (nntp is null)
                    throw new InvalidOperationException(
                        $"No NNTP provider configured for host '{record.ProviderHost}' " +
                        $"(backup {record.BackupId}). Retention for that provider cannot be " +
                        "checked; configure the provider or remove its upload records.");

                string? nntpPassword = Environment.GetEnvironmentVariable(NntpPasswordEnvVar);
                if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(nntp.PasswordProtected))
                    nntpPassword = Dpapi.Unprotect(nntp.PasswordProtected);

                var pool = new Nntp.NntpConnectionPool(
                    nntp.Host, nntp.Port, nntp.Ssl,
                    username: nntp.Username, password: nntpPassword,
                    size: nntp.Connections);
                var store = new Nntp.NntpBlobStore(
                    pool, nntp.Newsgroup, repo.RepoId, repo.CatalogPath,
                    messageIndex: messageIndex,
                    providerKey: Nntp.ChunkMessageIndex.MakeProviderKey(nntp.Host, nntp.Newsgroup));
                stores[record.ProviderHost] = (pool, store);
                return store;
            }

            var manager = new Nntp.RetentionManager(
                msg => Log($"retention: {msg}"),
                host => providers.FirstOrDefault(p =>
                        string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase))
                    ?.RetentionDays ?? 1095);

            var report = manager.CheckAndRepost(
                repo, StoreFor,
                warnDays: _config.RetentionWarnDays,
                repostThresholdDays: _config.RetentionRepostThresholdDays,
                dryRun: dryRun);

            Log($"retention check complete for '{repoPath}': {report.BackupsHealthy} healthy, " +
                $"{report.BackupsRefreshed} refreshed, {report.Errors.Count} errors");
        }
        finally
        {
            foreach (var (pool, store) in stores.Values)
            {
                try { store.Dispose(); } catch { /* best effort */ }
                try { pool.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Runs every job whose next-run time has passed. Exposed for tests.
    /// </summary>
    public void RunDueJobs(DateTime now)
    {
        List<JobState> due;
        lock (_runGate)
            due = _jobs.Where(j => j.NextRunLocal <= now).OrderBy(j => j.NextRunLocal).ToList();
        foreach (var job in due)
            RunOne(job, now);
    }

    /// <summary>
    /// Runs a single job immediately, outside its schedule (dashboard "run now").
    /// </summary>
    public JobRunResult RunJobNow(string name)
    {
        JobState job;
        lock (_runGate)
        {
            job = _jobs.FirstOrDefault(j => j.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown job '{name}'.");
            // Hold the lock for the entire run so a concurrent ReloadJobs
            // can't swap the job list out from under us.
            var result = Execute(job.Config, _clock.Now);
            job.LastResult = result;
            job.ConsecutiveFailures = result.Success ? 0 : job.ConsecutiveFailures + 1;
            // Re-anchor the schedule so "run now" doesn't cause an immediate re-run.
            job.NextRunLocal = job.Schedule.NextAfter(_clock.Now);
            return result;
        }
    }

    private void RunOne(JobState job, DateTime now)
    {
        JobRunResult result;
        lock (_runGate)
            result = Execute(job.Config, now);
        job.LastResult = result;
        job.ConsecutiveFailures = result.Success ? 0 : job.ConsecutiveFailures + 1;
        job.NextRunLocal = job.Schedule.NextAfter(now);
    }

    private JobRunResult Execute(BackupJobConfig job, DateTime now)
    {
        Log($"job '{job.Name}': starting");
        try
        {
            string passphrase = Environment.GetEnvironmentVariable(PassphraseEnvVar) ?? "";
            if (string.IsNullOrEmpty(passphrase))
                throw new InvalidOperationException(
                    $"Cannot run scheduled backup without a passphrase: set the {PassphraseEnvVar} " +
                    "environment variable for the service account.");

            using var repo = BackupRepository.Open(job.Repo, passphrase);
            using ISnapshotProvider? snap = CreateSnapshotProvider(job);

            BackupManifest manifest;
            try
            {
                if (job.Mode == "full")
                {
                    manifest = repo.BackupDirectory(job.Source, snap);
                }
                else
                {
                    string? parent = repo.ListBackups()
                        .OrderByDescending(b => b.CreatedUtc)
                        .FirstOrDefault()?.BackupId;
                    manifest = parent is null
                        ? repo.BackupDirectory(job.Source, snap)
                        : repo.BackupIncremental(job.Source, parent, snap,
                            VerificationModeParser.Parse(job.VerificationMode));
                }
                // VSS: writer session finalized only after data is safely stored.
                if (snap is Recovery.VssSnapshot vss)
                    vss.Complete();
            }
            catch
            {
                // VssSnapshot.Dispose aborts the writer session fail-safe.
                throw;
            }

            OperationLog.Append(job.Repo, "scheduled-backup",
                $"job={job.Name} id={manifest.BackupId} type={manifest.Type} files={manifest.Files.Count}");
            Log($"job '{job.Name}': {manifest.Type} backup {manifest.BackupId} ({manifest.Files.Count} files)");

            // Auto-upload to configured targets (nntp and/or smb) if any.
            bool autoUploadFailed = false;
            string? autoUploadError = null;
            if (job.EffectiveTargets.Count > 0)
            {
                try { UploadToTargets(job, manifest.BackupId, passphrase); }
                catch (Exception ex)
                {
                    // Upload failure doesn't fail the backup itself, but it's tracked
                    // in the result so the dashboard can warn the user.
                    autoUploadFailed = true;
                    autoUploadError = ex.Message;
                    Log($"job '{job.Name}': auto-upload FAILED: {ex.Message}");
                    OperationLog.Append(job.Repo, "auto-upload-failed",
                        $"job={job.Name} id={manifest.BackupId} error={ex.Message}");
                }
            }

            // Auto-verify if configured (default: after each backup).
            bool verifyFailed = false;
            string? verifyError = null;
            if (job.AutoVerify)
            {
                try
                {
                    Log($"job '{job.Name}': auto-verifying {manifest.BackupId}...");
                    repo.Verify(manifest.BackupId);
                    Log($"job '{job.Name}': auto-verify OK.");
                    OperationLog.Append(job.Repo, "auto-verify",
                        $"job={job.Name} id={manifest.BackupId} result=ok");
                }
                catch (Exception ex)
                {
                    verifyFailed = true;
                    verifyError = ex.Message;
                    Log($"job '{job.Name}': auto-verify FAILED: {ex.Message}");
                    OperationLog.Append(job.Repo, "auto-verify-failed",
                        $"job={job.Name} id={manifest.BackupId} error={ex.Message}");
                }
            }

            return new JobRunResult
            {
                JobName = job.Name,
                StartedLocal = now,
                Success = true,
                BackupId = manifest.BackupId,
                Files = manifest.Files.Count,
                AutoUploadFailed = autoUploadFailed,
                AutoUploadError = autoUploadError,
                VerifyFailed = verifyFailed,
                VerifyError = verifyError,
            };
        }
        catch (Exception ex)
        {
            TryAppendFailureLog(job, ex);
            Log($"job '{job.Name}': FAILED: {ex.Message}");
            return new JobRunResult
            {
                JobName = job.Name,
                StartedLocal = now,
                Success = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// The operations log is informational only: a failure to write it
    /// (e.g. the repo path itself is bad) must never mask the real error.
    /// </summary>
    private static void TryAppendFailureLog(BackupJobConfig job, Exception ex)
    {
        try
        {
            OperationLog.Append(job.Repo, "scheduled-backup-failed",
                $"job={job.Name} error={ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Creates the snapshot provider for a job: VSS shadow copy, backup
    /// privilege, or null for a live read. VSS and backup-privilege are
    /// mutually exclusive (validated at config load).
    /// </summary>
    private static ISnapshotProvider? CreateSnapshotProvider(BackupJobConfig job)
    {
        if (job.Vss && job.BackupPrivilege)
            throw new InvalidOperationException(
                $"Job '{job.Name}': 'vss' and 'backupPrivilege' are mutually exclusive.");
        if (job.BackupPrivilege)
            return new BackupPrivilegeSnapshotProvider(job.Source);
        if (job.Vss)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    $"Job '{job.Name}': VSS snapshots require Windows.");
            string helper = LocateVssHelper();
            string volume = Path.GetPathRoot(Path.GetFullPath(job.Source))
                ?? throw new InvalidOperationException(
                    $"Job '{job.Name}': cannot determine volume for '{job.Source}'.");
            var vss = new Recovery.VssSnapshot(helper);
            vss.Create(volume, job.Source);
            return vss;
        }
        return null;
    }

    /// <summary>
    /// Locates FileKeepVss.exe. The service runs from service/ while the
    /// helper ships in cli/, so check both the service directory and the
    /// sibling cli directory.
    /// </summary>
    private static string LocateVssHelper()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "FileKeepVss.exe"),
            Path.Combine(baseDir, "..", "cli", "FileKeepVss.exe"),
        };
        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(full))
                return full;
        }
        throw new FileNotFoundException(
            "VSS helper not found. Expected FileKeepVss.exe next to the service " +
            "or in the sibling cli/ directory.");
    }

    /// <summary>
    /// Uploads a completed backup's chunks to Usenet. The NNTP password comes
    /// from the USENETBACKUP_NNTP_PASSWORD environment variable and is never
    /// stored. Throws on failure; the caller logs it.
    /// </summary>
    /// <summary>
    /// Uploads a completed backup to each of the job's configured targets
    /// ("nntp" and/or "smb"). Each target runs independently: one failing
    /// doesn't stop the other, but all failures are collected and thrown
    /// together so the caller sees the full picture.
    /// </summary>
    private void UploadToTargets(BackupJobConfig job, string backupId, string passphrase)
    {
        var targets = job.EffectiveTargets;
        var errors = new List<string>();
        foreach (string targetName in targets)
        {
            // Resolve the target name to a location. Fall back to legacy
            // "nntp"/"smb" type names for configs that bypassed migration.
            BackupLocation? loc = _config.Locations.FirstOrDefault(l =>
                l.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            string type = loc?.Type ??
                (targetName.Equals("nntp", StringComparison.OrdinalIgnoreCase) ? "nntp" :
                 targetName.Equals("smb", StringComparison.OrdinalIgnoreCase) ? "smb" : "");
            try
            {
                if (type == "nntp")
                    UploadToNntpLocation(job, backupId, passphrase, loc);
                else if (type == "smb")
                    SyncToSmbLocation(job, backupId, loc);
                else
                    throw new InvalidOperationException(
                        $"Unknown backup location '{targetName}'.");
            }
            catch (Exception ex) { errors.Add($"{targetName}: {ex.Message}"); }
        }
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Upload to {errors.Count} target(s) failed: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// Uploads to a Usenet location. If <paramref name="loc"/> is null (legacy
    /// "nntp" target), falls back to the scheduler's configured providers.
    /// </summary>
    private void UploadToNntpLocation(
        BackupJobConfig job, string backupId, string passphrase, BackupLocation? loc)
    {
        // TODO: use loc's host/port/credentials when the multi-provider
        // upload path supports per-location providers. For now, the existing
        // provider-based upload is used.
        AutoUploadToUsenet(job, backupId, passphrase);
    }

    /// <summary>
    /// Syncs the repo to an SMB location. The share receives chunks,
    /// manifests, and repo.json — enough for a recovery wizard to open it
    /// directly as a read-only repo. If <paramref name="loc"/> is null
    /// (legacy "smb" target), falls back to the job's own SMB settings.
    /// </summary>
    private void SyncToSmbLocation(BackupJobConfig job, string backupId, BackupLocation? loc)
    {
        string share = loc?.Share ?? job.SmbShare;
        string user = loc?.SmbUser ?? job.SmbUser;
        string? passwordProtected = loc?.SmbPasswordProtected ?? job.SmbPasswordProtected;
        if (string.IsNullOrWhiteSpace(share))
            throw new InvalidOperationException(
                $"Job '{job.Name}' targets an SMB location but no share is configured.");
        string? smbPassword = Environment.GetEnvironmentVariable(SmbPasswordEnvVar);
        if (string.IsNullOrEmpty(smbPassword) && !string.IsNullOrEmpty(passwordProtected))
            smbPassword = Dpapi.Unprotect(passwordProtected);

        string locName = loc?.Name ?? "smb";
        Log($"job '{job.Name}': syncing backup {backupId} to '{locName}' ({share})");
        using var smb = SmbShare.Connect(share, user, smbPassword);
        var sync = new SmbSync(Log);
        var result = sync.Sync(job.Repo, smb);
        Log($"job '{job.Name}': SMB sync to '{locName}' complete — {result.ChunksCopied} chunks, " +
            $"{result.ManifestsCopied} manifests copied.");
    }

    private void AutoUploadToUsenet(BackupJobConfig job, string backupId, string passphrase)
    {
        var providers = _nntpProviders;
        if (providers.Count == 0)
            throw new InvalidOperationException("Auto-upload enabled but no Usenet provider configured.");

        using var repo = BackupRepository.Open(job.Repo, passphrase);
        var manifest = repo.LoadManifest(backupId);
        // Chunk order MUST use ChunkOrdering for parity group alignment (see b1921c8)
        string[] chunkIds = ChunkOrdering.GetOrderedChunkIds(manifest);
        // Packing mode and volume size are sticky per backup (see
        // BackupRepository.ResolveVolumeMode): flips are refused loudly
        // rather than silently breaking the upload/download contract.
        bool useVolumes = repo.ResolveVolumeMode(
            manifest, chunkIds, forceChunkMode: false, out string? refusal);
        if (refusal is not null)
            throw new InvalidOperationException(refusal);
        if (manifest.Volumes is { Count: > 0 } && !repo.UseVolumes)
            Log($"job '{job.Name}': backup was uploaded with volume packing; volume mode forced.");
        List<IReadOnlyList<string>> volumeGroups = useVolumes
            ? repo.PlanVolumes(manifest).ToList()
            : new();

        List<string> errors = new();
        foreach (var nntp in providers)
        {
            if (string.IsNullOrWhiteSpace(nntp.Host))
                continue;
            // Env var takes precedence; otherwise decrypt the DPAPI-stored password.
            string? nntpPassword = Environment.GetEnvironmentVariable(NntpPasswordEnvVar);
            if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(nntp.PasswordProtected))
                nntpPassword = Dpapi.Unprotect(nntp.PasswordProtected);
            if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(nntp.Username))
            {
                errors.Add($"{nntp.Host}: password not configured");
                continue;
            }

            Log($"job '{job.Name}': auto-uploading {backupId} to {nntp.Host}");
            try
            {
                // Preflight: prove the provider accepts our largest article
                // BEFORE posting anything. Uses the cached per-provider limit
                // when known; otherwise posts one probe article at the planned
                // size. Aborts this provider's upload if the probe is rejected.
                if (useVolumes)
                    PreflightProviderCapacity(repo, volumeGroups, nntp, nntpPassword);
                // Use a connection pool for parallel uploads.
                // Connections setting controls the pool size (1-100, default 10).
                using var pool = new Nntp.NntpConnectionPool(
                    nntp.Host, nntp.Port, nntp.Ssl,
                    nntp.Username, nntpPassword, nntp.Connections);
                using var store = new Nntp.NntpBlobStore(pool, nntp.Newsgroup, repo.RepoId, repo.CatalogPath, messageIndex: repo.MessageIndex,
                    providerKey: Nntp.ChunkMessageIndex.MakeProviderKey(nntp.Host, nntp.Newsgroup));
                // Determine redundancy mode: per-provider override, else job default
                string redundancy = !string.IsNullOrEmpty(nntp.RedundancyMode)
                    ? nntp.RedundancyMode
                    : job.RedundancyMode;
                int uploaded = 0, skipped = 0;
                // Parallel upload using the connection pool.
                // Each thread acquires a connection, uploads, releases it.
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = nntp.Connections
                };
                // Parity blocks are generated per group at upload time (never
                // all held in memory: a parity shard is volume-sized). Count
                // them upfront so the progress tracker knows the full scope:
                // positional groups of 10, trailing groups of < 2 skipped.
                int dataUnits = useVolumes ? volumeGroups.Count : chunkIds.Length;
                int parityGroups = dataUnits / 10 + (dataUnits % 10 >= 2 ? 1 : 0);
                int parityUnits = redundancy == "xor" ? parityGroups
                    : redundancy == "par2" ? parityGroups * 3 : 0;

                // Progress tracker: logs every 5 min with rolling throughput ETA.
                // Shows "measuring..." until enough data (10 chunks + 30s), then
                // real estimates that refine as more data arrives.
                // Exposed via ActiveUpload for dashboard progress bar.
                // Total covers data units (chunks or volumes) + parity + manifest
                // + index so the bar doesn't hit 100% and vanish while work remains.
                int totalUnits = dataUnits + parityUnits + 2;
                using var progress = new UploadProgressTracker(
                    Log, job.Name, nntp.Host, totalUnits);
                ActiveUpload = progress;
                try
                {
                    // volId -> chunk group, filled by the data phase; feeds parity.
                    var volumeEntries = new List<VolumeEntry>();
                    if (useVolumes)
                    {
                        progress.SetPhase("volumes");
                        Parallel.ForEach(volumeGroups, parallelOptions, group =>
                        {
                            var volume = Nntp.VolumePacker.BuildVolume(group, repo.GetChunkBlob);
                            lock (volumeEntries)
                                volumeEntries.Add(new VolumeEntry
                                {
                                    Id = volume.Id,
                                    ChunkIds = volume.ChunkIds.ToList(),
                                    SizeBytes = volume.Bytes.Length,
                                });
                            if (store.Exists(volume.Id))
                            {
                                Interlocked.Increment(ref skipped);
                                progress.RecordSkipped(1);
                            }
                            else
                            {
                                store.PutVolume(volume.Id, volume.Bytes);
                                Interlocked.Increment(ref uploaded);
                                progress.RecordUploaded(1, volume.Bytes.Length);
                            }
                        });
                        // Record the packing in the manifest BEFORE the manifest
                        // itself is uploaded (first provider to finish wins;
                        // the packing is deterministic so all agree).
                        if (manifest.Volumes is null)
                        {
                            manifest.Volumes = volumeEntries
                                .OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
                            manifest.VolumeSize = repo.VolumeSizeBytes;
                            repo.SaveManifest(manifest);
                        }
                    }
                    else
                    {
                        progress.SetPhase("chunks");
                        Parallel.ForEach(chunkIds, parallelOptions, chunkId =>
                        {
                            if (store.Exists(chunkId))
                            {
                                Interlocked.Increment(ref skipped);
                                progress.RecordSkipped(1);
                            }
                            else
                            {
                                byte[] blob = repo.GetChunkBlob(chunkId);
                                store.Put(chunkId, blob);
                                Interlocked.Increment(ref uploaded);
                                progress.RecordUploaded(1, blob.Length);
                            }
                        });
                    }

                    // Upload parity blocks (tracked; previously invisible).
                    // In volume mode parity is generated per group and posted
                    // immediately, so a volume-sized shard never multiplies
                    // into gigabytes held in memory.
                    int parityUploaded = 0, paritySkipped = 0;
                    if (parityUnits > 0)
                    {
                        progress.SetPhase("parity");
                        string parityLabel = redundancy == "xor" ? "XOR" : "PAR2";
                        if (useVolumes)
                        {
                            var chunkMap = volumeEntries.ToDictionary(
                                e => e.Id, e => (IReadOnlyList<string>)e.ChunkIds);
                            string[] sortedVolIds = chunkMap.Keys
                                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
                            byte[] GetVolumeBytes(string volId) =>
                                Nntp.VolumePacker.BuildVolume(chunkMap[volId], repo.GetChunkBlob).Bytes;
                            for (int g = 0; g < sortedVolIds.Length; g += 10)
                            {
                                string[] group = sortedVolIds.Skip(g).Take(10).ToArray();
                                if (group.Length < 2)
                                    continue;
                                IReadOnlyDictionary<string, byte[]> blocks =
                                    redundancy == "xor"
                                        ? Redundancy.XorParity.GenerateParity(group, GetVolumeBytes)
                                        : Redundancy.Par2Redundancy.GenerateParity(group, GetVolumeBytes);
                                Parallel.ForEach(blocks, parallelOptions, kvp =>
                                {
                                    if (!store.Exists(kvp.Key))
                                    {
                                        store.PutVolume(kvp.Key, kvp.Value);
                                        Interlocked.Increment(ref parityUploaded);
                                        progress.RecordUploaded(1, kvp.Value.Length);
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref paritySkipped);
                                        progress.RecordSkipped(1);
                                    }
                                });
                            }
                        }
                        else
                        {
                            IReadOnlyDictionary<string, byte[]> parityBlocks =
                                redundancy == "xor"
                                    ? Redundancy.XorParity.GenerateParity(
                                        chunkIds, id => repo.GetChunkBlob(id))
                                    : Redundancy.Par2Redundancy.GenerateParity(
                                        chunkIds, id => repo.GetChunkBlob(id));
                            Parallel.ForEach(parityBlocks, parallelOptions, kvp =>
                            {
                                if (!store.Exists(kvp.Key))
                                {
                                    store.Put(kvp.Key, kvp.Value);
                                    Interlocked.Increment(ref parityUploaded);
                                    progress.RecordUploaded(1, kvp.Value.Length);
                                }
                                else
                                {
                                    Interlocked.Increment(ref paritySkipped);
                                    progress.RecordSkipped(1);
                                }
                            });
                        }
                        Log($"job '{job.Name}': uploaded {parityUploaded} {parityLabel} parity blocks to {nntp.Host}" +
                            (paritySkipped > 0 ? $" ({paritySkipped} already present)" : ""));
                    }

                    // Manifest + index (tracked; previously invisible)
                    progress.SetPhase("manifest");
                    int manifestArticles = repo.UploadManifest(backupId, store);
                    progress.RecordUploaded(manifestArticles, 0);
                    // Reconcile: we reserved 2 slots; mark any unused as skipped
                    // so the bar reaches exactly 100%.
                    if (manifestArticles < 2)
                        progress.RecordSkipped(2 - manifestArticles);

                    // Track upload for expiration monitoring
                    var tracker = new Nntp.UsenetUploadTracker(job.Repo);
                    tracker.RecordUpload(backupId, nntp.Host, nntp.Newsgroup);
                    OperationLog.Append(job.Repo, "auto-upload",
                        $"job={job.Name} id={backupId} chunks={chunkIds.Length} volumes={(useVolumes ? volumeGroups.Count : 0)} uploaded={uploaded} skipped={skipped} host={nntp.Host}");
                    Log($"job '{job.Name}': auto-upload to {nntp.Host} complete ({uploaded} posted, {skipped} already present)");
                }
                finally
                {
                    ActiveUpload = null;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{nntp.Host}: {ex.Message}");
                Log($"job '{job.Name}': auto-upload to {nntp.Host} FAILED: {ex.Message}");
            }
        }
        if (errors.Count == providers.Count)
            throw new InvalidOperationException($"Auto-upload failed on all {providers.Count} provider(s): {string.Join("; ", errors)}");
    }

    /// <summary>
    /// Upload preflight: builds the largest planned volume, measures the exact
    /// posted article size, and proves the provider accepts it before any real
    /// data is posted. Uses the cached per-provider limit when known; otherwise
    /// posts a single probe article at the planned size. Throws (aborting this
    /// provider's upload) with a clear message if the provider rejects the size.
    /// </summary>
    private void PreflightProviderCapacity(
        BackupRepository repo,
        List<IReadOnlyList<string>> volumeGroups,
        NntpConfig nntp,
        string? nntpPassword)
    {
        if (volumeGroups.Count == 0)
            return;
        // Largest group by total blob bytes -> build it -> measure the article.
        long maxBlobBytes = 0;
        IReadOnlyList<string>? largest = null;
        foreach (var group in volumeGroups)
        {
            long total = 0;
            foreach (string id in group)
                total += repo.GetChunkBlobSize(id);
            if (total > maxBlobBytes)
            {
                maxBlobBytes = total;
                largest = group;
            }
        }
        if (largest is null)
            return;
        var volume = Nntp.VolumePacker.BuildVolume(largest, repo.GetChunkBlob);
        string article = Nntp.ArticleCodec.BuildVolumeArticle(
            volume.Id, repo.RepoId, volume.Bytes, nntp.Newsgroup, "usenet-backup");
        long articleBytes = article.Length; // yEnc body is ASCII: chars == bytes

        var limits = new Nntp.ProviderLimits(repo.RepoRoot);
        long? cached = limits.GetMaxArticleBytes(nntp.Host, nntp.Port);
        if (cached.HasValue && cached.Value >= articleBytes)
        {
            Log($"job preflight: largest article {articleBytes} bytes within " +
                $"known {nntp.Host}:{nntp.Port} limit ({cached.Value} bytes).");
            return;
        }
        Log($"job preflight: probing {nntp.Host}:{nntp.Port} with a {articleBytes}-byte " +
            "article to verify the upload will succeed...");
        limits.EnsureCapacity(nntp.Host, nntp.Port, articleBytes, size =>
        {
            byte[] payload = new byte[size];
            System.Random.Shared.NextBytes(payload);
            string nonce = Guid.NewGuid().ToString("N")[..8];
            var sb = new System.Text.StringBuilder();
            sb.Append("From: usenet-backup\r\n");
            sb.Append("Newsgroups: ").Append(nntp.Newsgroup).Append("\r\n");
            sb.Append("Subject: [usenet-backup] probe ").Append(nonce).Append("\r\n");
            sb.Append("Message-ID: <probe-").Append(nonce).Append("@usenet-backup>\r\n");
            sb.Append("X-UsenetBackup-Probe: 1\r\n");
            sb.Append("\r\n");
            sb.Append(Nntp.YEnc.Encode(payload, $"probe-{nonce}.bin"));
            try
            {
                using var client = new Nntp.NntpClient(nntp.Host, nntp.Port, nntp.Ssl);
                client.Connect();
                if (!string.IsNullOrEmpty(nntp.Username))
                    client.Authenticate(nntp.Username, nntpPassword ?? "");
                client.Post(sb.ToString());
                client.Quit();
                return true;
            }
            catch
            {
                return false;
            }
        });
        Log("job preflight OK — provider accepts the volume size.");
    }

    private void Log(string message) => _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
}
