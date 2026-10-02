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

    private readonly List<JobState> _jobs;
    private readonly IClock _clock;
    private readonly TimeSpan _pollInterval;
    private readonly Action<string>? _log;
    private readonly object _runGate = new(); // runs never overlap

    public IReadOnlyList<JobState> Jobs => _jobs;
    public DateTime StartedLocal { get; }

    public BackupScheduler(ServiceConfig config, IClock? clock = null,
        TimeSpan? pollInterval = null, Action<string>? log = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        _log = log;
        StartedLocal = _clock.Now;
        _jobs = config.Jobs.Select(j =>
        {
            var schedule = ScheduleParser.Parse(j.Schedule);
            return new JobState(j, schedule, schedule.NextAfter(_clock.Now));
        }).ToList();
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
            try { await Task.Delay(_pollInterval, stopping); }
            catch (OperationCanceledException) { break; }
        }
        Log("scheduler stopped");
    }

    /// <summary>
    /// Runs every job whose next-run time has passed. Exposed for tests.
    /// </summary>
    public void RunDueJobs(DateTime now)
    {
        foreach (var job in _jobs.Where(j => j.NextRunLocal <= now).OrderBy(j => j.NextRunLocal))
            RunOne(job, now);
    }

    /// <summary>
    /// Runs a single job immediately, outside its schedule (dashboard "run now").
    /// </summary>
    public JobRunResult RunJobNow(string name)
    {
        var job = _jobs.FirstOrDefault(j => j.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown job '{name}'.");
        JobRunResult result;
        lock (_runGate)
            result = Execute(job.Config, _clock.Now);
        job.LastResult = result;
        job.ConsecutiveFailures = result.Success ? 0 : job.ConsecutiveFailures + 1;
        // Re-anchor the schedule so "run now" doesn't cause an immediate re-run.
        job.NextRunLocal = job.Schedule.NextAfter(_clock.Now);
        return result;
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
            using ISnapshotProvider? snap = job.Vss ? new VssSnapshotProvider(job.Source) : null;

            BackupManifest manifest;
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
                    : repo.BackupIncremental(job.Source, parent, snap);
            }

            OperationLog.Append(job.Repo, "scheduled-backup",
                $"job={job.Name} id={manifest.BackupId} type={manifest.Type} files={manifest.Files.Count}");
            Log($"job '{job.Name}': {manifest.Type} backup {manifest.BackupId} ({manifest.Files.Count} files)");
            return new JobRunResult
            {
                JobName = job.Name,
                StartedLocal = now,
                Success = true,
                BackupId = manifest.BackupId,
                Files = manifest.Files.Count,
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

    private void Log(string message) => _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
}
