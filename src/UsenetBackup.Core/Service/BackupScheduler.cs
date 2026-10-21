namespace UsenetBackup.Core.Service;

using System.Text.Json;
using UsenetBackup.Core.Json;
using UsenetBackup.Core.Nntp;

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

    public sealed class Weekly : Schedule
    {
        public DayOfWeek Day { get; }
        public int Hour { get; }
        public int Minute { get; }
        public Weekly(DayOfWeek day, int hour, int minute) { Day = day; Hour = hour; Minute = minute; }

        public override DateTime NextAfter(DateTime now)
        {
            var target = new DateTime(now.Year, now.Month, now.Day, Hour, Minute, 0, now.Kind);
            int daysUntil = ((int)Day - (int)target.DayOfWeek + 7) % 7;
            if (daysUntil == 0 && target <= now) daysUntil = 7;
            return target.AddDays(daysUntil);
        }

        public override string ToString() => $"weekly {Day} {Hour:D2}:{Minute:D2}";
    }

    public sealed class Monthly : Schedule
    {
        public int Day { get; }
        public int Hour { get; }
        public int Minute { get; }
        public Monthly(int day, int hour, int minute) { Day = day; Hour = hour; Minute = minute; }

        public override DateTime NextAfter(DateTime now)
        {
            int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
            int targetDay = Math.Min(Day, daysInMonth);
            var target = new DateTime(now.Year, now.Month, targetDay, Hour, Minute, 0, now.Kind);
            if (target <= now)
            {
                var nextMonth = now.AddMonths(1);
                daysInMonth = DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month);
                targetDay = Math.Min(Day, daysInMonth);
                target = new DateTime(nextMonth.Year, nextMonth.Month, targetDay, Hour, Minute, 0, nextMonth.Kind);
            }
            return target;
        }

        public override string ToString() => $"monthly {Day} {Hour:D2}:{Minute:D2}";
    }

    public sealed class Yearly : Schedule
    {
        public int Month { get; }
        public int Day { get; }
        public int Hour { get; }
        public int Minute { get; }
        public Yearly(int month, int day, int hour, int minute) { Month = month; Day = day; Hour = hour; Minute = minute; }

        public override DateTime NextAfter(DateTime now)
        {
            int daysInMonth = DateTime.DaysInMonth(now.Year, Month);
            int targetDay = Math.Min(Day, daysInMonth);
            var target = new DateTime(now.Year, Month, targetDay, Hour, Minute, 0, now.Kind);
            if (target <= now)
            {
                daysInMonth = DateTime.DaysInMonth(now.Year + 1, Month);
                targetDay = Math.Min(Day, daysInMonth);
                target = new DateTime(now.Year + 1, Month, targetDay, Hour, Minute, 0, now.Kind);
            }
            return target;
        }

        public override string ToString() => $"yearly {Month:D2}-{Day:D2} {Hour:D2}:{Minute:D2}";
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
        if (parts.Length == 3 && parts[0].Equals("weekly", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<DayOfWeek>(parts[1], true, out var day))
        {
            string[] hm = parts[2].Split(':');
            if (hm.Length == 2 &&
                int.TryParse(hm[0], out int wh) && wh is >= 0 and < 24 &&
                int.TryParse(hm[1], out int wm) && wm is >= 0 and < 60)
                return new Schedule.Weekly(day, wh, wm);
        }
        if (parts.Length == 3 && parts[0].Equals("monthly", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1], out int mday) && mday is >= 1 and <= 31)
        {
            string[] hm = parts[2].Split(':');
            if (hm.Length == 2 &&
                int.TryParse(hm[0], out int mh) && mh is >= 0 and < 24 &&
                int.TryParse(hm[1], out int mm) && mm is >= 0 and < 60)
                return new Schedule.Monthly(mday, mh, mm);
        }
        if (parts.Length == 3 && parts[0].Equals("yearly", StringComparison.OrdinalIgnoreCase))
        {
            string[] md = parts[1].Split('-');
            string[] hm = parts[2].Split(':');
            if (md.Length == 2 && hm.Length == 2 &&
                int.TryParse(md[0], out int ymo) && ymo is >= 1 and <= 12 &&
                int.TryParse(md[1], out int yday) && yday is >= 1 and <= 31 &&
                int.TryParse(hm[0], out int yh) && yh is >= 0 and < 24 &&
                int.TryParse(hm[1], out int ymin) && ymin is >= 0 and < 60)
                return new Schedule.Yearly(ymo, yday, yh, ymin);
        }
        throw new InvalidOperationException(
            $"Invalid schedule '{text}' (expected \"daily HH:mm\", \"interval N\", \"weekly Day HH:mm\", \"monthly D HH:mm\", or \"yearly MM-DD HH:mm\").");
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

    /// <summary>
    /// Resolves the repository passphrase for backup operations: environment
    /// variable first, then the DPAPI-encrypted value in the service config
    /// (set via the dashboard Security card). Returns null if neither is
    /// configured. Note: the YubiKey flag gates <em>restore</em> sessions on
    /// a physical touch; backups always use the DPAPI-stored value
    /// unattended and never need the key.
    /// </summary>
    public static string? GetPassphrase(ServiceConfig config)
    {
        string? fromEnv = Environment.GetEnvironmentVariable(PassphraseEnvVar);
        if (!string.IsNullOrEmpty(fromEnv))
            return fromEnv;
        if (!string.IsNullOrEmpty(config.PassphraseProtected))
        {
            try { return Dpapi.Unprotect(config.PassphraseProtected); }
            catch { return null; }
        }
        return null;
    }

    private readonly List<JobState> _jobs;
    private readonly IClock _clock;
    private readonly TimeSpan _pollInterval;
    private readonly Action<string>? _log;
    /// <summary>
    /// Guards short-lived scheduler state: _jobs, _config, _nntpProviders,
    /// _lastRetentionCheck, _activeOperationCts, ActiveUpload. Never held
    /// across a backup, upload, verify, or retention operation (D1).
    /// </summary>
    private readonly object _stateLock = new();
    /// <summary>
    /// Serializes long-running operations (backup/upload/verify/retention)
    /// so they never overlap. Does NOT block short state reads: dashboard
    /// status, ReloadJobs, and the Jobs list stay responsive during a run.
    /// </summary>
    private readonly object _operationLock = new();
    /// <summary>
    /// CTS for the currently running operation, or null if idle.
    /// Guarded by _stateLock. Cancelled on service stop (D8).
    /// </summary>
    private CancellationTokenSource? _activeOperationCts;
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
            lock (_stateLock)
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
        lock (_stateLock)
        {
            var now = _clock.Now;
            _nntpProviders = config.EffectiveProviders;
            _config = config;
            var oldByName = _jobs.ToDictionary(j => j.Config.Name,
                StringComparer.OrdinalIgnoreCase);
            _jobs.Clear();
            // Snapshot the job configs under the Jobs lock: UpsertJob/DeleteJob
            // may replace the list concurrently (D5).
            List<BackupJobConfig> jobConfigs;
            lock (config.JobsLock)
                jobConfigs = config.Jobs.ToList();
            foreach (var jc in jobConfigs)
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
    /// Begins a long-running operation: creates a CancellationTokenSource for
    /// it and returns its token. Must be called holding _operationLock.
    /// </summary>
    private CancellationToken BeginOperation()
    {
        lock (_stateLock)
        {
            _activeOperationCts?.Dispose();
            _activeOperationCts = new CancellationTokenSource();
            return _activeOperationCts.Token;
        }
    }

    /// <summary>
    /// Ends the current operation, disposing its CTS. Must be called holding
    /// _operationLock.
    /// </summary>
    private void EndOperation()
    {
        lock (_stateLock)
        {
            _activeOperationCts?.Dispose();
            _activeOperationCts = null;
        }
    }

    /// <summary>
    /// Cancels the currently running operation, if any. Called on service
    /// stop so a running job aborts at the next phase boundary (D8).
    /// </summary>
    public void CancelActiveOperation()
    {
        lock (_stateLock)
        {
            _activeOperationCts?.Cancel();
        }
    }

    /// <summary>
    /// Main loop: runs until <paramref name="stopping"/> is cancelled.
    /// When stopping fires, any active operation is cancelled at its next
    /// phase boundary (D8).
    /// </summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        Log("scheduler started");
        using var reg = stopping.Register(CancelActiveOperation);
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                RunDueJobs(_clock.Now, stopping);
            }
            catch (OperationCanceledException)
            {
                // Service stop cancelled the active operation mid-run.
                // Break out gracefully instead of crashing.
                break;
            }
            RunRetentionIfDue(_clock.Now, stopping);
            try { await Task.Delay(_pollInterval, stopping); }
            catch (OperationCanceledException) { break; }
        }
        Log("scheduler stopped");
    }

    /// <summary>
    /// Runs retention checks if enabled and the interval has elapsed.
    /// Exposed for tests.
    /// </summary>
    public void RunRetentionIfDue(DateTime now, CancellationToken stopping = default)
    {
        if (!_config.RetentionEnabled)
            return;
        if (_config.RetentionCheckIntervalHours <= 0)
            return;

        TimeSpan interval = TimeSpan.FromHours(_config.RetentionCheckIntervalHours);
        lock (_stateLock)
        {
            if (now - _lastRetentionCheck < interval)
                return;
            _lastRetentionCheck = now;
        }

        try
        {
            RunRetentionChecks(stopping);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"retention check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Manual retention check for a single repo (dashboard operation).
    /// Serialized against other long operations via _operationLock; does not
    /// block short state reads (D1).
    /// </summary>
    public void RunRetentionCheck(string repoPath, bool dryRun)
    {
        CancellationToken token;
        IReadOnlyList<NntpConfig> providers;
        lock (_stateLock)
        {
            providers = _nntpProviders;
            if (providers.Count == 0)
                throw new InvalidOperationException("No NNTP providers configured.");
        }
        lock (_operationLock)
        {
            token = BeginOperation();
            try { RunRetentionForRepo(repoPath, providers, dryRun, token); }
            finally { EndOperation(); }
        }
    }

    /// <summary>
    /// Manual NNTP upload of a backup (dashboard operation).
    /// Serialized against other long operations via _operationLock; does not
    /// block short state reads (D1).
    /// </summary>
    public void UploadBackup(string repoPath, string backupId)
    {
        lock (_operationLock)
        {
            CancellationToken token = BeginOperation();
            try
            {
                string passphrase = GetPassphrase(_config) ?? "";
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
            UploadToTargets(job, backupId, passphrase, token);
            }
            finally { EndOperation(); }
        }
    }

    /// <summary>
    /// Downloads chunks referenced by an NZB file from the first configured provider.
    /// </summary>
    public void DownloadFromNzb(string repoPath, string nzbPath, string passphrase)
    {
        IReadOnlyList<NntpConfig> providers;
        lock (_stateLock)
            providers = _nntpProviders;
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
    private void RunRetentionChecks(CancellationToken stopping = default)
    {
        IReadOnlyList<NntpConfig> providers;
        lock (_stateLock)
            providers = _nntpProviders;
        if (providers.Count == 0)
        {
            Log("retention check skipped: no NNTP providers configured");
            return;
        }

        // Group jobs by repo to avoid checking the same repo twice.
        // Snapshot under the lock: ReloadJobs mutates _jobs concurrently.
        List<string> repos;
        lock (_stateLock)
        {
            repos = _jobs.Select(j => j.Config.Repo).Distinct().ToList();
        }
        // Serialize against other long operations. The token from
        // BeginOperation is cancelled by CancelActiveOperation on service
        // stop (registered in RunAsync).
        lock (_operationLock)
        {
            CancellationToken token = BeginOperation();
            try
            {
                foreach (string repoPath in repos)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        RunRetentionForRepo(repoPath, providers, token: token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log($"retention check failed for repo '{repoPath}': {ex.Message}");
                    }
                }
            }
            finally { EndOperation(); }
        }
    }

    private void RunRetentionForRepo(string repoPath, IReadOnlyList<NntpConfig> providers, bool dryRun = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string passphrase = GetPassphrase(_config) ?? "";
        if (string.IsNullOrEmpty(passphrase))
        {
            Log($"retention check skipped for '{repoPath}': no passphrase (set {PassphraseEnvVar})");
            return;
        }

        using var repo = BackupRepository.Open(repoPath, passphrase);
        token.ThrowIfCancellationRequested();
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
    /// The job's current run state, evaluated against the live config
    /// (targets + attached USB drives). Single evaluation path used by
    /// the due scan, manual runs and the dashboard's status feed.
    /// </summary>
    public JobRunState EvaluateJobState(BackupJobConfig job)
    {
        var cfg = _config;
        if (cfg is null)
            return job.Paused ? JobRunState.UserPaused : JobRunState.Active;
        return JobStateEvaluator.Evaluate(job, JobStateEvaluator.BuildLookup(cfg));
    }

    /// <summary>
    /// Runs every job whose next-run time has passed. Exposed for tests.
    /// A due job that cannot run (user-paused, no backup location, USB
    /// drive away) is NOT run and NOT left due: its next run is advanced
    /// past this occurrence. Missed occurrences are discarded, never
    /// caught up — when the cause clears, the job simply re-arms for its
    /// next scheduled time.
    /// </summary>
    public void RunDueJobs(DateTime now, CancellationToken stopping = default)
    {
        List<JobState> due;
        lock (_stateLock)
            due = _jobs.Where(j => j.NextRunLocal <= now).OrderBy(j => j.NextRunLocal).ToList();
        var lookup = _config is null ? null : JobStateEvaluator.BuildLookup(_config);
        foreach (var job in due)
        {
            stopping.ThrowIfCancellationRequested();
            JobRunState state = lookup is null
                ? (job.Config.Paused ? JobRunState.UserPaused : JobRunState.Active)
                : JobStateEvaluator.Evaluate(job.Config, lookup);
            if (state != JobRunState.Active)
            {
                lock (_stateLock)
                {
                    if (_jobs.Contains(job))
                        job.NextRunLocal = job.Schedule.NextAfter(now);
                }
                Log($"job '{job.Config.Name}' skipped ({JobStateEvaluator.Describe(state)}); " +
                    $"next run {job.NextRunLocal:yyyy-MM-dd HH:mm}");
                continue;
            }
            RunOne(job, now, stopping);
        }
    }

    /// <summary>
    /// Runs a single job immediately, outside its schedule (dashboard "run now").
    /// The long operation runs under _operationLock; state updates are applied
    /// afterwards under _stateLock only if the job is still present (D1).
    /// </summary>
    public JobRunResult RunJobNow(string name)
    {
        BackupJobConfig config;
        lock (_stateLock)
        {
            config = _jobs.FirstOrDefault(j => j.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?.Config ?? throw new InvalidOperationException($"Unknown job '{name}'.");
        }
        JobRunState state = EvaluateJobState(config);
        if (state != JobRunState.Active)
            throw new InvalidOperationException(
                $"Job '{name}' cannot run: {JobStateEvaluator.Describe(state)}.");
        JobRunResult result;
        lock (_operationLock)
        {
            CancellationToken token = BeginOperation();
            try { result = Execute(config, _clock.Now, token); }
            finally { EndOperation(); }
        }
        lock (_stateLock)
        {
            var job = _jobs.FirstOrDefault(j => j.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (job is not null)
            {
                job.LastResult = result;
                job.ConsecutiveFailures = result.Success ? 0 : job.ConsecutiveFailures + 1;
                // Re-anchor the schedule so "run now" doesn't cause an immediate re-run.
                job.NextRunLocal = job.Schedule.NextAfter(_clock.Now);
            }
        }
        return result;
    }

    private void RunOne(JobState job, DateTime now, CancellationToken stopping)
    {
        JobRunResult result;
        lock (_operationLock)
        {
            CancellationToken token = BeginOperation();
            try { result = Execute(job.Config, now, token); }
            finally { EndOperation(); }
        }
        lock (_stateLock)
        {
            // The job may have been removed by a concurrent ReloadJobs; only
            // update state if it's still in the list.
            if (_jobs.Contains(job))
            {
                job.LastResult = result;
                job.ConsecutiveFailures = result.Success ? 0 : job.ConsecutiveFailures + 1;
                job.NextRunLocal = job.Schedule.NextAfter(now);
            }
        }
        stopping.ThrowIfCancellationRequested();
    }

    private JobRunResult Execute(BackupJobConfig job, DateTime now, CancellationToken token)
        => Execute(job, now, token, requireTargets: true, opName: "scheduled-backup");

    /// <summary>
    /// Runs a one-time manual backup to a local folder chosen at run time.
    /// This is the only path that may produce a local-only backup: it is
    /// never scheduled and never persists a job. The repo is initialized
    /// here if it does not exist yet.
    /// </summary>
    public JobRunResult RunOneTimeBackup(IEnumerable<string> sourceDirs, string repoPath)
    {
        var sources = sourceDirs.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (sources.Count == 0 || sources.Any(s => !Directory.Exists(s)))
            throw new ArgumentException("Source folders must be existing directories.", nameof(sourceDirs));
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("A destination folder is required.", nameof(repoPath));
        string passphrase = GetPassphrase(_config) ?? "";
        if (string.IsNullOrEmpty(passphrase))
            throw new InvalidOperationException(
                $"Cannot run a backup without a passphrase: set the {PassphraseEnvVar} " +
                "environment variable for the service account.");
        if (!File.Exists(Path.Combine(repoPath, "repo.json")))
            BackupRepository.Init(repoPath, passphrase);
        var job = new BackupJobConfig
        {
            Name = "One-time backup",
            Source = sources[0],
            Sources = sources,
            Repo = repoPath,
            Mode = "full",
        };
        JobRunResult result;
        lock (_operationLock)
        {
            CancellationToken token = BeginOperation();
            try { result = Execute(job, _clock.Now, token, requireTargets: false, opName: "one-time-backup"); }
            finally { EndOperation(); }
        }
        return result;
    }

    private JobRunResult Execute(BackupJobConfig job, DateTime now, CancellationToken token,
        bool requireTargets, string opName)
    {
        // Fail closed: a scheduled job must upload to at least one Usenet or
        // network location. Local-only backups are only allowed as manual
        // one-time runs (requireTargets: false). This catches hand-edited
        // configs that bypass the dashboard validation.
        if (requireTargets && job.EffectiveTargets.Count == 0)
        {
            string msg = $"Scheduled job '{job.Name}' has no upload targets. " +
                "Scheduled backups must upload to Usenet or a network location; " +
                "local-only backups can only be run manually, one at a time.";
            Log($"job '{job.Name}': FAILED: {msg}");
            try { OperationLog.Append(job.Repo, "scheduled-backup-failed", $"job={job.Name} error={msg}"); }
            catch { /* best effort */ }
            return new JobRunResult
            {
                JobName = job.Name,
                StartedLocal = now,
                Success = false,
                Error = msg,
            };
        }
        Log($"job '{job.Name}': starting");
        try
        {
            token.ThrowIfCancellationRequested();
            string passphrase = GetPassphrase(_config) ?? "";
            if (string.IsNullOrEmpty(passphrase))
                throw new InvalidOperationException(
                    $"Cannot run scheduled backup without a passphrase: set the {PassphraseEnvVar} " +
                    "environment variable for the service account.");

            using var repo = OpenOrInitRepo(job.Repo, passphrase);

            // Disk-image jobs read the raw device; file snapshot providers
            // (VSS, backup privilege) do not apply. Always a single source.
            if (job.IsDiskImage)
            {
                // Whole-disk image jobs always capture the full device.
                // The image name is derived from the job name so each
                // scheduled run lands under a stable, no-typing name.
                //
                // If an SMB target is configured, stream chunks directly
                // to the share (sparse: used space only) instead of
                // staging them locally — a full drive image cannot fit
                // in a repo on the same drive. The manifest stays local.
                // Without an SMB target, fall back to local staging.
                bool batched = ShouldUseBatchedStaging(job, repo, out BackupManifest? batchParent);
                BackupManifest manifest;
                try
                {
                    token.ThrowIfCancellationRequested();
                    manifest = batched
                        ? RunBatchedBackup(job, repo, batchParent, null, token)
                        : RunDiskImageJob(job, repo, token);
                }
                catch
                {
                    throw;
                }
                return FinishBackup(job, repo,
                    new[] { (manifest, batched) }, passphrase, token, opName, now);
            }

            // File jobs: one manifest per source folder. Each source gets
            // its own snapshot provider (VSS snapshots are per-volume) and
            // its own incremental parent chain (matched by source folder),
            // so folders never diff against each other.
            var done = new List<(BackupManifest Manifest, bool Batched)>();
            foreach (string source in job.EffectiveSources)
            {
                token.ThrowIfCancellationRequested();
                var srcJob = CloneJobForSource(job, source);
                using ISnapshotProvider? snap = CreateSnapshotProvider(srcJob);
                // Batched staging + pruning: when remote targets exist, the
                // backup is staged one batch at a time, each batch is verified,
                // uploaded/copied to every target, confirmed, then pruned. The
                // local repo stays staging-only; manifests/catalog never prune.
                bool batched = ShouldUseBatchedStaging(srcJob, repo, out BackupManifest? batchParent);
                BackupManifest manifest;
                try
                {
                    if (batched)
                    {
                        manifest = RunBatchedBackup(srcJob, repo, batchParent, snap, token);
                    }
                    else if (srcJob.Mode == "full")
                    {
                        manifest = repo.BackupDirectory(srcJob.Source, snap);
                    }
                    else
                    {
                        string? parent = LatestBackupIdForSource(repo, srcJob.Source);
                        manifest = parent is null
                            ? repo.BackupDirectory(srcJob.Source, snap)
                            : repo.BackupIncremental(srcJob.Source, parent, snap,
                                VerificationModeParser.Parse(srcJob.VerificationMode));
                    }
                    // VSS: writer session finalized only after data is safely stored.
                    // (Batched mode finalizes inside RunBatchedBackup.)
                    if (!batched && snap is Recovery.VssSnapshot vss)
                        vss.Complete();
                }
                catch
                {
                    // VssSnapshot.Dispose aborts the writer session fail-safe.
                    throw;
                }
                done.Add((manifest, batched));
            }
            return FinishBackup(job, repo, done, passphrase, token, opName, now);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a failure: propagate so the caller can
            // distinguish "stopped by user/service" from "backup failed".
            Log($"job '{job.Name}': cancelled");
            throw;
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
    /// Shallow-copies a job with a single source folder, so the existing
    /// single-source backup machinery (snapshots, batching, upload guards)
    /// runs once per folder. JSON round-trip keeps the clone in sync with
    /// future config properties.
    /// </summary>
    private static BackupJobConfig CloneJobForSource(BackupJobConfig job, string source)
    {
        string json = JsonSerializer.Serialize(job, FileKeepJsonContext.Default.BackupJobConfig);
        var clone = JsonSerializer.Deserialize(json, FileKeepJsonContext.Default.BackupJobConfig)
            ?? throw new InvalidOperationException($"Failed to clone job '{job.Name}'.");
        clone.Source = source;
        clone.Sources = new List<string> { source };
        return clone;
    }

    /// <summary>
    /// Latest backup ID for a given source folder (matched by full path,
    /// case-insensitive). Incremental chains are per-source: each folder
    /// diffs against its own previous backup, never another folder's.
    /// </summary>
    private static string? LatestBackupIdForSource(BackupRepository repo, string source)
    {
        string full;
        try { full = Path.GetFullPath(source); }
        catch { return null; }
        return repo.ListBackups()
            .Where(b =>
            {
                try { return string.Equals(Path.GetFullPath(b.Source), full, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            })
            .OrderByDescending(b => b.CreatedUtc)
            .FirstOrDefault()?.BackupId;
    }

    /// <summary>
    /// Per-manifest post-backup: operation log, auto-upload to targets,
    /// auto-verify. Aggregates into one <see cref="JobRunResult"/>; when a
    /// job backs up several folders, BackupId holds every manifest ID.
    /// </summary>
    private JobRunResult FinishBackup(
        BackupJobConfig job, BackupRepository repo,
        IReadOnlyList<(BackupManifest Manifest, bool Batched)> done,
        string passphrase, CancellationToken token, string opName, DateTime now)
    {
        bool autoUploadFailed = false;
        string? autoUploadError = null;
        bool verifyFailed = false;
        string? verifyError = null;
        int totalFiles = 0;
        foreach (var (manifest, batched) in done)
        {
            OperationLog.Append(job.Repo, opName,
                $"job={job.Name} id={manifest.BackupId} type={manifest.Type} files={manifest.Files.Count}");
            Log($"job '{job.Name}': {manifest.Type} backup {manifest.BackupId} ({manifest.Files.Count} files)");
            totalFiles += manifest.Files.Count;

            // Auto-upload to configured targets (nntp and/or smb) if any.
            // Skipped for batched runs: every batch was already uploaded to
            // every target (and pruned) during the backup.
            if (job.EffectiveTargets.Count > 0 && !batched)
            {
                token.ThrowIfCancellationRequested();
                try {
                    var _m2 = repo.LoadManifest(manifest.BackupId);
                    if (_m2 is not null) { StampYubiKeyChallenge(_m2); repo.SaveManifest(_m2); }
                    UploadToTargets(job, manifest.BackupId, passphrase, token);
                }
                catch (OperationCanceledException) { throw; }
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
            // Skipped for batched runs: every batch's chunks were verified
            // locally before upload, and pruned chunks can't be re-read.
            if (job.AutoVerify && batched)
            {
                Log($"job '{job.Name}': auto-verify skipped (each batch was verified before upload).");
                OperationLog.Append(job.Repo, "auto-verify",
                    $"job={job.Name} id={manifest.BackupId} result=skipped-batched");
            }
            if (job.AutoVerify && !batched)
            {
                token.ThrowIfCancellationRequested();
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
        }

        return new JobRunResult
        {
            JobName = job.Name,
            StartedLocal = now,
            Success = true,
            BackupId = string.Join(",", done.Select(d => d.Manifest.BackupId)),
            Files = totalFiles,
            AutoUploadFailed = autoUploadFailed,
            AutoUploadError = autoUploadError,
            VerifyFailed = verifyFailed,
            VerifyError = verifyError,
        };
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
    /// <summary>
    /// Opens the job's repo, initializing it first if it does not exist yet.
    /// Repositories are FileKeep's internal storage: they are always
    /// auto-created on first use, never something the user manages by hand.
    /// </summary>
    private static BackupRepository OpenOrInitRepo(string repoPath, string passphrase)
    {
        if (!File.Exists(Path.Combine(repoPath, "repo.json")))
            BackupRepository.Init(repoPath, passphrase);
        return BackupRepository.Open(repoPath, passphrase);
    }

    /// <summary>
    /// Derives a stable, filesystem-safe image name for a whole-disk image
    /// job from its job name (e.g. "System disk" -&gt; "Systemdisk.img").
    /// BackupDiskImage requires a plain file name (no separators).
    /// </summary>
    internal static string DiskImageName(string jobName)
    {
        string safe = string.Concat(jobName.Split(Path.GetInvalidFileNameChars()));
        safe = new string(safe.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (string.IsNullOrEmpty(safe))
            safe = "disk";
        return safe + ".img";
    }

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
            // Scale the snapshot hold time: the helper aborts (deleting the
            // snapshot mid-backup) on timeout. Default 2h; configurable per job.
            int timeoutSecs = job.VssTimeoutSecs > 0 ? job.VssTimeoutSecs : 7200;
            var vss = new Recovery.VssSnapshot(helper, timeoutSecs);
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
    /// <summary>
    /// Stamps the YubiKey challenge onto a manifest so recovery can use a
    /// YubiKey without access to service.json. The challenge is not secret.
    /// </summary>
    private void StampYubiKeyChallenge(BackupManifest manifest)
    {
        if (_config.PassphraseYubiKey && !string.IsNullOrEmpty(_config.YubiKeyChallenge))
        {
            manifest.YubiKeyChallenge = _config.YubiKeyChallenge;
            // Stamp the wrapped passphrase too so bare-metal recovery can
            // unwrap it with a YubiKey touch alone (AES-GCM: safe publicly).
            manifest.YubiKeyWrappedPassphrase = _config.PassphraseYubiKeyWrapped;
            // Re-save since the manifest was already written
            // (the challenge must be in the uploaded copy)
        }
    }

    private void UploadToTargets(BackupJobConfig job, string backupId, string passphrase, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
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
                else if (type == "usb")
                    SyncToUsbLocation(job, backupId, loc);
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
        if (loc is null)
        {
            AutoUploadToUsenet(job, backupId, passphrase);
            return;
        }
        // Use the location's own host/port/credentials, not the legacy fields.
        var provider = new NntpConfig
        {
            Host = loc.Host,
            Port = loc.Port,
            Ssl = loc.Ssl,
            Username = loc.Username,
            Connections = loc.Connections,
            Newsgroup = loc.Newsgroup,
            RetentionDays = loc.RetentionDays,
            PasswordProtected = loc.PasswordProtected,
        };
        AutoUploadToUsenetWithProviders(job, backupId, passphrase,
            new List<NntpConfig> { provider });
    }

    /// <summary>
    /// Syncs the repo to an SMB location. The share receives chunks,
    /// manifests, and repo.json — enough for a recovery wizard to open it
    /// directly as a read-only repo. If <paramref name="loc"/> is null
    /// (legacy "smb" target), falls back to the job's own SMB settings.
    /// </summary>
    /// <summary>
    /// Runs a whole-disk image job. If SMB is the only target (or the only
    /// network target), streams chunks directly to the share (sparse: used
    /// space only) — no local staging. If NNTP is also targeted, falls back
    /// to local staging because volume packing requires local chunks.
    /// The manifest is always written to the local repo.
    /// </summary>
    private BackupManifest RunDiskImageJob(
        BackupJobConfig job, BackupRepository repo, CancellationToken token)
    {
        // Find SMB and NNTP targets.
        bool hasSmb = false;
        bool hasNntp = false;
        BackupLocation? smbLoc = null;
        BackupLocation? nntpLoc = null;
        foreach (string targetName in job.EffectiveTargets)
        {
            var loc = _config.Locations.FirstOrDefault(l =>
                l.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            string type = loc?.Type ??
                (targetName.Equals("smb", StringComparison.OrdinalIgnoreCase) ? "smb" :
                 targetName.Equals("nntp", StringComparison.OrdinalIgnoreCase) ? "nntp" : "");
            if (type == "smb") { hasSmb = true; smbLoc ??= loc; }
            if (type == "nntp") { hasNntp = true; nntpLoc ??= loc; }
        }

        string imageName = DiskImageName(job.Name);

        // Stream to a single target when possible; fall back to local staging
        // when both are targeted (fan-out) or neither is.
        if (hasSmb && !hasNntp)
            return StreamDiskImageToSmb(job, repo, imageName, smbLoc);
        if (hasNntp && !hasSmb)
            return StreamDiskImageToNntp(job, repo, imageName, nntpLoc, token);

        if (hasSmb && hasNntp)
            Log($"job '{job.Name}': SMB+NNTP targets — using local staging for fan-out.");
        else
            Log($"job '{job.Name}': no network target — staging disk image locally.");
        return repo.BackupDiskImage(job.Source, imageName);
    }

    /// <summary>
    /// Resolves a job's effective targets into typed locations, with the same
    /// legacy "nntp"/"smb" fallback as <see cref="UploadToTargets"/>.
    /// </summary>
    private List<(string Name, string Type, BackupLocation? Loc)> ResolveTargets(BackupJobConfig job)
    {
        var result = new List<(string, string, BackupLocation?)>();
        foreach (string targetName in job.EffectiveTargets)
        {
            BackupLocation? loc = _config.Locations.FirstOrDefault(l =>
                l.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            string type = loc?.Type ??
                (targetName.Equals("nntp", StringComparison.OrdinalIgnoreCase) ? "nntp" :
                 targetName.Equals("smb", StringComparison.OrdinalIgnoreCase) ? "smb" : "");
            if (type == "nntp" || type == "smb" || type == "usb")
                result.Add((targetName, type, loc));
        }
        return result;
    }

    /// <summary>
    /// Decides whether a job should use batched staging + pruning. Requires
    /// at least one remote target (nntp/smb/usb) — local-only backups keep
    /// their chunks — and a local chunk store. For incrementals in volume
    /// mode, the parent's volumes must cover every parent chunk; otherwise
    /// the child's manifest couldn't be self-contained (parent chunks are
    /// pruned and can't be re-packed), so we fall back to legacy staging.
    /// </summary>
    private bool ShouldUseBatchedStaging(
        BackupJobConfig job, BackupRepository repo, out BackupManifest? parentManifest)
    {
        parentManifest = null;
        if (ResolveTargets(job).Count == 0)
            return false;
        if (repo.LocalChunkStore is null)
            return false;

        if (!job.IsDiskImage && job.Mode != "full")
        {
            string? parentId = LatestBackupIdForSource(repo, job.Source);
            if (parentId is not null)
            {
                parentManifest = repo.LoadManifest(parentId);
                if (repo.UseVolumes)
                {
                    var covered = new HashSet<string>(StringComparer.Ordinal);
                    if (parentManifest.Volumes is not null)
                        foreach (var v in parentManifest.Volumes)
                            foreach (string id in v.ChunkIds)
                                covered.Add(id);
                    foreach (var f in parentManifest.Files)
                        foreach (string id in f.Chunks)
                            if (!string.IsNullOrEmpty(id) && !covered.Contains(id))
                            {
                                Log($"job '{job.Name}': parent backup has chunks outside volumes; " +
                                    "using legacy full staging for this backup.");
                                parentManifest = null;
                                return false;
                            }
                }
            }
        }
        return true;
    }

    private Nntp.NntpBatchUploader CreateBatchUploader(
        BackupRepository repo, string backupId, BackupJobConfig job, NntpConfig nntp)
    {
        string? nntpPassword = Environment.GetEnvironmentVariable(NntpPasswordEnvVar);
        if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(nntp.PasswordProtected))
            nntpPassword = Dpapi.Unprotect(nntp.PasswordProtected);
        string redundancy = !string.IsNullOrEmpty(nntp.RedundancyMode)
            ? nntp.RedundancyMode
            : job.RedundancyMode;
        return new Nntp.NntpBatchUploader(
            repo, backupId, nntp, nntpPassword, redundancy, job.ParityGroupSize, Log);
    }

    /// <summary>
    /// Runs a backup with batched staging + pruning: one batch at a time,
    /// each batch is verified locally, uploaded/copied to every target,
    /// confirmed, then pruned from the local repo. Manifests, the catalog,
    /// and the upload journal are never pruned. Returns the finalized
    /// manifest (already uploaded to NNTP targets when present).
    /// </summary>
    private BackupManifest RunBatchedBackup(
        BackupJobConfig job, BackupRepository repo, BackupManifest? parentManifest,
        ISnapshotProvider? snap, CancellationToken token)
    {
        var targets = ResolveTargets(job);
        string backupId = Guid.NewGuid().ToString("N");
        bool useVolumes = repo.UseVolumes;
        var localStore = repo.LocalChunkStore!; // guaranteed by ShouldUseBatchedStaging

        var uploaders = new List<Nntp.NntpBatchUploader>();
        var smbConns = new List<(string Name, SmbShare Share)>();
        try
        {
            // NNTP uploaders (one per provider).
            foreach (var (name, type, loc) in targets)
            {
                if (type != "nntp")
                    continue;
                if (loc is null)
                {
                    foreach (var provider in _nntpProviders)
                        uploaders.Add(CreateBatchUploader(repo, backupId, job, provider));
                }
                else
                {
                    var provider = new NntpConfig
                    {
                        Host = loc.Host,
                        Port = loc.Port,
                        Ssl = loc.Ssl,
                        Username = loc.Username,
                        Connections = loc.Connections,
                        Newsgroup = loc.Newsgroup,
                        RetentionDays = loc.RetentionDays,
                        PasswordProtected = loc.PasswordProtected,
                        RedundancyMode = loc.RedundancyMode,
                    };
                    uploaders.Add(CreateBatchUploader(repo, backupId, job, provider));
                }
            }

            // Batch size: smallest probed ceiling across providers (90%
            // margin), else the 100 MB default volume size. SMB/USB-only
            // jobs use a single batch.
            long batchSize = uploaders.Count > 0
                ? uploaders.Min(u => u.BatchSizeBytes)
                : long.MaxValue;
            Log($"job '{job.Name}': batched staging (batch={FormatBatchSize(batchSize)}, " +
                $"volumes={(useVolumes ? "on" : "off")}, targets={targets.Count})...");

            // SMB connections, held open for the whole backup.
            var smbSync = new SmbSync(Log);
            foreach (var (name, type, loc) in targets)
            {
                if (type != "smb")
                    continue;
                string share = loc?.Share ?? job.SmbShare;
                string user = loc?.SmbUser ?? job.SmbUser;
                string? passwordProtected = loc?.SmbPasswordProtected ?? job.SmbPasswordProtected;
                if (string.IsNullOrWhiteSpace(share))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' targets SMB but no share is configured.");
                string? smbPassword = Environment.GetEnvironmentVariable(SmbPasswordEnvVar);
                if (string.IsNullOrEmpty(smbPassword) && !string.IsNullOrEmpty(passwordProtected))
                    smbPassword = Dpapi.Unprotect(passwordProtected);
                smbConns.Add((loc?.Name ?? name, SmbShare.Connect(share, user, smbPassword)));
            }

            var usbLocs = targets
                .Where(t => t.Type == "usb" && t.Loc is not null)
                .Select(t => t.Loc!)
                .ToList();

            // USB self-backup guard: resolve once up front (drive letters
            // can change, but the guard is about the drive identity).
            foreach (var usbLoc in usbLocs)
            {
                string? usbPath = UsbDriveResolver.ResolveBackupPath(usbLoc.VolumeSerial, usbLoc.UsbFolder);
                if (usbPath is not null && !string.IsNullOrWhiteSpace(job.Source))
                    StorageGuard.EnsureUsbDriveNotSource(usbPath, job.Source, job.Name);
            }

            var tracker = new BatchTrackingBlobStore(localStore, repo.CatalogHasChunk);
            var batchedVolumes = new List<VolumeEntry>();

            bool OnFlushBatch(IReadOnlyList<string> chunkIds, int batchIndex)
            {
                token.ThrowIfCancellationRequested();
                // 1. Verify staged chunks locally before upload (fail closed).
                foreach (string id in chunkIds)
                    repo.VerifyChunk(id);
                // 2. NNTP: upload the batch to every provider in parallel
                //    (per-batch parity + retry inside the uploader). Volume
                //    entries are deterministic across providers (same chunks,
                //    same packing), so we take them from the first uploader.
                var entries = new List<VolumeEntry>();
                if (uploaders.Count == 1)
                {
                    entries.AddRange(uploaders[0].UploadBatch(chunkIds, batchIndex, useVolumes));
                }
                else if (uploaders.Count > 1)
                {
                    var uploaderEntries = new List<VolumeEntry>?[uploaders.Count];
                    Parallel.For(0, uploaders.Count, i =>
                    {
                        uploaderEntries[i] = uploaders[i].UploadBatch(chunkIds, batchIndex, useVolumes);
                    });
                    // Deterministic: all providers produce identical entries.
                    entries.AddRange(uploaderEntries[0]!);
                }
                lock (batchedVolumes)
                    batchedVolumes.AddRange(entries);
                // 3. SMB: copy the batch's chunks.
                foreach (var (shareName, share) in smbConns)
                {
                    int copied = smbSync.CopyChunks(chunkIds, job.Repo, share);
                    Log($"job '{job.Name}': batch {batchIndex}: {copied} chunks copied to SMB '{shareName}'.");
                }
                // 4. USB: copy the batch's chunks (graceful skip if unplugged).
                foreach (var usbLoc in usbLocs)
                    CopyBatchChunksToUsb(usbLoc, chunkIds, job);
                // 5. Prune: the batch is confirmed at every destination.
                foreach (string id in chunkIds)
                    localStore.Delete(id);
                Log($"job '{job.Name}': batch {batchIndex} complete — " +
                    $"{chunkIds.Count} chunks uploaded, pruned locally.");
                OperationLog.Append(job.Repo, "batch-flush",
                    $"job={job.Name} batch={batchIndex} chunks={chunkIds.Count}");
                return true;
            }

            // Pipelined: staging of batch N+1 overlaps upload of batch N.
            // Uploads run serially in order on a background task; FlushRemainder
            // waits for completion and propagates failures.
            using var batchCtx = new PipelinedBackupContext(tracker, batchSize, OnFlushBatch);

            BackupManifest manifest;
            if (job.IsDiskImage)
            {
                string imageName = DiskImageName(job.Name);
                string? volumeLetter = GetVolumeLetterForDevice(job.Source);
                manifest = repo.BackupDiskImageBatched(
                    job.Source, imageName, job.Sparse, volumeLetter, batchCtx, backupId);
            }
            else
            {
                manifest = repo.BackupDirectoryBatched(
                    job.Source, parentManifest, snap, batchCtx,
                    VerificationModeParser.Parse(job.VerificationMode), backupId);
            }
            batchCtx.FlushRemainder();

            // VSS: writer session finalized only after data is safely stored.
            if (snap is Recovery.VssSnapshot vss)
                vss.Complete();

            // Manifest volumes: this backup's batches, plus inherited parent
            // volumes for incrementals (eligibility pre-checked above).
            manifest.Volumes ??= new List<VolumeEntry>();
            manifest.Volumes.AddRange(batchedVolumes);
            if (parentManifest is not null && useVolumes)
            {
                if (!BackupRepository.TryInheritParentVolumes(manifest, parentManifest, out var inherited))
                    throw new InvalidOperationException(
                        $"job '{job.Name}': parent volume inheritance failed unexpectedly.");
                manifest.Volumes.AddRange(inherited);
            }

            StampYubiKeyChallenge(manifest);
            manifest = repo.FinalizeBatchedManifest(manifest);

            // NNTP: post the manifest + index per provider.
            foreach (var uploader in uploaders)
            {
                int articles = uploader.UploadManifest();
                Log($"job '{job.Name}': manifest posted to {uploader.Host} ({articles} articles).");
                OperationLog.Append(job.Repo, "auto-upload",
                    $"job={job.Name} id={manifest.BackupId} host={uploader.Host} manifest_articles={articles}");
            }

            // SMB: final metadata sync (chunks were copied per batch).
            foreach (var (shareName, share) in smbConns)
            {
                var result = smbSync.Sync(job.Repo, share);
                Log($"job '{job.Name}': SMB metadata sync to '{shareName}' complete.");
            }

            // USB: final metadata copy (chunks were copied per batch).
            foreach (var usbLoc in usbLocs)
                CopyMetadataToUsb(usbLoc, job);

            Log($"job '{job.Name}': batched backup {manifest.BackupId} complete " +
                $"({manifest.Files.Count} files, {batchedVolumes.Count} new volumes).");
            return manifest;
        }
        finally
        {
            foreach (var uploader in uploaders)
                uploader.Dispose();
            foreach (var (_, share) in smbConns)
                share.Dispose();
        }
    }

    private static string FormatBatchSize(long bytes) =>
        bytes == long.MaxValue ? "whole backup"
        : bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F1} GiB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MiB"
        : $"{bytes / 1024.0:F1} KiB";

    /// <summary>
    /// Copies one batch's chunk blobs to a USB location. Graceful skip (not
    /// failure) when the drive isn't currently plugged in.
    /// </summary>
    private void CopyBatchChunksToUsb(BackupLocation usbLoc, IReadOnlyList<string> chunkIds, BackupJobConfig job)
    {
        string? targetDir = UsbDriveResolver.ResolveBackupPath(usbLoc.VolumeSerial, usbLoc.UsbFolder);
        if (targetDir is null)
        {
            Log($"job '{job.Name}': USB drive for '{usbLoc.Name}' not present — skipping batch USB copy.");
            return;
        }
        int copied = 0;
        string chunksDir = Path.Combine(job.Repo, "chunks");
        string targetChunks = Path.Combine(targetDir, "chunks");
        foreach (string id in chunkIds)
        {
            string src = Path.Combine(chunksDir, id[..2], id[2..]);
            if (!File.Exists(src))
                continue;
            string dst = Path.Combine(targetChunks, id[..2], id[2..]);
            if (CopyFileIfDifferent(src, dst))
                copied++;
        }
        Log($"job '{job.Name}': {copied} chunks copied to USB '{usbLoc.Name}'.");
    }

    /// <summary>
    /// Copies manifests + repo.json to a USB location after all batches
    /// (chunks were copied per batch). Never pruned locally.
    /// </summary>
    private void CopyMetadataToUsb(BackupLocation usbLoc, BackupJobConfig job)
    {
        string? targetDir = UsbDriveResolver.ResolveBackupPath(usbLoc.VolumeSerial, usbLoc.UsbFolder);
        if (targetDir is null)
        {
            Log($"job '{job.Name}': USB drive for '{usbLoc.Name}' not present — skipping metadata copy.");
            return;
        }
        string manifestsDir = Path.Combine(job.Repo, "manifests");
        if (Directory.Exists(manifestsDir))
            foreach (string src in Directory.GetFiles(manifestsDir, "*.json"))
                CopyFileIfDifferent(src, Path.Combine(targetDir, "manifests", Path.GetFileName(src)));
        string repoJson = Path.Combine(job.Repo, "repo.json");
        if (File.Exists(repoJson))
            CopyFileIfDifferent(repoJson, Path.Combine(targetDir, "repo.json"));
        Log($"job '{job.Name}': metadata copied to USB '{usbLoc.Name}'.");
    }

    private static bool CopyFileIfDifferent(string src, string dst)
    {
        var srcInfo = new FileInfo(src);
        var dstInfo = new FileInfo(dst);
        if (dstInfo.Exists && dstInfo.Length == srcInfo.Length)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        string tmp = dst + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
        return true;
    }

    /// <summary>
    /// Streams a disk image directly to an SMB share (sparse: used space only).
    /// </summary>
    private BackupManifest StreamDiskImageToSmb(
        BackupJobConfig job, BackupRepository repo, string imageName, BackupLocation? smbLoc)
    {
        string share = smbLoc?.Share ?? job.SmbShare;
        string user = smbLoc?.SmbUser ?? job.SmbUser;
        string? passwordProtected = smbLoc?.SmbPasswordProtected ?? job.SmbPasswordProtected;
        if (string.IsNullOrWhiteSpace(share))
            throw new InvalidOperationException(
                $"Job '{job.Name}' targets SMB but no share is configured.");

        string? smbPassword = Environment.GetEnvironmentVariable(SmbPasswordEnvVar);
        if (string.IsNullOrEmpty(smbPassword) && !string.IsNullOrEmpty(passwordProtected))
            smbPassword = Dpapi.Unprotect(passwordProtected);

        string? volumeLetter = GetVolumeLetterForDevice(job.Source);

        Log($"job '{job.Name}': streaming disk image to SMB share '{share}' " +
            $"(sparse={job.Sparse})...");
        using var smb = SmbShare.Connect(share, user, smbPassword);
        var store = new SmbBlobStore(smb);
        var manifest = repo.BackupDiskImageStreaming(
            job.Source, store, imageName, sparse: job.Sparse, volumeLetter: volumeLetter);
        Log($"job '{job.Name}': disk image {manifest.BackupId} streamed to SMB.");
        OperationLog.Append(job.Repo, "disk-image-stream",
            $"job={job.Name} id={manifest.BackupId} share={share} sparse={job.Sparse}");
        return manifest;
    }

    /// <summary>
    /// Streams a disk image directly to Usenet via volume packing (sparse:
    /// used space only). Chunks are packed into 100MB volumes and posted as
    /// they accumulate — no local staging. The manifest is written locally;
    /// post it via the dashboard's manual upload to make the backup
    /// discoverable.
    /// </summary>
    private BackupManifest StreamDiskImageToNntp(
        BackupJobConfig job, BackupRepository repo, string imageName,
        BackupLocation? nntpLoc, CancellationToken token)
    {
        // Build the NNTP provider from the location or legacy config.
        NntpConfig provider;
        string newsgroup;
        if (nntpLoc is null)
        {
            // Legacy: use the scheduler's configured providers.
            if (_nntpProviders.Count == 0)
                throw new InvalidOperationException(
                    $"Job '{job.Name}' targets NNTP but no provider is configured.");
            provider = _nntpProviders[0];
            newsgroup = provider.Newsgroup;
        }
        else
        {
            provider = new NntpConfig
            {
                Host = nntpLoc.Host ?? "",
                Port = nntpLoc.Port,
                Ssl = nntpLoc.Ssl,
                Username = nntpLoc.Username ?? "",
                PasswordProtected = nntpLoc.PasswordProtected,
            };
            newsgroup = nntpLoc.Newsgroup ?? provider.Newsgroup;
        }

        string? volumeLetter = GetVolumeLetterForDevice(job.Source);

        Log($"job '{job.Name}': streaming disk image to Usenet " +
            $"(sparse={job.Sparse}, volumes=100MB)...");
        // Env var takes precedence; otherwise decrypt the DPAPI-stored password.
        string? nntpPassword = Environment.GetEnvironmentVariable(NntpPasswordEnvVar);
        if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(provider.PasswordProtected))
            nntpPassword = Dpapi.Unprotect(provider.PasswordProtected);

        using var client = new Nntp.NntpClient(provider.Host, provider.Port, provider.Ssl);
        client.Connect();
        if (!string.IsNullOrEmpty(provider.Username))
            client.Authenticate(provider.Username, nntpPassword ?? "");
        using var nntp = new NntpBlobStore(
            client, newsgroup, repo.RepoId, repo.CatalogPath,
            from: repo.GetOrCreatePoster());
        using var streaming = new StreamingVolumeBlobStore(nntp);
        var manifest = repo.BackupDiskImageStreaming(
            job.Source, streaming, imageName, sparse: job.Sparse, volumeLetter: volumeLetter);
        streaming.Flush();

        // Record the streamed volumes in the manifest BEFORE posting it, so a
        // restore from Usenet alone can find the volume articles. Mirrors the
        // non-streamed upload path (set volumes + volume size, then save).
        manifest.Volumes = streaming.PostedVolumes
            .Select(v => new VolumeEntry
            {
                Id = v.VolumeId,
                ChunkIds = v.ChunkIds.ToList(),
                // Not tracked in streaming mode; informational only.
                SizeBytes = 0,
            })
            .ToList();
        manifest.VolumeSize = repo.VolumeSizeBytes;
        repo.SaveManifest(manifest);

        // Post the manifest now that all of its volumes are confirmed on the
        // server (Flush is synchronous; WaitForArticle ran per volume). A
        // failed manifest post does NOT fail the backup — the volumes are
        // safe on Usenet and the post stays retryable via journal + STAT
        // idempotency.
        const int maxManifestAttempts = 3;
        Exception? manifestError = null;
        for (int attempt = 1; attempt <= maxManifestAttempts; attempt++)
        {
            try
            {
                int articles = repo.UploadManifest(manifest.BackupId, nntp);
                Log($"job '{job.Name}': manifest posted to Usenet ({articles} articles).");
                manifestError = null;
                break;
            }
            catch (Exception ex)
            {
                manifestError = ex;
                Log($"job '{job.Name}': manifest post attempt {attempt} failed: {ex.Message}");
                if (attempt < maxManifestAttempts)
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }
        if (manifestError is not null)
        {
            Log($"job '{job.Name}': WARNING: manifest for {manifest.BackupId} could not be " +
                $"posted after {maxManifestAttempts} attempts: {manifestError.Message}. " +
                "Volumes are on Usenet; re-run the job or use manual manifest upload to retry.");
            OperationLog.Append(job.Repo, "manifest-upload-failed",
                $"job={job.Name} id={manifest.BackupId} error={manifestError.Message}");
        }

        Log($"job '{job.Name}': disk image {manifest.BackupId} streamed to Usenet " +
            $"({streaming.PostedVolumes.Count} volumes).");
        OperationLog.Append(job.Repo, "disk-image-stream",
            $"job={job.Name} id={manifest.BackupId} target=nntp volumes={streaming.PostedVolumes.Count} sparse={job.Sparse}");
        return manifest;
    }

    /// <summary>
    /// Derives a volume letter (e.g. "C:") from a device path, for the NTFS
    /// bitmap. Returns null if the path isn't a volume path.
    /// </summary>
    private static string? GetVolumeLetterForDevice(string devicePath)
    {
        var volMatch = System.Text.RegularExpressions.Regex.Match(
            devicePath, @"\\\\\.\\([A-Za-z]):", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return volMatch.Success
            ? volMatch.Groups[1].Value.ToUpperInvariant() + ":"
            : null;
    }

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

    /// <summary>
    /// Syncs a backup to a USB removable drive location. The drive is
    /// identified by volume serial (stable across letter changes). If the
    /// drive isn't plugged in, this logs and returns gracefully — it does
    /// NOT throw, so a missing drive is a skip, not a failure.
    /// </summary>
    private void SyncToUsbLocation(BackupJobConfig job, string backupId, BackupLocation? loc)
    {
        string locName = loc?.Name ?? "usb";
        string serial = loc?.VolumeSerial ?? "";
        string folder = loc?.UsbFolder ?? "FileKeep";
        if (string.IsNullOrWhiteSpace(serial))
            throw new InvalidOperationException(
                $"Job '{job.Name}' targets USB location '{locName}' but no volume serial is configured.");

        string? targetDir = UsbDriveResolver.ResolveBackupPath(serial, folder);
        if (targetDir is null)
        {
            // Graceful skip: drive not present. Log it, don't fail the job.
            Log($"job '{job.Name}': USB drive for '{locName}' (serial {serial}) not present — skipping USB sync.");
            OperationLog.Append(job.Repo, "usb-sync-skipped",
                $"job={job.Name} location={locName} reason=drive-not-present");
            return;
        }

        // Guard: don't back up a drive to itself.
        foreach (string src in job.EffectiveSources)
            if (!string.IsNullOrWhiteSpace(src))
                StorageGuard.EnsureUsbDriveNotSource(targetDir, src, job.Name);

        Log($"job '{job.Name}': syncing backup {backupId} to USB '{locName}' ({targetDir})");
        var result = SyncRepoToLocalFolder(job.Repo, targetDir);
        Log($"job '{job.Name}': USB sync to '{locName}' complete — {result.ChunksCopied} chunks, " +
            $"{result.ManifestsCopied} manifests copied.");
        OperationLog.Append(job.Repo, "usb-sync",
            $"job={job.Name} location={locName} path={targetDir} chunks={result.ChunksCopied}");
    }

    /// <summary>
    /// Copies chunks, manifests, parity and repo.json from a local repo to a
    /// local folder (e.g. on a USB drive). Idempotent: existing files with
    /// matching sizes are skipped. Returns counts.
    /// </summary>
    private (int ChunksCopied, int ManifestsCopied) SyncRepoToLocalFolder(string repoRoot, string targetDir)
    {
        int chunksCopied = 0, manifestsCopied = 0;
        string chunksDir = Path.Combine(repoRoot, "chunks");
        string manifestsDir = Path.Combine(repoRoot, "manifests");

        string targetChunks = Path.Combine(targetDir, "chunks");
        string targetManifests = Path.Combine(targetDir, "manifests");
        Directory.CreateDirectory(targetChunks);
        Directory.CreateDirectory(targetManifests);

        if (Directory.Exists(chunksDir))
        {
            foreach (string src in Directory.GetFiles(chunksDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(chunksDir, src);
                string dst = Path.Combine(targetChunks, rel);
                if (CopyIfDifferent(src, dst))
                    chunksCopied++;
            }
        }
        if (Directory.Exists(manifestsDir))
        {
            foreach (string src in Directory.GetFiles(manifestsDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(manifestsDir, src);
                string dst = Path.Combine(targetManifests, rel);
                if (CopyIfDifferent(src, dst))
                    manifestsCopied++;
            }
        }
        // Copy repo.json so the USB drive is a self-contained recoverable repo.
        string repoJson = Path.Combine(repoRoot, "repo.json");
        if (File.Exists(repoJson))
            CopyIfDifferent(repoJson, Path.Combine(targetDir, "repo.json"));

        return (chunksCopied, manifestsCopied);
    }

    /// <summary>Copies src to dst if dst is missing or differs in size. Returns true if copied.</summary>
    private static bool CopyIfDifferent(string src, string dst)
    {
        var srcInfo = new FileInfo(src);
        var dstInfo = new FileInfo(dst);
        if (dstInfo.Exists && dstInfo.Length == srcInfo.Length)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
        return true;
    }

    private void AutoUploadToUsenet(BackupJobConfig job, string backupId, string passphrase)
    {
        AutoUploadToUsenetWithProviders(job, backupId, passphrase, _nntpProviders);
    }

    private void AutoUploadToUsenetWithProviders(
        BackupJobConfig job, string backupId, string passphrase,
        IReadOnlyList<NntpConfig> providers)
    {
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
                    providerKey: Nntp.ChunkMessageIndex.MakeProviderKey(nntp.Host, nntp.Newsgroup),
                    from: repo.GetOrCreatePoster());
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
                    // (groupIndex, entry) pairs so the manifest keeps PlanVolumes
                    // order — part numbers are index-derived and must agree
                    // across the CLI, scheduler, and NZB paths.
                    var volumeEntries = new List<(int Index, VolumeEntry Entry)>();
                    if (useVolumes)
                    {
                        progress.SetPhase("volumes");
                        // The post set reads as an ordinary multi-part binary post:
                        // part 1 is the NFO, parts 2..n+1 are the RAR-named volumes.
                        string setName = Nntp.ArticleCodec.SetName(manifest.BackupId);
                        int totalParts = volumeGroups.Count + 1;
                        string nfoText = Nntp.ArticleCodec.BuildNfoText(
                            manifest.BackupId,
                            manifest.CreatedUtc.ToString("yyyy-MM-dd"),
                            volumeGroups.Count,
                            repo.GetOrCreatePoster());
                        store.PostNfo(manifest.BackupId, nfoText, setName, totalParts);
                        progress.RecordUploaded(1, nfoText.Length);
                        Parallel.For(0, volumeGroups.Count, parallelOptions, i =>
                        {
                            var group = volumeGroups[i];
                            var volume = Nntp.VolumePacker.BuildVolume(group, repo.GetChunkBlob);
                            lock (volumeEntries)
                                volumeEntries.Add((i, new VolumeEntry
                                {
                                    Id = volume.Id,
                                    ChunkIds = volume.ChunkIds.ToList(),
                                    SizeBytes = volume.Bytes.Length,
                                }));
                            if (store.Exists(volume.Id))
                            {
                                Interlocked.Increment(ref skipped);
                                progress.RecordSkipped(1);
                            }
                            else
                            {
                                store.PutVolume(volume.Id, volume.Bytes, setName, i + 2, totalParts);
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
                                .OrderBy(x => x.Index).Select(x => x.Entry).ToList();
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
                                e => e.Entry.Id, e => (IReadOnlyList<string>)e.Entry.ChunkIds);
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
                                        ? Redundancy.XorParity.GenerateParity(group, GetVolumeBytes, null, job.ParityGroupSize)
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
                                        chunkIds, id => repo.GetChunkBlob(id), null, job.ParityGroupSize)
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
        // Measure with the real v2 framing (dummy set name keeps the size exact).
        string poster = repo.GetOrCreatePoster();
        string article = Nntp.ArticleCodec.BuildVolumeArticle(
            volume.Id, repo.RepoId, volume.Bytes, nntp.Newsgroup, poster, "00000000", 2, 2);
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
            string probeArticle = Nntp.ArticleCodec.BuildProbeArticle(
                payload, nntp.Newsgroup, poster, out _);
            try
            {
                using var client = new Nntp.NntpClient(nntp.Host, nntp.Port, nntp.Ssl);
                client.Connect();
                if (!string.IsNullOrEmpty(nntp.Username))
                    client.Authenticate(nntp.Username, nntpPassword ?? "");
                client.Post(probeArticle);
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
