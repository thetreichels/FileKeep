using UsenetBackup.Core;
using UsenetBackup.Core.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Milestone 7 acceptance tests: service configuration, schedule parsing,
/// and the backup scheduler (with a manual clock so no waiting is needed).
/// </summary>
public sealed class ServiceTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly string? _savedPassphrase;

    public ServiceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Passphrase);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class ManualClock : IClock
    {
        public DateTime Now { get; set; }
    }

    private static string WriteConfig(string dir, string json)
    {
        string path = Path.Combine(dir, "service.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string ValidConfigJson(string schedule = "interval 60", string mode = "incremental") => $$"""
        {
          "dashboardPort": 15789,
          "dashboardBind": "127.0.0.1",
          "jobs": [
            { "name": "docs", "repo": "{{_repoDir.Replace("\\", "\\\\")}}",
              "source": "{{_srcDir.Replace("\\", "\\\\")}}",
              "schedule": "{{schedule}}", "mode": "{{mode}}", "vss": false }
          ]
        }
        """;

    // ---------- config ----------

    [Fact]
    public void Config_LoadsValidDocument()
    {
        string path = WriteConfig(_workDir, ValidConfigJson());
        var config = ServiceConfig.Load(path);
        Assert.Equal(15789, config.DashboardPort);
        var job = Assert.Single(config.Jobs);
        Assert.Equal("docs", job.Name);
        Assert.Equal("incremental", job.Mode);
        Assert.False(job.Vss);
    }

    [Fact]
    public void Config_RejectsBadSchedules()
    {
        foreach (string bad in new[] { "", "daily", "daily 25:00", "daily 12:60", "interval 0", "interval -5", "weekly" })
        {
            string path = WriteConfig(_workDir, ValidConfigJson(schedule: bad));
            Assert.Throws<InvalidOperationException>(() => ServiceConfig.Load(path));
        }
    }

    [Fact]
    public void Config_RejectsDuplicateNamesNoJobsAndBadMode()
    {
        string dup = ValidConfigJson().Replace("\"name\": \"docs\"", "\"name\": \"DOCS\"");
        // Two jobs with the same name (case-insensitive).
        string two = dup.Replace("]", ",{\"name\":\"docs\",\"repo\":\"r\",\"source\":\"s\",\"schedule\":\"interval 60\"}]");
        Assert.Throws<InvalidOperationException>(() => ServiceConfig.Load(WriteConfig(_workDir, two)));

        string noJobs = """{ "jobs": [] }""";
        Assert.Throws<InvalidOperationException>(() => ServiceConfig.Load(WriteConfig(_workDir, noJobs)));

        Assert.Throws<InvalidOperationException>(() =>
            ServiceConfig.Load(WriteConfig(_workDir, ValidConfigJson(mode: "sometimes"))));
    }

    [Fact]
    public void Config_RejectsMissingFileAndBadJson()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ServiceConfig.Load(Path.Combine(_workDir, "nope.json")));
        Assert.Throws<InvalidOperationException>(() =>
            ServiceConfig.Load(WriteConfig(_workDir, "{ not json")));
    }

    // ---------- schedule parsing ----------

    [Fact]
    public void Schedule_Daily_NextAfter()
    {
        var s = ScheduleParser.Parse("daily 02:00");
        // Before 02:00 -> today 02:00.
        Assert.Equal(new DateTime(2026, 10, 5, 2, 0, 0),
            s.NextAfter(new DateTime(2026, 10, 5, 1, 0, 0)));
        // After 02:00 -> tomorrow 02:00.
        Assert.Equal(new DateTime(2026, 10, 6, 2, 0, 0),
            s.NextAfter(new DateTime(2026, 10, 5, 3, 0, 0)));
        Assert.Equal("daily 02:00", s.ToString());
    }

    [Fact]
    public void Schedule_Interval_NextAfter()
    {
        var s = ScheduleParser.Parse("interval 90");
        var now = new DateTime(2026, 10, 5, 10, 0, 0);
        Assert.Equal(now.AddMinutes(90), s.NextAfter(now));
    }

    // ---------- scheduler ----------

    private BackupScheduler MakeScheduler(ManualClock clock, string schedule = "interval 60", string mode = "incremental")
    {
        var config = ServiceConfig.Load(WriteConfig(_workDir, ValidConfigJson(schedule, mode)));
        return new BackupScheduler(config, clock, pollInterval: TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void Scheduler_RunsDueJobsAgainstRealRepo()
    {
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "v1"u8.ToArray());
        BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations).Dispose();

        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var scheduler = MakeScheduler(clock);
        var job = Assert.Single(scheduler.Jobs);
        Assert.Equal(new DateTime(2026, 10, 5, 11, 0, 0), job.NextRunLocal);

        // Not due yet: nothing happens.
        scheduler.RunDueJobs(clock.Now);
        Assert.Null(job.LastResult);

        // Due: first run is a full backup (no parent exists yet).
        clock.Now = clock.Now.AddHours(1);
        scheduler.RunDueJobs(clock.Now);
        Assert.NotNull(job.LastResult);
        Assert.True(job.LastResult.Success);
        Assert.Equal("full", GetManifestType(job.LastResult.BackupId!));

        // Next interval: incremental against the first backup.
        File.WriteAllBytes(Path.Combine(_srcDir, "b.txt"), "v2"u8.ToArray());
        clock.Now = clock.Now.AddHours(1);
        scheduler.RunDueJobs(clock.Now);
        Assert.True(job.LastResult!.Success);
        Assert.Equal("inc", GetManifestType(job.LastResult.BackupId!));
        Assert.Equal(0, job.ConsecutiveFailures);
    }

    [Fact]
    public void Scheduler_RecordsFailuresAndKeepsSchedule()
    {
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        // Point the job at a repo that was never initialized.
        string json = ValidConfigJson().Replace(_repoDir, Path.Combine(_workDir, "no-repo"));
        var config = ServiceConfig.Load(WriteConfig(_workDir, json));
        var scheduler = new BackupScheduler(config, clock);

        clock.Now = clock.Now.AddHours(2);
        scheduler.RunDueJobs(clock.Now);
        var job = Assert.Single(scheduler.Jobs);
        Assert.NotNull(job.LastResult);
        Assert.False(job.LastResult.Success);
        Assert.NotNull(job.LastResult.Error);
        Assert.Equal(1, job.ConsecutiveFailures);
        // Still rescheduled for the next interval — one bad run doesn't wedge it.
        Assert.Equal(new DateTime(2026, 10, 5, 13, 0, 0), job.NextRunLocal);
    }

    [Fact]
    public void Scheduler_RunJobNow_ExecutesImmediatelyAndReanchors()
    {
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "v1"u8.ToArray());
        BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations).Dispose();

        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var scheduler = MakeScheduler(clock);
        var job = Assert.Single(scheduler.Jobs);

        var result = scheduler.RunJobNow("docs");
        Assert.True(result.Success);
        Assert.Equal("full", GetManifestType(result.BackupId!));
        // Re-anchored to one interval after *now*, not left in the past.
        Assert.Equal(new DateTime(2026, 10, 5, 11, 0, 0), job.NextRunLocal);

        Assert.Throws<InvalidOperationException>(() => scheduler.RunJobNow("no-such-job"));
    }

    [Fact]
    public void Scheduler_RequiresPassphrase()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        try
        {
            var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
            var scheduler = MakeScheduler(clock);
            var result = scheduler.RunJobNow("docs");
            Assert.False(result.Success);
            Assert.Contains(BackupScheduler.PassphraseEnvVar, result.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Passphrase);
        }
    }

    private string GetManifestType(string backupId)
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        return repo.LoadManifest(backupId).Type;
    }
}
