using UsenetBackup.Core;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Job pause/resume and location-loss resilience (2026-10-20 design):
/// a job may REMAIN without a backup location but may not be CREATED
/// without one; deleting an in-use location strips it from its jobs
/// instead of refusing; derived pauses (no location / USB drive away)
/// lift on their own; resuming NEVER triggers a catch-up run — missed
/// occurrences are discarded and the job re-arms for its next time.
/// </summary>
public sealed class JobPauseTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly string? _savedPassphrase;

    public JobPauseTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "pause"u8.ToArray());
        BackupRepository.Init(_repoDir, Passphrase, 64 * 1024, 10_000).Dispose();
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

    // ---------- the state evaluator (pure) ----------

    private static BackupJobConfig Job(bool paused = false, params string[] targets) => new()
    {
        Name = "j", Repo = "r", Source = "s", Schedule = "interval 60",
        Paused = paused, Targets = targets.ToList(),
    };

    private static Func<string, JobStateEvaluator.LocationFacts> Lookup(
        params (string Name, bool IsUsb, bool Present)[] locations) => name =>
    {
        var hit = locations.FirstOrDefault(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return hit.Name is null
            ? new JobStateEvaluator.LocationFacts(false, false, false, false)
            : new JobStateEvaluator.LocationFacts(true, hit.IsUsb, hit.Present, false);
    };

    private static Func<string, JobStateEvaluator.LocationFacts> NetLookup(
        params (string Name, bool Away)[] locations) => name =>
    {
        var hit = locations.FirstOrDefault(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return hit.Name is null
            ? new JobStateEvaluator.LocationFacts(false, false, false, false)
            : new JobStateEvaluator.LocationFacts(true, false, false, hit.Away);
    };

    [Fact]
    public void Evaluator_UserPausedWinsOverEverything()
    {
        var state = JobStateEvaluator.Evaluate(
            Job(paused: true, "vault"), Lookup(("vault", false, true)));
        Assert.Equal(JobRunState.UserPaused, state);
    }

    [Fact]
    public void Evaluator_NoTargets_IsNeedsLocation()
    {
        Assert.Equal(JobRunState.NeedsLocation,
            JobStateEvaluator.Evaluate(Job(), Lookup()));
    }

    [Fact]
    public void Evaluator_SmbTarget_IsActive()
    {
        Assert.Equal(JobRunState.Active, JobStateEvaluator.Evaluate(
            Job(false, "vault"), Lookup(("vault", false, true))));
    }

    [Fact]
    public void Evaluator_OnlyAbsentUsb_IsWaitingForDrive()
    {
        Assert.Equal(JobRunState.WaitingForDrive, JobStateEvaluator.Evaluate(
            Job(false, "stick"), Lookup(("stick", true, false))));
    }

    [Fact]
    public void Evaluator_AbsentUsbPlusSmb_IsActive()
    {
        Assert.Equal(JobRunState.Active, JobStateEvaluator.Evaluate(
            Job(false, "stick", "vault"),
            Lookup(("stick", true, false), ("vault", false, true))));
    }

    [Fact]
    public void Evaluator_PresentUsb_IsActive()
    {
        Assert.Equal(JobRunState.Active, JobStateEvaluator.Evaluate(
            Job(false, "stick"), Lookup(("stick", true, true))));
    }

    [Fact]
    public void Evaluator_LegacyBareTarget_DefersToExecutor()
    {
        // A target naming no location (legacy bare "nntp") is resolved by
        // the executor as before; it is never a derived pause.
        Assert.Equal(JobRunState.Active, JobStateEvaluator.Evaluate(
            Job(false, "nntp"), Lookup()));
    }

    [Fact]
    public void Evaluator_NetworkAwayOnly_IsWaitingForNetwork()
    {
        Assert.Equal(JobRunState.WaitingForNetwork, JobStateEvaluator.Evaluate(
            Job(false, "vault"), NetLookup(("vault", true))));
    }

    [Fact]
    public void Evaluator_OneNetworkAwayOtherReachable_IsActive()
    {
        Assert.Equal(JobRunState.Active, JobStateEvaluator.Evaluate(
            Job(false, "vault", "usenet"),
            NetLookup(("vault", true), ("usenet", false))));
    }

    [Fact]
    public void Evaluator_UsbAwayAndNetworkAway_PrefersDrive()
    {
        // Both block; the physical, user-actionable one is reported.
        var facts = new Dictionary<string, JobStateEvaluator.LocationFacts>
        {
            ["stick"] = new(true, true, false, false),
            ["vault"] = new(true, false, false, true),
        };
        Assert.Equal(JobRunState.WaitingForDrive, JobStateEvaluator.Evaluate(
            Job(false, "stick", "vault"), n => facts[n]));
    }

    [Fact]
    public void Evaluator_UsbPresentAndNetworkAway_IsActive()
    {
        var facts = new Dictionary<string, JobStateEvaluator.LocationFacts>
        {
            ["stick"] = new(true, true, true, false),
            ["vault"] = new(true, false, false, true),
        };
        Assert.Equal(JobRunState.Active, JobStateEvaluator.Evaluate(
            Job(false, "stick", "vault"), n => facts[n]));
    }

    // ---------- the scheduler: skip + re-arm, never catch up ----------

    private string WriteConfigJson(string jobFields)
    {
        string json = $$"""
            {
              "dashboardPort": 15789,
              "dashboardBind": "127.0.0.1",
              "jobs": [
                { "name": "docs", "repo": "{{_repoDir.Replace("\\", "\\\\")}}",
                  "source": "{{_srcDir.Replace("\\", "\\\\")}}",
                  "schedule": "interval 60", "mode": "incremental",
                  {{jobFields}} }
              ]
            }
            """;
        string path = Path.Combine(_workDir, "service.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Scheduler_PausedDueJob_SkipsAndRearms_ResumeDoesNotCatchUp()
    {
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = ServiceConfig.Load(WriteConfigJson(
            "\"paused\": true, \"targets\": [\"nntp\"]"));
        var scheduler = new BackupScheduler(config, clock);
        var job = Assert.Single(scheduler.Jobs);
        Assert.Equal(new DateTime(2026, 10, 5, 11, 0, 0), job.NextRunLocal);

        // Due (and then some) while paused: not run, occurrence discarded.
        clock.Now = new DateTime(2026, 10, 5, 12, 30, 0);
        scheduler.RunDueJobs(clock.Now);
        Assert.Null(job.LastResult);
        Assert.Equal(new DateTime(2026, 10, 5, 13, 30, 0), job.NextRunLocal);

        // Resume: the missed 11:00 and 12:00 occurrences do NOT catch up.
        job.Config.Paused = false;
        clock.Now = new DateTime(2026, 10, 5, 12, 45, 0);
        scheduler.RunDueJobs(clock.Now);
        Assert.Null(job.LastResult);

        // The next scheduled occurrence runs normally.
        clock.Now = new DateTime(2026, 10, 5, 13, 30, 0);
        scheduler.RunDueJobs(clock.Now);
        Assert.NotNull(job.LastResult);
        Assert.True(job.LastResult.Success);
    }

    [Fact]
    public void Scheduler_TargetlessDueJob_SkipsAndRearms()
    {
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = ServiceConfig.Load(WriteConfigJson("\"targets\": []"));
        var scheduler = new BackupScheduler(config, clock);
        var job = Assert.Single(scheduler.Jobs);

        clock.Now = new DateTime(2026, 10, 5, 12, 30, 0);
        scheduler.RunDueJobs(clock.Now);
        Assert.Null(job.LastResult);
        Assert.Equal(new DateTime(2026, 10, 5, 13, 30, 0), job.NextRunLocal);
    }

    [Fact]
    public void Scheduler_RunJobNow_PausedJob_Throws()
    {
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = ServiceConfig.Load(WriteConfigJson(
            "\"paused\": true, \"targets\": [\"nntp\"]"));
        var scheduler = new BackupScheduler(config, clock);
        var ex = Assert.Throws<InvalidOperationException>(() => scheduler.RunJobNow("docs"));
        Assert.Contains("paused", ex.Message);
    }

    // ---------- the network gate: try at the due time, pause if away ----------

    private ServiceConfig MakeGateConfig()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        return new ServiceConfig
        {
            DashboardBind = "127.0.0.1",
            DashboardPort = 15789,
            Locations = new List<BackupLocation>
            {
                // A local folder as the "share": SmbShare treats non-UNC
                // paths as local, so a run can really sync to it here.
                new() { Name = "vault", Type = "smb", Share = shareDir },
            },
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "interval 60", Mode = "incremental",
                        Targets = new List<string> { "vault" } },
            },
        };
    }

    [Fact]
    public void Scheduler_NetworkAway_SkipsRecordsAndRearms_ThenRunsWhenBack()
    {
        bool reachable = false;
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = MakeGateConfig();
        var scheduler = new BackupScheduler(config, clock,
            networkReachable: _ => reachable);
        var job = Assert.Single(scheduler.Jobs);
        Assert.Equal(new DateTime(2026, 10, 5, 11, 0, 0), job.NextRunLocal);

        // Due with the network away: no run, no failure — a derived
        // pause is recorded and the occurrence is discarded.
        clock.Now = new DateTime(2026, 10, 5, 11, 0, 0);
        scheduler.RunDueJobs(clock.Now);
        Assert.Null(job.LastResult);
        Assert.True(job.NetworkAway);
        Assert.Equal(new DateTime(2026, 10, 5, 12, 0, 0), job.NextRunLocal);
        Assert.Equal(JobRunState.WaitingForNetwork,
            scheduler.EvaluateJobState(job.Config, job.NetworkAway));

        // Network back by the next occurrence: the job runs (to the
        // local-folder share) and the recorded pause clears.
        reachable = true;
        clock.Now = new DateTime(2026, 10, 5, 12, 0, 0);
        scheduler.RunDueJobs(clock.Now);
        Assert.NotNull(job.LastResult);
        Assert.True(job.LastResult.Success);
        Assert.False(job.NetworkAway);
    }

    [Fact]
    public void Scheduler_RunJobNow_NetworkAway_ThrowsAndRecords()
    {
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = MakeGateConfig();
        var scheduler = new BackupScheduler(config, clock,
            networkReachable: _ => false);
        var job = Assert.Single(scheduler.Jobs);
        var ex = Assert.Throws<InvalidOperationException>(() => scheduler.RunJobNow("docs"));
        Assert.Contains("network", ex.Message);
        Assert.True(job.NetworkAway);
    }

    // ---------- the dashboard operations ----------

    private ServiceConfig MakeConfigWithLocations()
    {
        var config = new ServiceConfig
        {
            DashboardBind = "127.0.0.1",
            DashboardPort = 15789,
            Locations = new List<BackupLocation>
            {
                new() { Name = "vault", Type = "smb", Share = @"\\nas\backups" },
                new() { Name = "spare", Type = "smb", Share = @"\\nas\spare" },
            },
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "interval 60", Mode = "incremental",
                        Targets = new List<string> { "vault" } },
            },
        };
        return config;
    }

    private string SaveConfig(ServiceConfig config)
    {
        string path = Path.Combine(_workDir, "service.json");
        config.Save(path);
        return path;
    }

    [Fact]
    public void DeleteLocation_StripsTargets_JobRemains()
    {
        var config = MakeConfigWithLocations();
        string path = SaveConfig(config);
        var scheduler = new BackupScheduler(config);

        var (status, _) = DashboardApi.DeleteLocation(config, scheduler, path, "vault");
        Assert.Equal(200, status);
        Assert.DoesNotContain(config.Locations, l => l.Name == "vault");
        var job = Assert.Single(config.Jobs);
        Assert.Empty(job.EffectiveTargets);

        // Persisted: a reload sees the location gone and the job targetless.
        var reloaded = ServiceConfig.Load(path);
        Assert.Single(reloaded.Locations);
        Assert.Empty(Assert.Single(reloaded.Jobs).EffectiveTargets);
    }

    [Fact]
    public void UpsertJob_CreateWithoutTargets_Rejected()
    {
        var config = MakeConfigWithLocations();
        string path = SaveConfig(config);
        var scheduler = new BackupScheduler(config);
        var job = new BackupJobConfig
        {
            Name = "new1", Source = _srcDir, Schedule = "interval 60",
            Mode = "incremental", Targets = new List<string>(),
        };
        var (status, _) = DashboardApi.UpsertJob(config, scheduler, path, job);
        Assert.Equal(400, status);
        Assert.DoesNotContain(config.Jobs, j => j.Name == "new1");
    }

    [Fact]
    public void UpsertJob_UpdateToZeroTargets_Allowed()
    {
        var config = MakeConfigWithLocations();
        string path = SaveConfig(config);
        var scheduler = new BackupScheduler(config);
        var updated = new BackupJobConfig
        {
            Name = "docs", Repo = _repoDir, Source = _srcDir,
            Schedule = "interval 60", Mode = "incremental",
            Targets = new List<string>(),
        };
        var (status, _) = DashboardApi.UpsertJob(config, scheduler, path, updated);
        Assert.Equal(200, status);
        Assert.Empty(Assert.Single(config.Jobs).EffectiveTargets);
    }

    [Fact]
    public void SetJobPaused_Persists()
    {
        var config = MakeConfigWithLocations();
        string path = SaveConfig(config);
        var scheduler = new BackupScheduler(config);

        var (status, _) = DashboardApi.SetJobPaused(config, scheduler, path, "docs", true);
        Assert.Equal(200, status);
        Assert.True(Assert.Single(config.Jobs).Paused);
        Assert.True(Assert.Single(ServiceConfig.Load(path).Jobs).Paused);

        var (status2, _) = DashboardApi.SetJobPaused(config, scheduler, path, "docs", false);
        Assert.Equal(200, status2);
        Assert.False(Assert.Single(config.Jobs).Paused);
    }

    [Fact]
    public void AttachLocation_AppendsTarget_Idempotently()
    {
        var config = MakeConfigWithLocations();
        Assert.Single(config.Jobs).Targets = new List<string>();
        string path = SaveConfig(config);
        var scheduler = new BackupScheduler(config);

        var (status, _) = DashboardApi.AttachLocation(config, scheduler, path, "docs", "spare");
        Assert.Equal(200, status);
        Assert.Equal(new[] { "spare" }, Assert.Single(config.Jobs).EffectiveTargets);

        var (status2, _) = DashboardApi.AttachLocation(config, scheduler, path, "docs", "spare");
        Assert.Equal(200, status2);
        Assert.Equal(new[] { "spare" }, Assert.Single(config.Jobs).EffectiveTargets);

        var (status3, _) = DashboardApi.AttachLocation(config, scheduler, path, "docs", "nope");
        Assert.Equal(400, status3);
    }

    [Fact]
    public void Status_ReportsJobState()
    {
        var config = MakeConfigWithLocations();
        Assert.Single(config.Jobs).Paused = true;
        var scheduler = new BackupScheduler(config);
        var status = DashboardApi.GetStatus(scheduler);
        Assert.Equal("UserPaused", Assert.Single(status.Jobs).State);

        Assert.Single(config.Jobs).Paused = false;
        Assert.Single(config.Jobs).Targets = new List<string>();
        scheduler.ReloadJobs(config);
        var status2 = DashboardApi.GetStatus(scheduler);
        Assert.Equal("NeedsLocation", Assert.Single(status2.Jobs).State);
    }
}
