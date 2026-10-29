namespace FileKeep.Core.Service;

using System.Text.Json;
using FileKeep.Core.Json;
using FileKeep.Core.Nntp;
using FileKeep.Core.Security;

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

    /// <summary>
    /// P2-D9: the anchor for a (re)built job state. NextAfter alone can
    /// re-select an occurrence that already ran today — the concrete
    /// case is a service restart inside the repeated fall-back hour,
    /// where wall-clock 01:30 exists twice and the persisted last run
    /// (01:30, first pass) doesn't stop a second 01:30 run. When the
    /// last run started on the candidate's date at or after the
    /// candidate's time, that occurrence is spoken for: advance to the
    /// following one. Interval schedules are exempt (their occurrences
    /// have no date identity).
    /// </summary>
    public DateTime AnchorAfter(DateTime now, DateTime? lastRunStart)
    {
        var candidate = NextAfter(now);
        if (lastRunStart is { } last && this is not Interval)
        {
            while (candidate.Date == last.Date && last.TimeOfDay >= candidate.TimeOfDay)
                candidate = NextAfter(candidate);
        }
        return candidate;
    }

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
    /// Set when the job has several backup locations and at least one
    /// received the backup while at least one did not — skipped by the
    /// availability gate or failed during upload. The job is degraded,
    /// not failed: the dashboard shows a yellow warning with this note
    /// as the reason. Null when every location received the backup, or
    /// when none did (that is <see cref="AutoUploadFailed"/> territory),
    /// or for single-location jobs.
    /// </summary>
    public string? DegradedNote { get; init; }
    /// <summary>
    /// True when auto-verify ran and found corruption. The backup may be
    /// damaged — investigate immediately.
    /// </summary>
    public bool VerifyFailed { get; init; }
    public string? VerifyError { get; init; }
    /// <summary>
    /// MU-16: true when this "result" records a due run that was SKIPPED
    /// (paused / no location / drive or network away) rather than a run
    /// that happened. Success is false, but a skip is not a failure:
    /// ConsecutiveFailures is untouched and surfaces render it
    /// neutrally. The reason is in <see cref="Error"/>.
    /// </summary>
    public bool Skipped { get; init; }
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

    /// <summary>
    /// Recorded result of this job's last network try: true when every
    /// network target was unreachable at the last due time (or manual
    /// run), so the job is derived-paused as WaitingForNetwork. Runtime
    /// state only — learned from tries, never probed in the background,
    /// and cleared by the first try that finds a usable target.
    /// </summary>
    public bool NetworkAway { get; set; }

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
    public const string PassphraseEnvVar = "FILEKEEP_PASSPHRASE";
    public const string NntpPasswordEnvVar = "FILEKEEP_NNTP_PASSWORD";
    public const string SmbPasswordEnvVar = "FILEKEEP_SMB_PASSWORD";

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
        TryGetPassphrase(config, out string? passphrase, out _);
        return passphrase;
    }

    /// <summary>
    /// Resolves the passphrase like <see cref="GetPassphrase"/>, but
    /// reports WHY resolution failed when a stored passphrase exists
    /// (MU-12): a DPAPI blob that no longer decrypts — the service
    /// account or machine changed — used to be swallowed and reported
    /// as "no passphrase configured," sending the user to set something
    /// that exists but is broken. <paramref name="problem"/> is null
    /// when the passphrase resolved OR when none is configured at all
    /// (callers phrase that case themselves); it is set exactly when a
    /// stored passphrase is present but undecryptable.
    /// </summary>
    public static bool TryGetPassphrase(ServiceConfig config,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? passphrase,
        out string? problem)
    {
        passphrase = null;
        problem = null;
        try
        {
            // Precedence (SecretResolver): env var wins, else the DPAPI
            // blob, else none. The helper returns null exactly when no
            // blob exists, so problem stays null in that case, as before;
            // it throws only when a blob exists but does not decrypt.
            passphrase = SecretResolver.ResolvePassword(PassphraseEnvVar, config.PassphraseProtected);
            return passphrase is not null;
        }
        catch (Exception ex)
        {
            problem = "The stored passphrase cannot be decrypted on this " +
                "machine or Windows account (it was protected for a different " +
                "user or machine, e.g. after a service-account change). " +
                "Re-enter the passphrase in Settings \u2192 Security. " +
                $"({ex.GetType().Name}: {ex.Message})";
            return false;
        }
    }

    private readonly List<JobState> _jobs;
    private readonly string? _runStateFilePath;
    private readonly IClock _clock;
    private readonly TimeSpan _pollInterval;
    private readonly Action<string>? _log;
    private readonly Func<BackupLocation, bool> _networkReachable;
    /// <summary>
    /// Guards short-lived scheduler state: _jobs, _config, _nntpProviders,
    /// _lastRetentionCheck, _activeOperationCts. Never held
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
    /// <summary>SF-12: after a failed retention pass, the earliest time
    /// it may run again — a short retry delay instead of suppressing
    /// checks for the whole interval.</summary>
    private DateTime _nextRetentionRetry = DateTime.MinValue;

    // Live progress for the backup run in flight — the job cards'
    // progress row reads it via GetRunProgress. Published by Execute
    // for the run's whole duration; volatile because dashboard request
    // threads read it while run threads publish/clear it. Runs are
    // serialized by _operationLock, so a single tracker suffices.
    private volatile RunProgressTracker? _activeRun;

    /// <summary>Snapshot of the running backup's progress, or null
    /// when no backup run is in flight.</summary>
    public RunProgressSnapshot? GetRunProgress() => _activeRun?.Snapshot();

    /// <summary>The tracker the in-flight run publishes into (null
    /// outside a run). Run-path helpers update it null-conditionally.</summary>
    private RunProgressTracker? RunProgress => _activeRun;

    private int _userCancelRequested;

    /// <summary>
    /// Cancels the in-flight backup run for the named job (the job
    /// card's Cancel button). Returns false when that job is not the
    /// one running. The cancellation is marked user-requested so the
    /// run's callers record a cancellation, not a failure; a service
    /// stop goes through CancelActiveOperation directly and keeps
    /// its existing behavior.
    /// </summary>
    public bool CancelRun(string jobName)
    {
        var active = _activeRun;
        if (active is null ||
            !active.JobName.Equals(jobName, StringComparison.OrdinalIgnoreCase))
            return false;
        Interlocked.Exchange(ref _userCancelRequested, 1);
        CancelActiveOperation();
        return true;
    }

    /// <summary>Consumes the user-cancel mark (true exactly once per
    /// CancelRun). Run callers that caught a cancellation check it to
    /// tell a user's Cancel apart from a service stop.</summary>
    private bool ConsumeUserCancel()
        => Interlocked.Exchange(ref _userCancelRequested, 0) == 1;

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
        TimeSpan? pollInterval = null, Action<string>? log = null,
        Func<BackupLocation, bool>? networkReachable = null,
        string? runStateFilePath = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        _log = log;
        _networkReachable = networkReachable ?? NetworkProbe.IsLocationReachable;
        _runStateFilePath = runStateFilePath;
        StartedLocal = _clock.Now;
        _config = config;
        _nntpProviders = config.EffectiveProviders;
        _jobs = config.Jobs.Select(j =>
        {
            var schedule = ScheduleParser.Parse(j.Schedule);
            return new JobState(j, schedule, schedule.NextAfter(_clock.Now));
        }).ToList();
        LoadRunState();
        // P2-D9: with persisted run history merged, re-anchor any job
        // whose next occurrence is one it already ran (fall-back
        // restart double-run).
        foreach (var state in _jobs)
        {
            if (state.LastResult is not null)
                state.NextRunLocal = state.Schedule.AnchorAfter(
                    _clock.Now, state.LastResult.StartedLocal);
        }
    }

    // ---------- run-state persistence (MU-16) ----------
    //
    // LastResult / ConsecutiveFailures used to live only in memory:
    // after any service restart every job showed "never run" regardless
    // of its real history. When the host supplies a state file path,
    // the run history is written there after every outcome change and
    // reloaded at startup. Best-effort throughout: a state-file problem
    // must never break a backup, so load/save failures only log.

    // Internal (not private) so the source-generated RunStateJsonContext
    // below can register it: P2-R1 — the MU-16 code serialized this map
    // with reflection JSON, which throws in the trimmed published
    // service, so run history silently never survived a restart.
    // Source-gen only, like every other persisted type.
    internal sealed class PersistedJobRun
    {
        public JobRunResult? LastResult { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    private void LoadRunState()
    {
        if (_runStateFilePath is null || !File.Exists(_runStateFilePath))
            return;
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize(
                File.ReadAllText(_runStateFilePath),
                RunStateJsonContext.Default.DictionaryStringPersistedJobRun);
            if (map is null)
                return;
            foreach (var state in _jobs)
            {
                if (map.TryGetValue(state.Config.Name, out var saved))
                {
                    state.LastResult = saved.LastResult;
                    state.ConsecutiveFailures = saved.ConsecutiveFailures;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"run-state load failed ({_runStateFilePath}): {ex.Message}");
        }
    }

    /// <summary>
    /// Snapshots run history. Call with _stateLock held; pass the result
    /// to <see cref="WriteRunState"/> AFTER releasing the lock.
    /// </summary>
    private Dictionary<string, PersistedJobRun> SnapshotRunState()
    {
        var map = new Dictionary<string, PersistedJobRun>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in _jobs)
            map[state.Config.Name] = new PersistedJobRun
            {
                LastResult = state.LastResult,
                ConsecutiveFailures = state.ConsecutiveFailures,
            };
        return map;
    }

    /// <summary>
    /// Writes a run-history snapshot to disk. P2-C16: this file IO used
    /// to happen while holding _stateLock (the old PersistRunState was
    /// called from inside the lock at every call site), stalling every
    /// status read behind a disk write. Call WITHOUT _stateLock held.
    /// </summary>
    private void WriteRunState(Dictionary<string, PersistedJobRun> map)
    {
        if (_runStateFilePath is null)
            return;
        try
        {
            // Unique temp name (P2-C16): a fixed ".tmp" collides between
            // overlapping saves and is left behind by a kill mid-write.
            string tmp = _runStateFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(map,
                RunStateJsonContext.Default.DictionaryStringPersistedJobRun));
            File.Move(tmp, _runStateFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"run-state save failed ({_runStateFilePath}): {ex.Message}");
        }
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
            // Snapshot the job configs via the store: UpsertJob/DeleteJob
            // may replace the list concurrently (D5). SnapshotJobs
            // copies under the store lock internally, so no explicit
            // lock is needed here.
            List<BackupJobConfig> jobConfigs = config.SnapshotJobs();
            foreach (var jc in jobConfigs)
            {
                var schedule = ScheduleParser.Parse(jc.Schedule);
                var state = new JobState(jc, schedule, schedule.NextAfter(now));
                // Preserve run history for unchanged jobs.
                if (oldByName.TryGetValue(jc.Name, out var old))
                {
                    state.LastResult = old.LastResult;
                    state.ConsecutiveFailures = old.ConsecutiveFailures;
                    state.NetworkAway = old.NetworkAway;
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
        // A user-cancel mark belongs to the operation it was pressed
        // during; never let one leak into the next operation.
        Interlocked.Exchange(ref _userCancelRequested, 0);
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
    /// P2-C3: the gate dashboard-initiated long operations must hold.
    /// Six dashboard starts (SMB sync, verify, restore, adhoc backup,
    /// disk backup, disk restore) used to bypass _operationLock
    /// entirely, so they ran CONCURRENTLY with scheduled runs against
    /// the same repo — competing chunk Put/prune and cross-instance
    /// index saves. The scope blocks until the gate is free (the same
    /// serialization every scheduler operation accepts), registers the
    /// operation so CancelActiveOperation can stop it on service stop,
    /// and releases both on Dispose. Enter and Dispose must happen on
    /// the same thread (the lock is a Monitor) — dashboard tasks do
    /// both inside their Task.Run body.
    /// </summary>
    public DashboardOperationScope BeginDashboardOperation()
    {
        System.Threading.Monitor.Enter(_operationLock);
        try
        {
            return new DashboardOperationScope(this, BeginOperation());
        }
        catch
        {
            System.Threading.Monitor.Exit(_operationLock);
            throw;
        }
    }

    /// <summary>See <see cref="BeginDashboardOperation"/>.</summary>
    public sealed class DashboardOperationScope : IDisposable
    {
        private readonly BackupScheduler _owner;
        private bool _disposed;

        internal DashboardOperationScope(BackupScheduler owner, CancellationToken token)
        {
            _owner = owner;
            Token = token;
        }

        /// <summary>Cancelled by CancelActiveOperation (service stop).</summary>
        public CancellationToken Token { get; }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner.EndOperation();
            System.Threading.Monitor.Exit(_owner._operationLock);
        }
    }

    /// <summary>
    /// Cancels the currently running operation, if any. Called on service
    /// stop. Since NS-3/NS-5 the token reaches into the backup/restore
    /// work itself, so the abort lands at the next file/chunk boundary
    /// rather than waiting for a whole phase to finish.
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
    /// When stopping fires, any active operation is cancelled (NS-3/NS-5:
    /// the token is threaded into the backup/restore work itself).
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
            catch (Exception ex)
            {
                // SF-18: a fault in one scan — e.g. the dashboard
                // mutating the config's collections mid-enumeration, or
                // a reachability probe throwing — must not kill the
                // scheduler. Log it and try again next cycle; an escaped
                // exception would fault this task (and, via the host's
                // supervision, take the whole service down).
                Log($"scheduler: due-job scan failed, will retry next cycle: {ex.Message}");
            }
            try { RunRetentionIfDue(_clock.Now, stopping); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"scheduler: retention pass failed, will retry next cycle: {ex.Message}");
            }
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
            if (now < _nextRetentionRetry)
                return;
        }

        // SF-12: the interval stamp used to be taken BEFORE the pass
        // ran, so one transient failure (provider down, repo locked)
        // suppressed retention checks for the whole interval — and the
        // pass's own per-repo failures never reached this method at
        // all. Now the stamp is taken only on success; a failure
        // retries after ~15 minutes instead.
        bool ok;
        try
        {
            ok = RunRetentionChecks(stopping);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"retention check failed: {ex.Message}");
            ok = false;
        }
        lock (_stateLock)
        {
            if (ok)
            {
                _lastRetentionCheck = now;
                _nextRetentionRetry = DateTime.MinValue;
            }
            else
            {
                _nextRetentionRetry = now + TimeSpan.FromMinutes(15);
            }
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
                if (!TryGetPassphrase(_config, out string? passphrase, out string? problem))
                    throw new InvalidOperationException(
                        problem ?? $"No passphrase (set {PassphraseEnvVar}).");
            // Manual upload is a repo-scoped operation: it posts to the
            // configured global Usenet providers directly. It does not
            // go through job target resolution (there is no job — the
            // synthetic config only carries the repo/name the upload
            // path logs against).
            var job = new BackupJobConfig
            {
                Name = "manual-upload",
                Repo = repoPath,
            };
            AutoUploadToUsenet(job, backupId, passphrase, token);
            }
            finally { EndOperation(); }
        }
    }

    /// <summary>
    /// Downloads chunks referenced by an NZB file from the first configured provider.
    /// </summary>
    public void DownloadFromNzb(string repoPath, string nzbPath, string passphrase)
    {
        // P2-V3 (NS-4 residual): this was the one long operation that
        // never took _operationLock nor registered with BeginOperation,
        // so it ran concurrently with job runs/uploads/retention on the
        // same repo and CancelActiveOperation could not stop it.
        lock (_operationLock)
        {
            BeginOperation();
            try
            {
                DownloadFromNzbCore(repoPath, nzbPath, passphrase);
            }
            finally
            {
                EndOperation();
            }
        }
    }

    private void DownloadFromNzbCore(string repoPath, string nzbPath, string passphrase)
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
        string? nntpPassword = SecretResolver.ResolvePassword(NntpPasswordEnvVar, nntp.PasswordProtected);

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
                messageIndex: repo.MessageIndex, providerKey: providerKey,
                indexAuth: repo.IndexAuth);
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
    /// <summary>Runs retention checks for every job repo.
    /// Returns false when any repo's check failed (SF-12: the caller
    /// uses this to decide whether the interval stamp may advance).</summary>
    private bool RunRetentionChecks(CancellationToken stopping = default)
    {
        IReadOnlyList<NntpConfig> providers;
        lock (_stateLock)
            providers = _nntpProviders;
        if (providers.Count == 0)
        {
            Log("retention check skipped: no NNTP providers configured");
            return true;
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
        bool allOk = true;
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
                        allOk = false;
                    }
                }
            }
            finally { EndOperation(); }
        }
        return allOk;
    }

    private void RunRetentionForRepo(string repoPath, IReadOnlyList<NntpConfig> providers, bool dryRun = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!TryGetPassphrase(_config, out string? passphrase, out string? problem))
        {
            Log($"retention check skipped for '{repoPath}': " +
                (problem ?? $"no passphrase (set {PassphraseEnvVar})"));
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

                string? nntpPassword = SecretResolver.ResolvePassword(NntpPasswordEnvVar, nntp.PasswordProtected);

                var pool = new Nntp.NntpConnectionPool(
                    nntp.Host, nntp.Port, nntp.Ssl,
                    username: nntp.Username, password: nntpPassword,
                    size: nntp.Connections);
                var store = new Nntp.NntpBlobStore(
                    pool, nntp.Newsgroup, repo.RepoId, repo.CatalogPath,
                    messageIndex: messageIndex,
                    providerKey: Nntp.ChunkMessageIndex.MakeProviderKey(nntp.Host, nntp.Newsgroup),
                    indexAuth: repo.IndexAuth);
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
                dryRun: dryRun,
                cancellationToken: token);

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
    /// Network away-ness is never probed here — status reads pass the
    /// recorded result of the job's last try, so rendering a page never
    /// touches the network.
    /// </summary>
    public JobRunState EvaluateJobState(BackupJobConfig job) =>
        EvaluateJobState(job, recordedNetworkAway: false);

    public JobRunState EvaluateJobState(BackupJobConfig job, bool recordedNetworkAway)
    {
        var cfg = _config;
        if (cfg is null)
            return job.Paused ? JobRunState.UserPaused : JobRunState.Active;
        return JobStateEvaluator.Evaluate(job,
            JobStateEvaluator.BuildLookup(cfg, _ => recordedNetworkAway),
            TryGetPassphrase(cfg, out _, out _));
    }

    /// <summary>
    /// The availability gate, evaluated at a due time or a manual run —
    /// the only places network locations are ever probed. Each of the
    /// job's network locations gets one bounded reachability try. When
    /// the job can run, also returns the config to execute: a copy
    /// filtered to the targets usable right now, so an away target is
    /// skipped for this run (and, per the no-caching rule, gets nothing
    /// later). Stored pauses and target-less jobs short-circuit before
    /// any probe.
    /// </summary>
    private (JobRunState State, BackupJobConfig? Runnable,
        IReadOnlyList<(string Name, string Reason)> Skipped) Gate(BackupJobConfig job)
    {
        var cfg = _config;
        if (cfg is null)
            return (job.Paused ? JobRunState.UserPaused : JobRunState.Active, job,
                Array.Empty<(string, string)>());
        var probeCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Away(BackupLocation loc)
        {
            if (!probeCache.TryGetValue(loc.Name, out bool reachable))
            {
                reachable = _networkReachable(loc);
                probeCache[loc.Name] = reachable;
            }
            return !reachable;
        }
        var lookup = JobStateEvaluator.BuildLookup(cfg, Away);
        var state = JobStateEvaluator.Evaluate(job, lookup,
            TryGetPassphrase(cfg, out _, out _));
        if (state != JobRunState.Active)
            return (state, null, Array.Empty<(string, string)>());
        var usable = new List<string>();
        var skipped = new List<(string, string)>();
        foreach (string t in job.EffectiveTargets)
        {
            var f = lookup(t);
            // A target naming no location resolves to nothing (Phase 6):
            // never usable, and when another target IS usable it is
            // reported as skipped so the run's degraded note names it
            // instead of silently dropping it (SF-21).
            if (f.Exists && (f.IsUsb ? f.UsbPresent : !f.NetworkAway))
                usable.Add(t);
            else
                skipped.Add((t, !f.Exists
                    ? "backup location not found"
                    : f.IsUsb
                        ? "USB drive not connected"
                        : "network location unavailable"));
        }
        return (state, usable.Count == job.EffectiveTargets.Count
            ? job
            : job.CloneWithTargets(usable), skipped);
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
        foreach (var job in due)
        {
            stopping.ThrowIfCancellationRequested();
            var (state, runnable, skipped) = Gate(job.Config);
            if (state != JobRunState.Active)
            {
                Dictionary<string, PersistedJobRun>? runStateToWrite = null;
                lock (_stateLock)
                {
                    if (_jobs.Contains(job))
                    {
                        job.NextRunLocal = job.Schedule.NextAfter(now);
                        if (state == JobRunState.WaitingForNetwork)
                            job.NetworkAway = true;
                        // MU-16: record the skip as the job's latest
                        // outcome — without it the dashboard kept
                        // showing the previous success as if current.
                        // A skip is not a failure: ConsecutiveFailures
                        // is untouched.
                        job.LastResult = new JobRunResult
                        {
                            JobName = job.Config.Name,
                            StartedLocal = now,
                            Success = false,
                            Skipped = true,
                            Error = $"Skipped — {JobStateEvaluator.Describe(state)}",
                        };
                        runStateToWrite = SnapshotRunState();
                    }
                }
                if (runStateToWrite is not null)
                    WriteRunState(runStateToWrite);
                Log($"job '{job.Config.Name}' skipped ({JobStateEvaluator.Describe(state)}); " +
                    $"next run {job.NextRunLocal:yyyy-MM-dd HH:mm}");
                continue;
            }
            lock (_stateLock) { job.NetworkAway = false; }
            RunOne(job, now, stopping, runnable, skipped);
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
        var (state, runnable, skipped) = Gate(config);
        if (state != JobRunState.Active)
        {
            if (state == JobRunState.WaitingForNetwork)
                lock (_stateLock)
                {
                    var js = _jobs.FirstOrDefault(j => j.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (js is not null) js.NetworkAway = true;
                }
            throw new InvalidOperationException(
                $"Job '{name}' cannot run: {JobStateEvaluator.Describe(state)}.");
        }
        lock (_stateLock)
        {
            var js = _jobs.FirstOrDefault(j => j.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (js is not null) js.NetworkAway = false;
        }
        JobRunResult result;
        lock (_operationLock)
        {
            CancellationToken token = BeginOperation();
            try { result = Execute(runnable ?? config, _clock.Now, token, skipped); }
            catch (OperationCanceledException) when (ConsumeUserCancel())
            {
                result = new JobRunResult
                {
                    JobName = config.Name,
                    StartedLocal = _clock.Now,
                    Success = false,
                    Skipped = true,
                    Error = "Cancelled",
                };
            }
            finally { EndOperation(); }
        }
        Dictionary<string, PersistedJobRun>? runStateToWrite = null;
        lock (_stateLock)
        {
            var job = _jobs.FirstOrDefault(j => j.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (job is not null)
            {
                job.LastResult = result;
                job.ConsecutiveFailures = result.Success
                    ? 0
                    : result.Skipped ? job.ConsecutiveFailures : job.ConsecutiveFailures + 1;
                // Re-anchor the schedule so "run now" doesn't cause an immediate re-run.
                job.NextRunLocal = job.Schedule.NextAfter(_clock.Now);
                runStateToWrite = SnapshotRunState();
            }
        }
        if (runStateToWrite is not null)
            WriteRunState(runStateToWrite);
        return result;
    }

    private void RunOne(JobState job, DateTime now, CancellationToken stopping,
        BackupJobConfig? configOverride = null,
        IReadOnlyList<(string Name, string Reason)>? gateSkipped = null)
    {
        JobRunResult result;
        lock (_operationLock)
        {
            CancellationToken token = BeginOperation();
            try { result = Execute(configOverride ?? job.Config, now, token, gateSkipped); }
            catch (OperationCanceledException) when (ConsumeUserCancel())
            {
                result = new JobRunResult
                {
                    JobName = job.Config.Name,
                    StartedLocal = now,
                    Success = false,
                    Skipped = true,
                    Error = "Cancelled",
                };
            }
            finally { EndOperation(); }
        }
        Dictionary<string, PersistedJobRun>? runStateToWrite = null;
        lock (_stateLock)
        {
            // The job may have been removed by a concurrent ReloadJobs; only
            // update state if it's still in the list.
            if (_jobs.Contains(job))
            {
                job.LastResult = result;
                job.ConsecutiveFailures = result.Success
                    ? 0
                    : result.Skipped ? job.ConsecutiveFailures : job.ConsecutiveFailures + 1;
                // P2-D8: anchor from COMPLETION, not the due-scan
                // instant — a run longer than its interval used to
                // re-arm into the past and immediately run back-to-back.
                job.NextRunLocal = job.Schedule.NextAfter(_clock.Now);
                runStateToWrite = SnapshotRunState();
            }
        }
        if (runStateToWrite is not null)
            WriteRunState(runStateToWrite);
        stopping.ThrowIfCancellationRequested();
    }

    private JobRunResult Execute(BackupJobConfig job, DateTime now, CancellationToken token,
        IReadOnlyList<(string Name, string Reason)>? gateSkipped = null)
        => Execute(job, now, token, requireTargets: true, opName: "scheduled-backup", gateSkipped);

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
        if (!TryGetPassphrase(_config, out string? passphrase, out string? problem))
            throw new InvalidOperationException(problem ??
                ($"Cannot run a backup without a passphrase: set the {PassphraseEnvVar} " +
                 "environment variable for the service account."));
        if (!File.Exists(Path.Combine(repoPath, "repo.json")))
            BackupRepository.Init(repoPath, passphrase);
        var job = new BackupJobConfig
        {
            Name = "One-time backup",
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
        bool requireTargets, string opName,
        IReadOnlyList<(string Name, string Reason)>? gateSkipped = null)
    {
        // Publish live progress for the whole run — the job cards'
        // progress row reads it via GetRunProgress. Cleared in
        // finally, so a finished, failed, or cancelled run's row
        // collapses instead of going stale.
        var tracker = new RunProgressTracker(job.Name);
        _activeRun = tracker;
        try { return ExecuteRun(job, now, token, requireTargets, opName, gateSkipped); }
        finally { if (ReferenceEquals(_activeRun, tracker)) _activeRun = null; }
    }

    private JobRunResult ExecuteRun(BackupJobConfig job, DateTime now, CancellationToken token,
        bool requireTargets, string opName,
        IReadOnlyList<(string Name, string Reason)>? gateSkipped = null)
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
            if (!TryGetPassphrase(_config, out string? passphrase, out string? problem))
                throw new InvalidOperationException(problem ??
                    ($"Cannot run scheduled backup without a passphrase: set the {PassphraseEnvVar} " +
                     "environment variable for the service account."));

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
                IReadOnlyList<(string Target, string? Error)> outcomes =
                    Array.Empty<(string, string?)>();
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (batched)
                        (manifest, outcomes) = RunBatchedBackup(job, repo, batchParent, null, token);
                    else
                        manifest = RunDiskImageJob(job, repo, token);
                }
                catch
                {
                    throw;
                }
                return FinishBackup(job, repo,
                    new[] { (manifest, batched, outcomes) }, passphrase, token, opName, now, gateSkipped);
            }

            // File jobs: one manifest per source folder. Each source gets
            // its own snapshot provider (VSS snapshots are per-volume) and
            // its own incremental parent chain (matched by source folder),
            // so folders never diff against each other.
            var done = new List<(BackupManifest Manifest, bool Batched,
                IReadOnlyList<(string Target, string? Error)> Outcomes)>();
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
                IReadOnlyList<(string Target, string? Error)> outcomes =
                    Array.Empty<(string, string?)>();
                try
                {
                    if (batched)
                    {
                        (manifest, outcomes) = RunBatchedBackup(srcJob, repo, batchParent, snap, token);
                    }
                    else if (srcJob.ParsedMode == BackupMode.Full)
                    {
                        // NS-3: the operation token reaches into the
                        // backup itself — a stop aborts at the next file
                        // boundary instead of waiting for the whole run.
                        manifest = repo.BackupDirectory(srcJob.FirstSource, snap, token);
                    }
                    else
                    {
                        string? parent = LatestBackupIdForSource(repo, srcJob.FirstSource);
                        manifest = parent is null
                            ? repo.BackupDirectory(srcJob.FirstSource, snap, token)
                            : repo.BackupIncremental(srcJob.FirstSource, parent, snap,
                                VerificationModeParser.Parse(srcJob.VerificationMode),
                                cancellationToken: token);
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
                RunProgress?.AddFilesScanned(manifest.Files.Count);
                done.Add((manifest, batched, outcomes));
            }
            return FinishBackup(job, repo, done, passphrase, token, opName, now, gateSkipped);
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
        IReadOnlyList<(BackupManifest Manifest, bool Batched,
            IReadOnlyList<(string Target, string? Error)> Outcomes)> done,
        string passphrase, CancellationToken token, string opName, DateTime now,
        IReadOnlyList<(string Name, string Reason)>? gateSkipped = null)
    {
        // Run progress: destinations are known here even for the
        // non-batched paths (batched runs registered theirs already —
        // registration is idempotent). Non-batched runs still owe
        // their uploads and possibly an auto-verify.
        {
            var rp = RunProgress;
            if (rp is not null)
            {
                rp.SetPhase(RunPhases.Finishing);
                foreach (var (tName, tType, _) in ResolveTargets(job))
                {
                    // tType comes from a validated location type, so the
                    // parse cannot fail; the string stays the display kind.
                    var tKind = LocationKindParser.Parse(tType);
                    rp.RegisterDestination(tName, tType,
                        tKind == LocationKind.Nntp ? "items" : tKind == LocationKind.Smb ? "files" : "chunks");
                }
                rp.SetExpectVerify(job.AutoVerify && done.Any(d => !d.Batched));
            }
        }
        bool autoUploadFailed = false;
        string? autoUploadError = null;
        bool verifyFailed = false;
        string? verifyError = null;
        int totalFiles = 0;
        // Per-target upload outcomes across all manifests, for the
        // degraded (partial delivery) determination at the end. Batched
        // runs report theirs per source; legacy runs fill this in below
        // from their own UploadToTargets calls.
        var uploadOutcomes = new List<(string Target, string? Error)>();
        foreach (var d in done)
            uploadOutcomes.AddRange(d.Outcomes);
        foreach (var (manifest, batched, _) in done)
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
                    RunProgress?.SetPhase(RunPhases.Uploading);
                    UploadToTargets(job, manifest.BackupId, passphrase, token, uploadOutcomes);
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
                    RunProgress?.SetPhase(RunPhases.Verifying);
                    Log($"job '{job.Name}': auto-verifying {manifest.BackupId}...");
                    // P2-D6: Verify RETURNS its findings (it does not throw
                    // for chunk/file failures) — discarding the list made
                    // every auto-verify "OK" by construction.
                    var verifyIssues = repo.Verify(manifest.BackupId);
                    if (verifyIssues.Count == 0)
                    {
                        Log($"job '{job.Name}': auto-verify OK.");
                        OperationLog.Append(job.Repo, "auto-verify",
                            $"job={job.Name} id={manifest.BackupId} result=ok");
                    }
                    else
                    {
                        verifyFailed = true;
                        verifyError = verifyIssues.Count == 1
                            ? verifyIssues[0]
                            : $"{verifyIssues.Count} problems; first: {verifyIssues[0]}";
                        Log($"job '{job.Name}': auto-verify FAILED: {verifyError}");
                        OperationLog.Append(job.Repo, "auto-verify-failed",
                            $"job={job.Name} id={manifest.BackupId} error={verifyError}");
                    }
                }
                catch (Exception ex)
                {
                    verifyFailed = true;
                    verifyError = ex.Message;
                    Log($"job '{job.Name}': auto-verify FAILED: {ex.Message}");
                    OperationLog.Append(job.Repo, "auto-verify-failed",
                        $"job={job.Name} id={manifest.BackupId} error={ex.Message}");
                }
                RunProgress?.SetPhase(RunPhases.Finishing);
            }
        }

        // Degraded (partial delivery): the job has several locations, at
        // least one received this backup, and at least one did not —
        // gate-skipped or upload-failed. When NONE received it, the run
        // stays plain AutoUploadFailed with no note.
        string? degradedNote = null;
        string? deliveryFailure = null;
        {
            var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (target, error) in uploadOutcomes)
                if (error is not null && !failed.ContainsKey(target))
                    failed[target] = error.Length > 160 ? error.Substring(0, 157) + "…" : error;
            int delivered = uploadOutcomes
                .Where(o => o.Error is null)
                .Select(o => o.Target)
                .Where(t => !failed.ContainsKey(t))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var skipped = gateSkipped ?? Array.Empty<(string Name, string Reason)>();
            int totalTargets = job.EffectiveTargets.Count + skipped.Count;
            if (totalTargets >= 2 && delivered >= 1 && failed.Count + skipped.Count >= 1)
            {
                var parts = skipped
                    .Select(s => $"Location '{s.Name}' didn't receive this backup: {s.Reason}.")
                    .Concat(failed.Select(f =>
                        $"Location '{f.Key}' didn't receive this backup: {f.Value}"));
                degradedNote = string.Join(" ", parts);
            }

            // DL-5 + MU-19: a run where NO location received the backup
            // is a FAILED run, batched or not. The batched case is the
            // sharp one (the manifest stage failed everywhere, so no
            // destination can even discover the backup), but a legacy
            // run whose every target failed also used to report Success
            // with the failure parked in side flags the status surface
            // never showed — a green "backed up" for a backup no
            // destination holds. Partial delivery stays a warning
            // (degraded note / AutoUploadFailed): the local repo plus
            // at least one destination hold the backup there. A job
            // with no targets at all (local-only) is unaffected.
            if (delivered == 0 && totalTargets > 0 &&
                (failed.Count + skipped.Count > 0 || autoUploadFailed))
            {
                deliveryFailure = failed.Count > 0
                    ? $"Location '{failed.Keys.First()}' didn't receive this backup: {failed.Values.First()}"
                    : skipped.Count > 0
                        ? $"Location '{skipped[0].Name}' didn't receive this backup: {skipped[0].Reason}."
                        : $"Upload failed: {autoUploadError}";
                Log($"job '{job.Name}': FAILED: {deliveryFailure}");
                try
                {
                    OperationLog.Append(job.Repo, "scheduled-backup-failed",
                        $"job={job.Name} error={deliveryFailure}");
                }
                catch { /* the operations log must never mask the result */ }
            }
        }

        // MU-19: verify-detected corruption is likewise a failed run —
        // the status surface keys on Success, and "the backup may be
        // damaged" must not sit behind a green pill.
        string? runFailure = deliveryFailure ?? (verifyFailed
            ? $"Auto-verify found a problem with this backup: {verifyError}"
            : null);

        return new JobRunResult
        {
            JobName = job.Name,
            StartedLocal = now,
            Success = runFailure is null,
            Error = runFailure,
            BackupId = string.Join(",", done.Select(d => d.Manifest.BackupId)),
            Files = totalFiles,
            AutoUploadFailed = autoUploadFailed,
            AutoUploadError = autoUploadError,
            DegradedNote = degradedNote,
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
            return new BackupPrivilegeSnapshotProvider(job.FirstSource);
        if (job.Vss)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    $"Job '{job.Name}': VSS snapshots require Windows.");
            string helper = LocateVssHelper();
            string volume = Path.GetPathRoot(Path.GetFullPath(job.FirstSource))
                ?? throw new InvalidOperationException(
                    $"Job '{job.Name}': cannot determine volume for '{job.FirstSource}'.");
            // Scale the snapshot hold time: the helper aborts (deleting the
            // snapshot mid-backup) on timeout. Default 2h; configurable per job.
            int timeoutSecs = job.VssTimeoutSecs > 0 ? job.VssTimeoutSecs : 7200;
            var vss = new Recovery.VssSnapshot(helper, timeoutSecs);
            vss.Create(volume, job.FirstSource);
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
    /// from the FILEKEEP_NNTP_PASSWORD environment variable and is never
    /// stored. Throws on failure; the caller logs it.
    /// </summary>
    /// <summary>
    /// Uploads a completed backup to each of the job's configured targets
    /// (location names). Each target runs independently: one failing
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

    private void UploadToTargets(BackupJobConfig job, string backupId, string passphrase,
        CancellationToken token = default,
        List<(string Target, string? Error)>? outcomes = null)
    {
        token.ThrowIfCancellationRequested();
        var targets = job.EffectiveTargets;
        var errors = new List<string>();
        foreach (string targetName in targets)
        {
            // P2-C6: the token used to stop at this boundary — the legs
            // below ran uninterruptibly and a service stop killed the
            // process mid-post/mid-copy with no run outcome recorded.
            token.ThrowIfCancellationRequested();
            // Resolve the target name to a location. A name matching no
            // location resolves to nothing and lands in the error
            // outcome below (loaded configs reject unknown names at
            // validation; this covers in-memory jobs).
            BackupLocation? loc = _config.SnapshotLocations().FirstOrDefault(l =>
                l.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            string type = loc?.Type ?? "";
            try
            {
                if (type == "nntp")
                    UploadToNntpLocation(job, backupId, passphrase, loc!, token);
                else if (type == "smb")
                    SyncToSmbLocation(job, backupId, loc!, token);
                else if (type == "usb")
                    SyncToUsbLocation(job, backupId, loc, token);
                else
                    throw new InvalidOperationException(
                        $"Unknown backup location '{targetName}'.");
                outcomes?.Add((targetName, null));
                RunProgress?.CompleteDestination(targetName);
            }
            catch (Exception ex)
            {
                errors.Add($"{targetName}: {ex.Message}");
                outcomes?.Add((targetName, ex.Message));
                RunProgress?.FailDestination(targetName, ex.Message);
            }
        }
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Upload to {errors.Count} target(s) failed: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// Uploads to a Usenet location, using the location's own
    /// host/port/credentials.
    /// </summary>
    private void UploadToNntpLocation(
        BackupJobConfig job, string backupId, string passphrase, BackupLocation loc,
        CancellationToken token = default)
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
        };
        AutoUploadToUsenetWithProviders(job, backupId, passphrase,
            new List<NntpConfig> { provider }, token, loc.Name);
    }

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
        // Find SMB and NNTP targets. Only names that resolve to a
        // location count — unknown names resolve to nothing.
        bool hasSmb = false;
        bool hasNntp = false;
        BackupLocation? smbLoc = null;
        BackupLocation? nntpLoc = null;
        foreach (string targetName in job.EffectiveTargets)
        {
            var loc = _config.SnapshotLocations().FirstOrDefault(l =>
                l.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            string type = loc?.Type ?? "";
            if (type == "smb") { hasSmb = true; smbLoc ??= loc; }
            if (type == "nntp") { hasNntp = true; nntpLoc ??= loc; }
        }

        string imageName = DiskImageName(job.Name);

        // Stream to a single target when possible; fall back to local staging
        // when both are targeted (fan-out) or neither is.
        if (hasSmb && !hasNntp)
            return StreamDiskImageToSmb(job, repo, imageName, smbLoc!, token);
        if (hasNntp && !hasSmb)
            return StreamDiskImageToNntp(job, repo, imageName, nntpLoc!, token);

        if (hasSmb && hasNntp)
            Log($"job '{job.Name}': SMB+NNTP targets — using local staging for fan-out.");
        else
            Log($"job '{job.Name}': no network target — staging disk image locally.");
        return repo.BackupDiskImage(job.FirstSource, imageName, token);
    }

    /// <summary>
    /// Resolves a job's effective targets into typed locations. A target
    /// name that matches no location resolves to nothing and is simply
    /// absent from the result — there is no bare-token fallback
    /// (Phase 6), so every returned entry carries a real location.
    /// </summary>
    private List<(string Name, string Type, BackupLocation Loc)> ResolveTargets(BackupJobConfig job)
    {
        var result = new List<(string, string, BackupLocation)>();
        foreach (string targetName in job.EffectiveTargets)
        {
            BackupLocation? loc = _config.SnapshotLocations().FirstOrDefault(l =>
                l.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            if (loc is null)
                continue;
            string type = loc.Type ?? "";
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

        if (!job.IsDiskImage && job.ParsedMode != BackupMode.Full)
        {
            string? parentId = LatestBackupIdForSource(repo, job.FirstSource);
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
        string? nntpPassword = SecretResolver.ResolvePassword(NntpPasswordEnvVar, nntp.PasswordProtected);
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
    private (BackupManifest Manifest, List<(string Target, string? Error)> Outcomes) RunBatchedBackup(
        BackupJobConfig job, BackupRepository repo, BackupManifest? parentManifest,
        ISnapshotProvider? snap, CancellationToken token)
    {
        var targets = ResolveTargets(job);
        string backupId = Guid.NewGuid().ToString("N");
        bool useVolumes = repo.UseVolumes;
        var localStore = repo.LocalChunkStore!; // guaranteed by ShouldUseBatchedStaging

        // Per-target delivery tracking: a target that fails (connect,
        // batch copy/upload, manifest/metadata) is marked with its first
        // error and excluded from the rest of the run while the other
        // targets continue — the run comes back degraded, not failed.
        // Only a batch that reaches NO target at all fails the run.
        var failedTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deliveredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void MarkFailed(string name, Exception ex)
        {
            if (!failedTargets.ContainsKey(name))
            {
                failedTargets[name] = ex.Message;
                Log($"job '{job.Name}': backup location '{name}' failed and is out of this run: {ex.Message}");
                // The job card turns amber and the estimate rebuilds
                // around the destinations that are left.
                RunProgress?.FailDestination(name, ex.Message);
            }
        }

        // Run progress: every destination counts chunks in batched
        // mode (the batch is the unit of delivery for all of them).
        foreach (var (tName, tType, _) in targets)
            RunProgress?.RegisterDestination(tName, tType, "chunks");

        var uploaders = new List<(string Target, Nntp.NntpBatchUploader Uploader)>();
        var smbConns = new List<(string Name, SmbShare Share, BackupLocation Loc)>();
        try
        {
            // NNTP uploaders (one per NNTP location — ResolveTargets
            // only returns located targets, so the provider always
            // comes from the location itself).
            foreach (var (name, type, loc) in targets)
            {
                if (type != "nntp")
                    continue;
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
                uploaders.Add((name, CreateBatchUploader(repo, backupId, job, provider)));
            }

            // Batch size: smallest probed ceiling across providers (90%
            // margin), else the 100 MB default volume size. SMB/USB-only
            // jobs use a single batch.
            long batchSize = uploaders.Count > 0
                ? uploaders.Min(u => u.Uploader.BatchSizeBytes)
                : long.MaxValue;
            Log($"job '{job.Name}': batched staging (batch={FormatBatchSize(batchSize)}, " +
                $"volumes={(useVolumes ? "on" : "off")}, targets={targets.Count})...");

            // SMB connections, held open for the whole backup. Share
            // and credentials come from the location — the per-job SMB
            // fields were retired in Phase 6.
            var smbSync = new SmbSync(Log);
            foreach (var (name, type, loc) in targets)
            {
                if (type != "smb")
                    continue;
                string share = loc.Share;
                string user = loc.SmbUser;
                string? passwordProtected = loc.SmbPasswordProtected;
                if (string.IsNullOrWhiteSpace(share))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' targets SMB but no share is configured.");
                string? smbPassword = SecretResolver.ResolvePassword(SmbPasswordEnvVar, passwordProtected);
                try
                {
                    smbConns.Add((loc.Name, SmbShare.Connect(share, user, smbPassword), loc));
                }
                catch (Exception ex)
                {
                    MarkFailed(loc.Name, ex);
                }
            }

            // Every target already dead at setup and nothing else to
            // deliver to: fail fast, exactly as an unguarded connect
            // failure did before per-target isolation.
            if (targets.Count > 0 && uploaders.Count == 0 &&
                targets.All(t => failedTargets.ContainsKey(t.Item1)))
                throw new InvalidOperationException(failedTargets.Values.First());

            var usbLocs = targets
                .Where(t => t.Loc.ParsedKind == LocationKind.Usb)
                .Select(t => t.Loc)
                .ToList();

            // A resolved target with no concrete deliverer — an SMB
            // location whose connection just failed is the reachable
            // case — must not pass as a silent success: mark it failed
            // now so the run's outcome (and, if it is the only target,
            // the batch prune guard) treats it honestly.
            foreach (var (name, type, loc) in targets)
            {
                if (failedTargets.ContainsKey(name))
                    continue;
                bool hasDeliverer = type switch
                {
                    "nntp" => uploaders.Any(u => u.Target == name),
                    "smb" => smbConns.Any(c => c.Name == loc.Name),
                    _ => true, // usb: delivery is decided per batch (drive presence)
                };
                if (!hasDeliverer)
                    MarkFailed(name, new InvalidOperationException(
                        $"Backup location '{name}' has no deliverer for this run."));
            }

            // USB self-backup guard: resolve once up front (drive letters
            // can change, but the guard is about the drive identity).
            foreach (var usbLoc in usbLocs)
            {
                string? usbPath = UsbDriveResolver.ResolveBackupPath(usbLoc.VolumeSerial, usbLoc.UsbFolder);
                if (usbPath is not null && !string.IsNullOrWhiteSpace(job.FirstSource))
                    StorageGuard.EnsureUsbDriveNotSource(usbPath, job.FirstSource, job.Name);
            }

            // DL-1 delivery evidence for staging dedup: a chunk may be
            // skipped only when EVERY target of this run already has
            // it — flushed earlier in this run (runDelivered), recorded
            // in the provider-scoped upload journal for an NNTP target
            // (DL-2), or holding a blob that verifies at an SMB/USB
            // target. Catalog
            // knowledge alone is not evidence: it only proves a past
            // staging, possibly by a failed run or for another target.
            var runDelivered = new HashSet<string>(StringComparer.Ordinal);
            var runDeliveredLock = new object();
            bool IsDeliveredAtAllTargets(string chunkId)
            {
                lock (runDeliveredLock)
                {
                    if (runDelivered.Contains(chunkId))
                        return true;
                }
                foreach (var (_, type, loc) in targets)
                {
                    bool confirmed = type switch
                    {
                        "nntp" => repo.IsChunkUploadedToProvider(chunkId,
                                Nntp.ChunkMessageIndex.MakeProviderKey(loc.Host, loc.Newsgroup)),
                        // File destinations: the blob must VERIFY at the
                        // destination (decrypt + hash) — presence or size
                        // alone is not evidence (DL-4). Disguised targets
                        // resolve through the cover folder + derived name
                        // and verify header-stripped; plain targets use
                        // the classic sharded path.
                        "smb" => loc is not null && SmbChunkVerified(loc, chunkId),
                        "usb" => loc is not null && UsbChunkVerified(loc, chunkId),
                        _ => false,
                    };
                    if (!confirmed)
                        return false;
                }
                return true;
            }
            bool SmbChunkVerified(BackupLocation loc, string chunkId)
            {
                // The disguise slice's batched delivery check used the
                // classic path unconditionally, so it never found a
                // disguised blob and re-uploaded every run; with headers
                // it would additionally have failed verification. Both
                // are fixed here: disguised targets resolve + verify
                // through the layout, plain targets are untouched.
                if (loc.Disguise is { } smbLayout)
                    return repo.VerifyDisguisedChunkBlobAt(Path.Combine(
                        loc.Share, smbLayout.CoverName, smbLayout.ChunkFileName(chunkId)), chunkId);
                return repo.VerifyChunkBlobAt(Path.Combine(
                    loc.Share, "chunks", chunkId[..2], chunkId[2..]), chunkId);
            }

            bool UsbChunkVerified(BackupLocation loc, string chunkId)
            {
                string? root = UsbDriveResolver.ResolveBackupPath(
                    loc.VolumeSerial, DisguisedLayout.UsbFolderFor(loc));
                if (root is null)
                    return false;
                if (loc.Disguise is { } usbLayout)
                    return repo.VerifyDisguisedChunkBlobAt(Path.Combine(
                        root, usbLayout.ChunkFileName(chunkId)), chunkId);
                return repo.VerifyChunkBlobAt(Path.Combine(
                    root, "chunks", chunkId[..2], chunkId[2..]), chunkId);
            }

            var tracker = new BatchTrackingBlobStore(localStore, IsDeliveredAtAllTargets);
            var batchedVolumes = new List<VolumeEntry>();

            long flushedChunks = 0;

            bool OnFlushBatch(IReadOnlyList<string> chunkIds, int batchIndex)
            {
                token.ThrowIfCancellationRequested();
                RunProgress?.SetPhase(RunPhases.Uploading);
                // 1. Verify staged chunks locally before upload (fail closed).
                foreach (string id in chunkIds)
                    repo.VerifyChunk(id);
                // 2. NNTP: upload the batch to every provider in parallel
                //    (per-batch parity + retry inside the uploader). Volume
                //    entries are deterministic across providers (same chunks,
                //    same packing), so we take them from the first uploader
                //    that succeeded. A failed provider is out of the run;
                //    the remaining destinations still confirm the batch.
                var entries = new List<VolumeEntry>();
                bool batchDelivered = false;
                var activeUploaders = uploaders
                    .Where(u => !failedTargets.ContainsKey(u.Target)).ToList();
                if (activeUploaders.Count == 1)
                {
                    var (tName, up) = activeUploaders[0];
                    try
                    {
                        entries.AddRange(up.UploadBatch(chunkIds, batchIndex, useVolumes));
                        deliveredTargets.Add(tName);
                        batchDelivered = true;
                        RunProgress?.AddDestinationDone(tName, chunkIds.Count);
                    }
                    catch (Exception ex) { MarkFailed(tName, ex); }
                }
                else if (activeUploaders.Count > 1)
                {
                    var uploaderEntries = new List<VolumeEntry>?[activeUploaders.Count];
                    var uploaderErrors = new Exception?[activeUploaders.Count];
                    Parallel.For(0, activeUploaders.Count, i =>
                    {
                        try
                        {
                            uploaderEntries[i] = activeUploaders[i].Uploader
                                .UploadBatch(chunkIds, batchIndex, useVolumes);
                        }
                        catch (Exception ex) { uploaderErrors[i] = ex; }
                    });
                    for (int i = 0; i < activeUploaders.Count; i++)
                    {
                        if (uploaderErrors[i] is not null)
                        {
                            MarkFailed(activeUploaders[i].Target, uploaderErrors[i]!);
                            continue;
                        }
                        deliveredTargets.Add(activeUploaders[i].Target);
                        batchDelivered = true;
                        RunProgress?.AddDestinationDone(activeUploaders[i].Target, chunkIds.Count);
                        // Deterministic: all providers produce identical entries.
                        if (entries.Count == 0 && uploaderEntries[i] is not null)
                            entries.AddRange(uploaderEntries[i]!);
                    }
                }
                // (Volume entries are recorded after ALL delivery legs —
                // see the MU-18 note before the prune step.)
                // 3. SMB: copy the batch's chunks.
                foreach (var (shareName, share, smbLoc) in smbConns)
                {
                    if (failedTargets.ContainsKey(shareName))
                        continue;
                    try
                    {
                        var batchLayout = smbLoc.Disguise;
                        int copied = smbSync.CopyChunks(chunkIds, job.Repo, share,
                            batchLayout?.CoverName, batchLayout);
                        Log($"job '{job.Name}': batch {batchIndex}: {copied} chunks copied to SMB '{shareName}'.");
                        deliveredTargets.Add(shareName);
                        batchDelivered = true;
                        RunProgress?.AddDestinationDone(shareName, chunkIds.Count);
                    }
                    catch (Exception ex) { MarkFailed(shareName, ex); }
                }
                // 4. USB: copy the batch's chunks (graceful skip if unplugged).
                foreach (var usbLoc in usbLocs)
                {
                    if (failedTargets.ContainsKey(usbLoc.Name))
                        continue;
                    try
                    {
                        if (CopyBatchChunksToUsb(usbLoc, chunkIds, job))
                        {
                            deliveredTargets.Add(usbLoc.Name);
                            batchDelivered = true;
                            RunProgress?.AddDestinationDone(usbLoc.Name, chunkIds.Count);
                        }
                        else
                        {
                            // Drive not present: this target did NOT
                            // receive the batch. Record the miss so the
                            // prune guard and the run outcome treat it
                            // as a failure, never a silent skip.
                            MarkFailed(usbLoc.Name, new IOException(
                                $"USB drive for '{usbLoc.Name}' is not present."));
                        }
                    }
                    catch (Exception ex) { MarkFailed(usbLoc.Name, ex); }
                }
                // MU-18: volume entries describe the PACKING, not the
                // posting. When no NNTP uploader produced them (the job
                // has no NNTP target, or every provider failed this
                // batch) but a file destination received the batch,
                // compute them locally — the packing is deterministic,
                // so the entries are identical. Without this the
                // manifest's Volumes list silently omitted those
                // batches even though their chunks were pruned on
                // delivery, and volume-mode consumers saw a partial
                // packing for a complete backup.
                if (entries.Count == 0 && useVolumes && batchDelivered)
                {
                    try
                    {
                        entries.AddRange(Nntp.NntpBatchUploader.PlanVolumeEntries(
                            repo, backupId, chunkIds, batchIndex));
                    }
                    catch (Exception ex)
                    {
                        Log($"job '{job.Name}': batch {batchIndex}: local volume-entry " +
                            $"computation failed: {ex.Message}");
                    }
                }
                lock (batchedVolumes)
                    batchedVolumes.AddRange(entries);
                // 5. Prune: only a batch confirmed at ≥1 destination is
                //    pruned. A destination that missed it gets nothing
                //    later (no catch-up). If NO destination received
                //    the batch — every deliverer failed, or the targets
                //    resolved to no concrete deliverer at all — the
                //    staged chunks are the only copy: fail the run
                //    here and prune nothing.
                if (!batchDelivered)
                    throw new InvalidOperationException(
                        $"Batch {batchIndex}: no backup location received it; the run cannot continue.");
                // The batch is confirmed at ≥1 destination and every
                // other target either has it too or is out of the run
                // (failed targets get no catch-up) — so for the rest of
                // THIS run these chunks count as delivered for staging
                // dedup (DL-1), whatever earlier runs left behind.
                lock (runDeliveredLock)
                {
                    foreach (string id in chunkIds)
                        runDelivered.Add(id);
                }
                // Prune failures must not fail a delivered run (NS-12).
                // P2-R2/C10: exclude chunks a LATER batch has already
                // staged (duplicate content across files) — their blob
                // is that batch's upload source; pruning it here made
                // the later flush die on a missing blob. At this point
                // tracker.StagedIds holds only later batches' ids (the
                // pipelined context resets the set when a batch queues).
                var toPrune = ComputePruneSet(chunkIds, tracker.StagedIds);
                PruneStagedChunks(localStore, toPrune,
                    msg => Log($"job '{job.Name}': batch {batchIndex}: {msg}"));
                Log($"job '{job.Name}': batch {batchIndex} complete — " +
                    $"{chunkIds.Count} chunks uploaded, pruned locally.");
                // Totals are planned-so-far: flushed plus whatever
                // the pipelined staging already holds for later
                // batches. They settle to the final count as the walk
                // completes, and a destination's bar never passes its
                // own delivered count.
                flushedChunks += chunkIds.Count;
                foreach (var (tName, _, _) in targets)
                    if (!failedTargets.ContainsKey(tName))
                        RunProgress?.SetDestinationTotal(tName,
                            flushedChunks + tracker.StagedIds.Count);
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
                string? volumeLetter = GetVolumeLetterForDevice(job.FirstSource);
                manifest = repo.BackupDiskImageBatched(
                    job.FirstSource, imageName, job.Sparse, volumeLetter, batchCtx, backupId,
                    cancellationToken: token);
            }
            else
            {
                manifest = repo.BackupDirectoryBatched(
                    job.FirstSource, parentManifest, snap, batchCtx,
                    VerificationModeParser.Parse(job.VerificationMode), backupId,
                    cancellationToken: token);
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

            // P2-V2a/V2b: stamp each uploader's parity record and the
            // per-batch NFO dates into the manifest BEFORE the manifest
            // article is posted below, so the posted manifest carries
            // them. Retention reads both from the manifest; neither is
            // reconstructible from config or batch layout afterwards.
            bool uploadRecordStamped = false;
            foreach (var (tName, uploader) in uploaders)
            {
                if (failedTargets.ContainsKey(tName))
                    continue;
                manifest.ParityByProvider ??=
                    new Dictionary<string, ManifestParityRecord>();
                manifest.ParityByProvider[uploader.ProviderKey] =
                    uploader.ParityRecord;
                if (manifest.NfoDates is null && uploader.NfoDates is { } dates)
                    manifest.NfoDates = dates;
                uploadRecordStamped = true;
            }
            if (uploadRecordStamped)
                repo.SaveManifest(manifest);

            // NNTP: post the manifest + index per provider.
            foreach (var (tName, uploader) in uploaders)
            {
                if (failedTargets.ContainsKey(tName))
                    continue;
                try
                {
                    int articles = uploader.UploadManifest();
                    Log($"job '{job.Name}': manifest posted to {uploader.Host} ({articles} articles).");
                    OperationLog.Append(job.Repo, "auto-upload",
                        $"job={job.Name} id={manifest.BackupId} host={uploader.Host} manifest_articles={articles}");
                    deliveredTargets.Add(tName);
                }
                catch (Exception ex) { MarkFailed(tName, ex); }
            }

            // SMB: final metadata sync (chunks were copied per batch).
            foreach (var (shareName, share, smbLoc) in smbConns)
            {
                if (failedTargets.ContainsKey(shareName))
                    continue;
                try
                {
                    var finalLayout = smbLoc.Disguise;
                    var result = smbSync.Sync(job.Repo, share, finalLayout?.CoverName, finalLayout);
                    Log($"job '{job.Name}': SMB metadata sync to '{shareName}' complete.");
                    deliveredTargets.Add(shareName);
                }
                catch (Exception ex) { MarkFailed(shareName, ex); }
            }

            // USB: final metadata copy (chunks were copied per batch).
            foreach (var usbLoc in usbLocs)
            {
                if (failedTargets.ContainsKey(usbLoc.Name))
                    continue;
                try
                {
                    if (CopyMetadataToUsb(usbLoc, job))
                        deliveredTargets.Add(usbLoc.Name);
                    else
                        MarkFailed(usbLoc.Name, new IOException(
                            $"USB drive for '{usbLoc.Name}' is not present."));
                }
                catch (Exception ex) { MarkFailed(usbLoc.Name, ex); }
            }

            Log($"job '{job.Name}': batched backup {manifest.BackupId} complete " +
                $"({manifest.Files.Count} files, {batchedVolumes.Count} new volumes).");
            var outcomes = new List<(string Target, string? Error)>();
            foreach (var (name, _, _) in targets)
                outcomes.Add((name, failedTargets.TryGetValue(name, out var err) ? err : null));
            return (manifest, outcomes);
        }
        finally
        {
            foreach (var (_, uploader) in uploaders)
                uploader.Dispose();
            foreach (var (_, share, _) in smbConns)
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
    /// failure) when the drive isn't currently plugged in. Returns true
    /// when the drive was present and every chunk is confirmed there —
    /// present, with content matching the local blob. A missing local
    /// chunk or a copy that does not verify throws: the caller prunes
    /// the local copies on this method's success, so an unconfirmed
    /// chunk must never pass as delivered (DL-4).
    /// </summary>
    private bool CopyBatchChunksToUsb(BackupLocation usbLoc, IReadOnlyList<string> chunkIds, BackupJobConfig job)
    {
        string? targetDir = UsbDriveResolver.ResolveBackupPath(usbLoc.VolumeSerial, DisguisedLayout.UsbFolderFor(usbLoc));
        if (targetDir is null)
        {
            Log($"job '{job.Name}': USB drive for '{usbLoc.Name}' not present — skipping batch USB copy.");
            return false;
        }
        var layout = usbLoc.Disguise;
        int copied = 0;
        string chunksDir = Path.Combine(job.Repo, "chunks");
        string targetChunks = Path.Combine(targetDir, "chunks");
        foreach (string id in chunkIds)
        {
            string src = Path.Combine(chunksDir, id[..2], id[2..]);
            if (!File.Exists(src))
                throw new FileNotFoundException(
                    $"Chunk {id} is missing from the local staging repo; " +
                    "it cannot be confirmed at the USB destination.", src);
            string dst = layout is not null
                ? Path.Combine(targetDir, layout.ChunkFileName(id))
                : Path.Combine(targetChunks, id[..2], id[2..]);
            if (CopyFileWrappedOrPlain(src, dst, layout))
            {
                copied++;
                // Confirm the copy that just landed: a destination can
                // accept a write and still store different bytes.
                bool verified = layout is not null
                    ? DisguisedLayout.WrappedMatches(src, dst)
                    : FilesHaveSameContent(src, dst);
                if (!verified)
                    throw new IOException(
                        $"Chunk {id} did not verify at the USB destination after copying.");
            }
            // Not copied = already present with matching size AND hash
            // (CopyFileIfDifferent compared content) — confirmed.
        }
        Log($"job '{job.Name}': {copied} chunks copied to USB '{usbLoc.Name}'.");
        return true;
    }

    /// <summary>
    /// Copies manifests + repo.json to a USB location after all batches
    /// (chunks were copied per batch). Never pruned locally. Returns true
    /// when the drive was present and the copy ran.
    /// </summary>
    private bool CopyMetadataToUsb(BackupLocation usbLoc, BackupJobConfig job)
    {
        string? targetDir = UsbDriveResolver.ResolveBackupPath(usbLoc.VolumeSerial, DisguisedLayout.UsbFolderFor(usbLoc));
        if (targetDir is null)
        {
            Log($"job '{job.Name}': USB drive for '{usbLoc.Name}' not present — skipping metadata copy.");
            return false;
        }
        var layout = usbLoc.Disguise;
        string manifestsDir = Path.Combine(job.Repo, "manifests");
        if (Directory.Exists(manifestsDir))
            foreach (string src in Directory.GetFiles(manifestsDir, "*.json"))
                CopyFileWrappedOrPlain(src, layout is not null
                    ? Path.Combine(targetDir, layout.ManifestFileName(Path.GetFileName(src)))
                    : Path.Combine(targetDir, "manifests", Path.GetFileName(src)), layout);
        string repoJson = Path.Combine(job.Repo, "repo.json");
        if (File.Exists(repoJson))
            CopyFileWrappedOrPlain(repoJson, layout is not null
                ? Path.Combine(targetDir, layout.RepoFileName())
                : Path.Combine(targetDir, "repo.json"), layout);
        Log($"job '{job.Name}': metadata copied to USB '{usbLoc.Name}'.");
        return true;
    }

    private static bool CopyFileIfDifferent(string src, string dst)
    {
        var srcInfo = new FileInfo(src);
        var dstInfo = new FileInfo(dst);
        // Size alone is never trusted (DL-4): a same-length file counts
        // as in sync only when its content hash also matches.
        if (dstInfo.Exists && dstInfo.Length == srcInfo.Length
            && FilesHaveSameContent(src, dst))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        string tmp = dst + ".tmp-" + Guid.NewGuid().ToString("N"); // P2-C14: unique temp
        File.Copy(src, tmp, overwrite: true);
        try { File.Move(tmp, dst, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; } // P2-C15: don't leak the temp on failure
        return true;
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
    /// Streams a disk image directly to an SMB share (sparse: used space only).
    /// </summary>
    private BackupManifest StreamDiskImageToSmb(
        BackupJobConfig job, BackupRepository repo, string imageName, BackupLocation smbLoc,
        CancellationToken token = default)
    {
        string share = smbLoc.Share;
        string user = smbLoc.SmbUser;
        string? passwordProtected = smbLoc.SmbPasswordProtected;
        if (string.IsNullOrWhiteSpace(share))
            throw new InvalidOperationException(
                $"Job '{job.Name}' targets SMB but no share is configured.");

        string? smbPassword = SecretResolver.ResolvePassword(SmbPasswordEnvVar, passwordProtected);

        string? volumeLetter = GetVolumeLetterForDevice(job.FirstSource);

        Log($"job '{job.Name}': streaming disk image to SMB share '{share}' " +
            $"(sparse={job.Sparse})...");
        using var smb = SmbShare.Connect(share, user, smbPassword);
        var streamLayout = smbLoc.Disguise;
        var store = new SmbBlobStore(smb, streamLayout?.CoverName, streamLayout);
        var manifest = repo.BackupDiskImageStreaming(
            job.FirstSource, store, imageName, sparse: job.Sparse, volumeLetter: volumeLetter,
            cancellationToken: token);
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
        BackupLocation nntpLoc, CancellationToken token)
    {
        // Build the NNTP provider from the location.
        var provider = new NntpConfig
        {
            Host = nntpLoc.Host ?? "",
            Port = nntpLoc.Port,
            Ssl = nntpLoc.Ssl,
            Username = nntpLoc.Username ?? "",
            PasswordProtected = nntpLoc.PasswordProtected,
        };
        string newsgroup = nntpLoc.Newsgroup ?? provider.Newsgroup;

        string? volumeLetter = GetVolumeLetterForDevice(job.FirstSource);

        Log($"job '{job.Name}': streaming disk image to Usenet " +
            $"(sparse={job.Sparse}, volumes=100MB)...");
        // Env var takes precedence; otherwise decrypt the DPAPI-stored password.
        string? nntpPassword = SecretResolver.ResolvePassword(NntpPasswordEnvVar, provider.PasswordProtected);

        using var client = new Nntp.NntpClient(provider.Host, provider.Port, provider.Ssl);
        client.Connect();
        if (!string.IsNullOrEmpty(provider.Username))
            client.Authenticate(provider.Username, nntpPassword ?? "");
        // P2-R5: this was the one construction site still missing the
        // provider scope — its uploads journaled under the legacy ''
        // key DL-2 treats as evidence for NO provider, so streamed
        // volumes/manifests were invisible to delivery checks and
        // retention, and refreshed identities went unrecorded (no
        // message index). Mirror NntpBatchUploader exactly.
        using var nntp = new NntpBlobStore(
            client, newsgroup, repo.RepoId, repo.CatalogPath,
            from: repo.GetOrCreatePoster(),
            messageIndex: repo.MessageIndex,
            providerKey: ChunkMessageIndex.MakeProviderKey(provider.Host, newsgroup),
            indexAuth: repo.IndexAuth);
        using var streaming = new StreamingVolumeBlobStore(nntp,
            onFlushError: ex => Log($"job '{job.Name}': final volume flush on dispose failed: {ex.Message}"));
        var manifest = repo.BackupDiskImageStreaming(
            job.FirstSource, streaming, imageName, sparse: job.Sparse, volumeLetter: volumeLetter,
            cancellationToken: token);
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

    /// <summary>
    /// Syncs the repo to an SMB location. The share receives chunks,
    /// manifests, and repo.json — enough for a recovery wizard to open
    /// it directly as a read-only repo. Share and credentials come from
    /// the location; the per-job SMB fields were retired in Phase 6.
    /// </summary>
    private void SyncToSmbLocation(BackupJobConfig job, string backupId, BackupLocation loc,
        CancellationToken token = default)
    {
        string share = loc.Share;
        string user = loc.SmbUser;
        string? passwordProtected = loc.SmbPasswordProtected;
        if (string.IsNullOrWhiteSpace(share))
            throw new InvalidOperationException(
                $"Job '{job.Name}' targets an SMB location but no share is configured.");
        string? smbPassword = SecretResolver.ResolvePassword(SmbPasswordEnvVar, passwordProtected);

        string locName = loc.Name;
        Log($"job '{job.Name}': syncing backup {backupId} to '{locName}' ({share})");
        using var smb = SmbShare.Connect(share, user, smbPassword);
        var sync = new SmbSync(Log);
        var smbLayout = loc.Disguise;
        var result = sync.Sync(job.Repo, smb, smbLayout?.CoverName, smbLayout, progress: (doneFiles, totalFiles) =>
        {
            var rp = RunProgress;
            if (rp is null) return;
            rp.SetDestinationTotal(locName, totalFiles);
            rp.SetDestinationDone(locName, doneFiles);
        }, cancellationToken: token);
        Log($"job '{job.Name}': SMB sync to '{locName}' complete — {result.ChunksCopied} chunks, " +
            $"{result.ManifestsCopied} manifests copied.");
    }

    /// <summary>
    /// Syncs a backup to a USB removable drive location. The drive is
    /// identified by volume serial (stable across letter changes). If the
    /// drive isn't plugged in, this logs and returns gracefully — it does
    /// NOT throw, so a missing drive is a skip, not a failure.
    /// </summary>
    private void SyncToUsbLocation(BackupJobConfig job, string backupId, BackupLocation? loc,
        CancellationToken token = default)
    {
        string locName = loc?.Name ?? "usb";
        string serial = loc?.VolumeSerial ?? "";
        string folder = loc is not null ? DisguisedLayout.UsbFolderFor(loc) : "FileKeep";
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
        var result = SyncRepoToLocalFolder(job.Repo, targetDir, token, loc?.Disguise, (doneChunks, totalChunks) =>
        {
            var rp = RunProgress;
            if (rp is null) return;
            rp.SetDestinationTotal(locName, totalChunks);
            rp.SetDestinationDone(locName, doneChunks);
        });
        Log($"job '{job.Name}': USB sync to '{locName}' complete — {result.ChunksCopied} chunks, " +
            $"{result.ManifestsCopied} manifests copied.");
        OperationLog.Append(job.Repo, "usb-sync",
            $"job={job.Name} location={locName} path={targetDir} chunks={result.ChunksCopied}");
    }

    /// <summary>
    /// Copies chunks, manifests, parity and repo.json from a local repo to a
    /// local folder (e.g. on a USB drive). Idempotent: existing files with
    /// matching sizes are skipped. Returns counts.
    /// With a layout, the destination wears the disguised layout instead:
    /// all files sit flat in <paramref name="targetDir"/> under derived
    /// names, plus decoys when enabled (see <see cref="DisguisedLayout"/>).
    /// </summary>
    // Internal (not private) so tests can drive the USB folder sync
    // directly — same test-seam pattern as the connection-pool seam.
    internal (int ChunksCopied, int ManifestsCopied) SyncRepoToLocalFolder(
        string repoRoot, string targetDir, CancellationToken token = default,
        DisguisedLayout? layout = null,
        Action<int, int>? progress = null)
    {
        int chunksCopied = 0, manifestsCopied = 0;
        string chunksDir = Path.Combine(repoRoot, "chunks");
        string manifestsDir = Path.Combine(repoRoot, "manifests");

        string targetChunks = Path.Combine(targetDir, "chunks");
        string targetManifests = Path.Combine(targetDir, "manifests");
        if (layout is not null)
            Directory.CreateDirectory(targetDir);
        else
        {
            Directory.CreateDirectory(targetChunks);
            Directory.CreateDirectory(targetManifests);
        }

        int chunkFileCount = 0;
        if (Directory.Exists(chunksDir))
        {
            // Enumerate up front so progress has a real total; every
            // file counts as delivered once processed (copied now or
            // already present — both mean the destination has it).
            var chunkFiles = Directory.GetFiles(chunksDir, "*", SearchOption.AllDirectories);
            chunkFileCount = chunkFiles.Length;
            int processed = 0;
            foreach (string src in chunkFiles)
            {
                token.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(chunksDir, src);
                string dst = layout is not null
                    ? Path.Combine(targetDir, layout.ChunkFileName(
                        rel.Replace(Path.DirectorySeparatorChar.ToString(), "")
                           .Replace(Path.AltDirectorySeparatorChar.ToString(), "")))
                    : Path.Combine(targetChunks, rel);
                if (CopyWrappedOrPlain(src, dst, layout))
                    chunksCopied++;
                processed++;
                progress?.Invoke(processed, chunkFiles.Length);
            }
        }
        if (Directory.Exists(manifestsDir))
        {
            foreach (string src in Directory.GetFiles(manifestsDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(manifestsDir, src);
                string dst = layout is not null
                    ? Path.Combine(targetDir, layout.ManifestFileName(Path.GetFileName(src)))
                    : Path.Combine(targetManifests, rel);
                if (CopyWrappedOrPlain(src, dst, layout))
                    manifestsCopied++;
            }
        }
        // Copy repo.json so the USB drive is a self-contained recoverable repo.
        string repoJson = Path.Combine(repoRoot, "repo.json");
        if (File.Exists(repoJson))
            CopyWrappedOrPlain(repoJson, layout is not null
                ? Path.Combine(targetDir, layout.RepoFileName())
                : Path.Combine(targetDir, "repo.json"), layout);
        if (layout is not null && layout.DecoysEnabled)
            layout.WriteDecoys(targetDir, chunkFileCount);

        return (chunksCopied, manifestsCopied);
    }

    /// <summary>
    /// Disguised destinations copy header-wrapped (and compare wrapped);
    /// plain destinations copy raw. Single branch point for the USB
    /// folder-sync paths, mirroring SmbSync's.
    /// </summary>
    private static bool CopyWrappedOrPlain(string src, string dst, DisguisedLayout? layout)
    {
        if (layout is not null)
            return DisguisedLayout.CopyWrappedIfNeeded(src, dst, out _);
        return CopyIfDifferent(src, dst);
    }

    /// <summary>
    /// Disguised destinations copy header-wrapped (and compare wrapped,
    /// including content hash); plain destinations copy raw with the
    /// hash-compare path. Single branch point for the batched USB paths.
    /// </summary>
    private static bool CopyFileWrappedOrPlain(string src, string dst, DisguisedLayout? layout)
    {
        if (layout is not null)
            return DisguisedLayout.CopyWrappedIfNeeded(src, dst, out _);
        return CopyFileIfDifferent(src, dst);
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

    private void AutoUploadToUsenet(BackupJobConfig job, string backupId, string passphrase,
        CancellationToken token = default)
    {
        AutoUploadToUsenetWithProviders(job, backupId, passphrase, _nntpProviders, token);
    }

    private void AutoUploadToUsenetWithProviders(
        BackupJobConfig job, string backupId, string passphrase,
        IReadOnlyList<NntpConfig> providers, CancellationToken token = default,
        string? progressDestination = null)
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
        int attempted = 0, succeeded = 0;
        foreach (var nntp in providers)
        {
            if (string.IsNullOrWhiteSpace(nntp.Host))
            {
                // SF-14: a blank-host entry is a config wart, not a
                // provider — it must not count toward the totals below
                // (it used to inflate the denominator so a run where
                // every real provider failed still "succeeded").
                Log($"job '{job.Name}': skipping a configured Usenet provider with a blank host");
                continue;
            }
            attempted++;
            // Env var takes precedence; otherwise decrypt the DPAPI-stored password.
            string? nntpPassword = SecretResolver.ResolvePassword(NntpPasswordEnvVar, nntp.PasswordProtected);
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
                    from: repo.GetOrCreatePoster(), indexAuth: repo.IndexAuth);
                // Determine redundancy mode: per-provider override, else job default
                string redundancy = !string.IsNullOrEmpty(nntp.RedundancyMode)
                    ? nntp.RedundancyMode
                    : job.RedundancyMode;
                // Parsed once for the comparisons below; the string still
                // goes to the uploader, whose parameter stays a string
                // (it is a runtime decision value, not config).
                var redundancyKind = RedundancyKindParser.Parse(redundancy);
                int uploaded = 0, skipped = 0;
                // Parallel upload using the connection pool.
                // Each thread acquires a connection, uploads, releases it.
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = nntp.Connections,
                    // P2-C6: every Parallel.For/ForEach in this provider
                    // leg (volumes, chunks, parity) shares these options,
                    // so this one assignment makes the whole leg stop.
                    CancellationToken = token,
                };
                // Parity blocks are generated per group at upload time (never
                // all held in memory: a parity shard is volume-sized). Count
                // them upfront so the progress tracker knows the full scope:
                // positional groups of 10, trailing groups of < 2 skipped.
                int dataUnits = useVolumes ? volumeGroups.Count : chunkIds.Length;
                int parityGroups = dataUnits / 10 + (dataUnits % 10 >= 2 ? 1 : 0);
                int parityUnits = redundancyKind == RedundancyKind.Xor ? parityGroups
                    : redundancyKind == RedundancyKind.Par2 ? parityGroups * 3 : 0;

                // Progress tracker: logs every 5 min with rolling throughput ETA.
                // Shows "measuring..." until enough data (10 chunks + 30s), then
                // real estimates that refine as more data arrives.
                // Total covers data units (chunks or volumes) + parity + manifest
                // + index so the bar doesn't hit 100% and vanish while work remains.
                // CX-2: volume mode ALSO posts an NFO article (recorded below);
                // it wasn't reserved, so progress could report done = total + 1
                // — over 100%.
                int totalUnits = dataUnits + parityUnits + 2 + (useVolumes ? 1 : 0);
                // Mirror this provider's units into the run progress
                // (job card + estimate). The destination is the
                // location's name when the upload came from one;
                // the legacy global-provider path names it by host.
                string destName = progressDestination ?? nntp.Host;
                RunProgress?.RegisterDestination(destName, "nntp", "items");
                using var progress = new UploadProgressTracker(
                    Log, job.Name, nntp.Host, totalUnits,
                    snap =>
                    {
                        var rp = RunProgress;
                        if (rp is null) return;
                        rp.SetDestinationTotal(destName, snap.TotalChunks);
                        rp.SetDestinationDone(destName, snap.DoneChunks);
                        rp.SetDestinationObservedRate(destName, snap.BytesPerSec);
                    });
                // Scope block: formerly a try/finally whose finally only
                // documented not clearing the removed ActiveUpload tracker.
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
                    // P2-V2a: the groups generated here are collected and
                    // stamped into the manifest below (before the manifest
                    // article posts), keyed by provider.
                    var parityGroupLists = new List<List<string>>();
                    int parityUploaded = 0, paritySkipped = 0;
                    if (parityUnits > 0)
                    {
                        progress.SetPhase("parity");
                        string parityLabel = redundancyKind == RedundancyKind.Xor ? "XOR" : "PAR2";
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
                                parityGroupLists.AddRange(redundancyKind == RedundancyKind.Xor
                                    ? Redundancy.XorParity.GroupsFor(group, job.ParityGroupSize)
                                    : Redundancy.Par2Redundancy.GroupsFor(group));
                                IReadOnlyDictionary<string, byte[]> blocks =
                                    redundancyKind == RedundancyKind.Xor
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
                            parityGroupLists.AddRange(redundancyKind == RedundancyKind.Xor
                                ? Redundancy.XorParity.GroupsFor(chunkIds, job.ParityGroupSize)
                                : Redundancy.Par2Redundancy.GroupsFor(chunkIds));
                            IReadOnlyDictionary<string, byte[]> parityBlocks =
                                redundancyKind == RedundancyKind.Xor
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

                    // P2-V2a: stamp this provider's parity record into
                    // the manifest BEFORE the manifest article posts, so
                    // the posted manifest carries it. (An explicit
                    // "none" record distinguishes no-parity-by-design
                    // from a legacy backup with no record at all.)
                    manifest.ParityByProvider ??=
                        new Dictionary<string, ManifestParityRecord>();
                    manifest.ParityByProvider[
                        Nntp.ChunkMessageIndex.MakeProviderKey(
                            nntp.Host, nntp.Newsgroup)] =
                        new ManifestParityRecord
                        {
                            Mode = redundancy,
                            GroupSize = job.ParityGroupSize,
                            Groups = parityGroupLists,
                        };
                    repo.SaveManifest(manifest);

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
                    succeeded++;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A stop is not a provider failure: propagate so the run
                // ends as cancelled and its outcome is recorded (P2-C6).
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{nntp.Host}: {ex.Message}");
                Log($"job '{job.Name}': auto-upload to {nntp.Host} FAILED: {ex.Message}");
            }
        }
        // SF-14: ANY failed provider must surface. The old rule threw
        // only when every configured entry (including blank hosts that
        // were never attempted) had an error, so a partial failure —
        // one provider missing its copy of the backup — returned
        // normally and the job showed clean success.
        if (attempted == 0)
            throw new InvalidOperationException(
                "Auto-upload enabled but every configured Usenet provider has a blank host.");
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Auto-upload failed on {errors.Count} of {attempted} provider(s) " +
                $"({succeeded} succeeded): {string.Join("; ", errors)}");
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

    /// <summary>
    /// The chunks of a delivered batch that may be pruned now: all of
    /// them EXCEPT any already staged into a later batch (P2-R2/C10).
    /// Internal for tests.
    /// </summary>
    internal static List<string> ComputePruneSet(
        IReadOnlyList<string> flushedChunkIds, IReadOnlyList<string> laterStagedIds)
    {
        if (laterStagedIds.Count == 0)
            return flushedChunkIds.ToList();
        var later = new HashSet<string>(laterStagedIds, StringComparer.Ordinal);
        return flushedChunkIds.Where(id => !later.Contains(id)).ToList();
    }

    /// <summary>
    /// Deletes staged chunks after a batch is confirmed delivered
    /// (NS-12). A prune failure — a locked file, a transient IO error —
    /// must never fail the run: the data is already at its destination,
    /// and DL-1 means a leftover staged file is never mistaken for
    /// undelivered data. Failures are logged per chunk and counted.
    /// Returns the number of chunks that could not be pruned.
    /// </summary>
    internal static int PruneStagedChunks(
        IBlobStore store, IEnumerable<string> chunkIds, Action<string>? log = null)
    {
        int failures = 0;
        foreach (string id in chunkIds)
        {
            try
            {
                store.Delete(id);
            }
            catch (Exception ex)
            {
                failures++;
                log?.Invoke($"could not prune staged chunk {id} ({ex.Message}); it stays on disk.");
            }
        }
        if (failures > 0)
            log?.Invoke($"{failures} staged chunk(s) could not be pruned (locked?); " +
                "the backup itself is delivered.");
        return failures;
    }
}

/// <summary>
/// Source-gen context for the run-state file (P2-R1). Internal, like
/// the type it registers; used by BackupScheduler's load/persist only.
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, BackupScheduler.PersistedJobRun>))]
internal partial class RunStateJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
