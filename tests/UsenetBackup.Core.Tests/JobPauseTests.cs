using System.Collections.Concurrent;
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
    public void Evaluator_NoSecurity_IsHeld_EvenWithGoodTargets()
    {
        // Joel, 2026-10-22 (item 3): a job whose passphrase was never
        // set up is HELD — a visible derived state — instead of
        // failing every scheduled attempt.
        var state = JobStateEvaluator.Evaluate(
            Job(false, "vault"), Lookup(("vault", false, true)),
            securityReady: false);
        Assert.Equal(JobRunState.HeldNoSecurity, state);
    }

    [Fact]
    public void Evaluator_UserPausedWinsOverHeld()
    {
        var state = JobStateEvaluator.Evaluate(
            Job(paused: true, "vault"), Lookup(("vault", false, true)),
            securityReady: false);
        Assert.Equal(JobRunState.UserPaused, state);
    }

    [Fact]
    public void Evaluator_HeldBeatsNeedsLocation()
    {
        // Security is the precondition for any run, so it is the
        // headline even when the job also lacks a location.
        var state = JobStateEvaluator.Evaluate(
            Job(), Lookup(), securityReady: false);
        Assert.Equal(JobRunState.HeldNoSecurity, state);
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
    public void Evaluator_UnresolvableTargetOnly_IsNeedsLocation()
    {
        // Phase 6: a target naming no existing location — the legacy
        // bare token "nntp" is the archetype — resolves to nothing. A
        // job whose targets all fail to resolve behaves like the
        // zero-target case: derived pause (NeedsLocation), never a
        // silent upload somewhere unintended.
        Assert.Equal(JobRunState.NeedsLocation, JobStateEvaluator.Evaluate(
            Job(false, "nntp"), Lookup()));

        // Mixed: one dangling target beside a real, present location
        // still runs on the location (the dangling one is dropped by
        // the gate/executor and named in the run's degraded note).
        Assert.Equal(JobRunState.Active, JobStateEvaluator.Evaluate(
            Job(false, "nntp", "vault"), Lookup(("vault", false, true))));
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
        // The job targets a local-folder share so its resumed run
        // really delivers. (It used to target a bare "nntp" with no
        // providers configured and "succeed" vacuously — DL-3 removed
        // that path; a provider-less run now fails, as it should.)
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var config = new ServiceConfig
        {
            DashboardBind = "127.0.0.1",
            DashboardPort = 15789,
            Locations = new List<BackupLocation>
            {
                new() { Name = "vault", Type = "smb", Share = shareDir },
            },
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "interval 60", Mode = "incremental",
                        Paused = true,
                        Targets = new List<string> { "vault" } },
            },
        };
        var scheduler = new BackupScheduler(config, clock);
        var job = Assert.Single(scheduler.Jobs);
        Assert.Equal(new DateTime(2026, 10, 5, 11, 0, 0), job.NextRunLocal);

        // Due (and then some) while paused: not run, occurrence discarded.
        clock.Now = new DateTime(2026, 10, 5, 12, 30, 0);
        scheduler.RunDueJobs(clock.Now);
        // MU-16: the skip itself is the recorded outcome — the previous
        // behaviour (no record at all) left the dashboard showing the
        // last success as if current. A skip is not a failure.
        Assert.NotNull(job.LastResult);
        Assert.True(job.LastResult.Skipped);
        Assert.False(job.LastResult.Success);
        Assert.Equal(0, job.ConsecutiveFailures);
        Assert.Equal(new DateTime(2026, 10, 5, 13, 30, 0), job.NextRunLocal);

        // Resume: the missed 11:00 and 12:00 occurrences do NOT catch up.
        job.Config.Paused = false;
        clock.Now = new DateTime(2026, 10, 5, 12, 45, 0);
        scheduler.RunDueJobs(clock.Now);
        Assert.True(job.LastResult.Skipped); // the skip record stands until a real run

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
        Assert.NotNull(job.LastResult);
        Assert.True(job.LastResult.Skipped);
        Assert.Equal(new DateTime(2026, 10, 5, 13, 30, 0), job.NextRunLocal);
    }

    [Fact]
    public void Scheduler_NoSecurityDueJob_HeldNotFailed_LiftsWhenSecuritySet()
    {
        // Joel, 2026-10-22 (item 3): with no passphrase stored and no
        // env passphrase, a due job used to throw "Cannot run scheduled
        // backup without a passphrase…" and pile up FAILURES. It is
        // now held: the attempt records as a skip, the state shows as
        // HeldNoSecurity, and the job runs on its own once a usable
        // passphrase exists. (The class ctor sets the env passphrase;
        // this test clears it for its own duration.)
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        try
        {
            var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
            string shareDir = Path.Combine(_workDir, "share-held");
            Directory.CreateDirectory(shareDir);
            var config = new ServiceConfig
            {
                DashboardBind = "127.0.0.1",
                DashboardPort = 15789,
                Locations = new List<BackupLocation>
                {
                    new() { Name = "vault", Type = "smb", Share = shareDir },
                },
                Jobs = new List<BackupJobConfig>
                {
                    new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                            Schedule = "interval 60", Mode = "incremental",
                            Targets = new List<string> { "vault" } },
                },
            };
            var scheduler = new BackupScheduler(config, clock);
            var job = Assert.Single(scheduler.Jobs);
            Assert.Equal(JobRunState.HeldNoSecurity,
                scheduler.EvaluateJobState(job.Config));

            clock.Now = new DateTime(2026, 10, 5, 12, 30, 0);
            scheduler.RunDueJobs(clock.Now);
            Assert.NotNull(job.LastResult);
            Assert.True(job.LastResult.Skipped);
            Assert.False(job.LastResult.Success);
            Assert.Contains("set up security", job.LastResult.Error);
            Assert.Equal(0, job.ConsecutiveFailures);
            Assert.Equal(new DateTime(2026, 10, 5, 13, 30, 0), job.NextRunLocal);

            // Run-now also refuses, with the held reason (not a failure).
            var ex = Assert.Throws<InvalidOperationException>(
                () => scheduler.RunJobNow("docs"));
            Assert.Contains("set up security", ex.Message);

            // Security appears: the very next due occurrence runs.
            Environment.SetEnvironmentVariable(
                BackupScheduler.PassphraseEnvVar, Passphrase);
            Assert.Equal(JobRunState.Active, scheduler.EvaluateJobState(job.Config));
            clock.Now = new DateTime(2026, 10, 5, 13, 30, 0);
            scheduler.RunDueJobs(clock.Now);
            Assert.NotNull(job.LastResult);
            Assert.True(job.LastResult.Success);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                BackupScheduler.PassphraseEnvVar, Passphrase);
        }
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
        Assert.NotNull(job.LastResult);
        Assert.True(job.LastResult.Skipped);
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

    // ---------- degraded: one of several locations misses a backup ----------

    private ServiceConfig MakeMultiTargetConfig(params BackupLocation[] locations)
    {
        return new ServiceConfig
        {
            DashboardBind = "127.0.0.1",
            DashboardPort = 15789,
            Locations = locations.ToList(),
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "interval 60", Mode = "incremental",
                        Targets = locations.Select(l => l.Name).ToList() },
            },
        };
    }

    private BackupLocation LocalShare(string name)
    {
        string dir = Path.Combine(_workDir, name);
        Directory.CreateDirectory(dir);
        return new BackupLocation { Name = name, Type = "smb", Share = dir };
    }

    private BackupLocation BrokenShare(string name)
    {
        // A FILE where the share folder should be: the gate passes it
        // (non-UNC = local), the sync fails trying to create it.
        string file = Path.Combine(_workDir, name);
        File.WriteAllBytes(file, "not a folder"u8.ToArray());
        return new BackupLocation { Name = name, Type = "smb", Share = file };
    }

    [Fact]
    public void Run_PartialUpload_SucceedsWithDegradedNote()
    {
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = MakeMultiTargetConfig(LocalShare("vault"), BrokenShare("broken"));
        var scheduler = new BackupScheduler(config, clock, networkReachable: _ => true);

        var result = scheduler.RunJobNow("docs");
        Assert.True(result.Success);
        Assert.NotNull(result.DegradedNote);
        Assert.Contains("broken", result.DegradedNote);
        Assert.Contains("didn't receive", result.DegradedNote);

        // And it reaches the dashboard status feed.
        var status = DashboardApi.GetStatus(scheduler);
        Assert.Equal(result.DegradedNote,
            Assert.Single(status.Jobs).LastResult!.DegradedNote);
    }

    [Fact]
    public void Run_SmbOnlyBatched_ManifestVolumesCoverEveryChunk()
    {
        // MU-18: volume entries used to come only from NNTP uploaders,
        // so an SMB-only batched job produced a manifest whose Volumes
        // list was empty even though every chunk was packed, delivered,
        // and pruned. The packing is deterministic — the manifest must
        // record it in full.
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = MakeMultiTargetConfig(LocalShare("vault"));
        var scheduler = new BackupScheduler(config, clock, networkReachable: _ => true);

        var result = scheduler.RunJobNow("docs");
        Assert.True(result.Success, result.Error);

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.LoadManifest(result.BackupId!);
        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Volumes);
        Assert.NotEmpty(manifest.Volumes!);
        var packedChunks = manifest.Volumes.SelectMany(v => v.ChunkIds).ToHashSet();
        var fileChunks = manifest.Files.SelectMany(f => f.Chunks)
            .Where(c => !string.IsNullOrEmpty(c)).ToHashSet();
        Assert.NotEmpty(fileChunks);
        Assert.Equal(fileChunks, packedChunks);
    }

    [Fact]
    public void RunState_SurvivesSchedulerRestart()
    {
        // MU-16: run history used to be memory-only — after a service
        // restart every job showed "never run" regardless of its real
        // history. With a state file, a new scheduler over the same
        // config restores the last outcome.
        string statePath = Path.Combine(_workDir, "job-state.json");
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = MakeMultiTargetConfig(LocalShare("vault"), BrokenShare("broken"));

        var first = new BackupScheduler(config, clock, networkReachable: _ => true,
            runStateFilePath: statePath);
        var result = first.RunJobNow("docs");
        Assert.True(result.Success); // degraded: one location delivered
        Assert.True(File.Exists(statePath));

        var second = new BackupScheduler(config, clock, networkReachable: _ => true,
            runStateFilePath: statePath);
        var restored = Assert.Single(second.Jobs).LastResult;
        Assert.NotNull(restored);
        Assert.True(restored!.Success);
        Assert.Equal(result.BackupId, restored.BackupId);
        Assert.NotNull(restored.DegradedNote);
        Assert.False(restored.Skipped);

        // The dashboard status carries the restored history too.
        var status = DashboardApi.GetStatus(second);
        Assert.Equal(result.BackupId, Assert.Single(status.Jobs).LastResult!.BackupId);
    }

    [Fact]
    public void Run_AutoVerify_SeesVerifyFindings_FailsRun()
    {
        // P2-D6: scheduled auto-verify called repo.Verify and DISCARDED
        // the returned error list, logging "auto-verify OK" no matter
        // what. Seed a chunk-mode parent, corrupt one of its chunk
        // blobs in place (blob present, catalog knows it, content bad):
        // the incremental run dedups against it, and auto-verify must
        // fail the run.
        string chunkPath;
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            var parent = repo.BackupDirectory(_srcDir, snapshotProvider: null);
            string chunkId = parent.Files[0].Chunks[0];
            chunkPath = Path.Combine(_repoDir, "chunks", chunkId[..2], chunkId[2..]);
        }
        byte[] blob = File.ReadAllBytes(chunkPath);
        blob[20] ^= 0xFF;
        File.WriteAllBytes(chunkPath, blob);

        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = MakeMultiTargetConfig(LocalShare("vault"));
        config.Jobs[0].AutoVerify = true;
        var scheduler = new BackupScheduler(config, clock, networkReachable: _ => true);

        var result = scheduler.RunJobNow("docs");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("Auto-verify", result.Error);
    }

    [Fact]
    public void Run_LegacyPath_TotalUploadFailure_IsAFailedRun()
    {
        // MU-19: a legacy (non-batched) run whose only target fails
        // used to report Success, with the failure parked in the
        // AutoUploadFailed side flag the status surface never showed.
        // Seed a chunk-mode parent backup directly: its chunks live
        // outside volumes, which is exactly what routes the next
        // scheduled run down the legacy path.
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            repo.BackupDirectory(_srcDir, snapshotProvider: null);

        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = MakeMultiTargetConfig(BrokenShare("broken"));
        var scheduler = new BackupScheduler(config, clock, networkReachable: _ => true);

        var result = scheduler.RunJobNow("docs");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("broken", result.Error);

        // The status feed reports the failure too (the pill keys on it).
        var status = DashboardApi.GetStatus(scheduler);
        var lr = Assert.Single(status.Jobs).LastResult!;
        Assert.False(lr.Success);
    }

    [Fact]
    public void Run_GateSkippedTarget_SucceedsWithDegradedNote()
    {
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var usb = new BackupLocation
        {
            Name = "stick", Type = "usb",
            VolumeSerial = "DEADBEEF", UsbFolder = "FileKeep",
        };
        var config = MakeMultiTargetConfig(LocalShare("vault"), usb);
        var scheduler = new BackupScheduler(config, clock);

        var result = scheduler.RunJobNow("docs");
        Assert.True(result.Success);
        Assert.NotNull(result.DegradedNote);
        Assert.Contains("stick", result.DegradedNote);
        Assert.Contains("USB drive not connected", result.DegradedNote);
    }

    [Fact]
    public void Run_AllTargetsFail_NoDegradedNote()
    {
        // Nothing was delivered anywhere: the run fails outright (the
        // batched path's long-standing behavior when no destination can
        // take the backup) — a failure, not a partial yellow state.
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = MakeMultiTargetConfig(BrokenShare("broken1"), BrokenShare("broken2"));
        var scheduler = new BackupScheduler(config, clock, networkReachable: _ => true);

        var result = scheduler.RunJobNow("docs");
        Assert.False(result.Success);
        Assert.Null(result.DegradedNote);
    }

    [Fact]
    public void Run_UnresolvableTargetOnly_DerivedPausedNeedsLocation()
    {
        // Was Run_BareNntpTargetWithoutProviders_FailsAndKeepsChunks
        // (DL-3, error-handling audit 2026-10-21): a legacy bare "nntp"
        // target with no providers used to run, resolve to no deliverer,
        // and prune its staged chunks into the void. Since Phase 6
        // dropped the bare-target fallback, such a job never runs at
        // all: its targets resolve to no location, so it is
        // derived-paused (NeedsLocation) exactly like a zero-target job —
        // no crash, no silent upload, and nothing staged or pruned.
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        var config = new ServiceConfig
        {
            DashboardBind = "127.0.0.1",
            DashboardPort = 15789,
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "interval 60", Mode = "incremental",
                        Targets = new List<string> { "nntp" } },
            },
        };
        var scheduler = new BackupScheduler(config, clock);

        Assert.Equal(JobRunState.NeedsLocation, scheduler.EvaluateJobState(config.Jobs[0]));
        int ChunkFiles() => Directory.Exists(Path.Combine(_repoDir, "chunks"))
            ? Directory.GetFiles(Path.Combine(_repoDir, "chunks"), "*", SearchOption.AllDirectories).Length
            : 0;
        int before = ChunkFiles();
        var ex = Assert.Throws<InvalidOperationException>(() => scheduler.RunJobNow("docs"));
        Assert.Contains("no backup location", ex.Message);
        Assert.Equal(before, ChunkFiles());
    }

    [Fact]
    public void Run_ManifestStageFails_SingleTarget_FailsTheRun()
    {
        // DL-5 (error-handling audit 2026-10-21): chunks reach the
        // share per batch, but the final metadata sync cannot run (a
        // file sits where its manifests folder must be created). The
        // destination ends up holding chunks it cannot discover — the
        // run must report failure, not the plain success it used to.
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        string shareDir = Path.Combine(_workDir, "vault");
        Directory.CreateDirectory(shareDir);
        File.WriteAllBytes(Path.Combine(shareDir, "manifests"), "not a folder"u8.ToArray());
        var config = MakeMultiTargetConfig(
            new BackupLocation { Name = "vault", Type = "smb", Share = shareDir });
        var scheduler = new BackupScheduler(config, clock, networkReachable: _ => true);

        var result = scheduler.RunJobNow("docs");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("vault", result.Error);
        // The chunks really did arrive — moving the data was not the
        // problem; the undiscoverable backup was.
        Assert.NotEmpty(Directory.GetFiles(
            Path.Combine(shareDir, "chunks"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Run_ManifestStageFails_OneOfTwoTargets_SucceedsDegraded()
    {
        // DL-5: with a second location fully receiving the backup, the
        // same manifest-stage failure is a degraded success naming the
        // location that missed out — not a failed run.
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        string badDir = Path.Combine(_workDir, "brokenmeta");
        Directory.CreateDirectory(badDir);
        File.WriteAllBytes(Path.Combine(badDir, "manifests"), "not a folder"u8.ToArray());
        var config = MakeMultiTargetConfig(
            LocalShare("vault"),
            new BackupLocation { Name = "brokenmeta", Type = "smb", Share = badDir });
        var scheduler = new BackupScheduler(config, clock, networkReachable: _ => true);

        var result = scheduler.RunJobNow("docs");
        Assert.True(result.Success);
        Assert.NotNull(result.DegradedNote);
        Assert.Contains("brokenmeta", result.DegradedNote);
    }

    [Fact]
    public void Run_SameLengthCorruptChunkOnShare_IsRecopiedAndVerifies()
    {
        // DL-4 (error-handling audit 2026-10-21): the share already
        // holds a file at the chunk's path with the right LENGTH but
        // garbage bytes. Size-only confirmation would count it as
        // delivered, prune the only good copy, and leave a corrupt
        // backup behind a green success. The run must recopy it — and
        // the share-side repo must verify clean afterwards.
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        string shareDir = Path.Combine(_workDir, "vault");
        Directory.CreateDirectory(shareDir);
        // The fixture source is a.txt = "pause" (5 bytes) → one chunk
        // whose ID is the SHA-256 of those bytes; its blob is
        // nonce(12) + ciphertext(5) + tag(16) = 33 bytes.
        string chunkId = Hashing.Sha256Hex("pause"u8);
        string destChunk = Path.Combine(shareDir, "chunks", chunkId[..2], chunkId[2..]);
        Directory.CreateDirectory(Path.GetDirectoryName(destChunk)!);
        File.WriteAllBytes(destChunk, new byte[33]);
        var config = MakeMultiTargetConfig(
            new BackupLocation { Name = "vault", Type = "smb", Share = shareDir });
        var scheduler = new BackupScheduler(config, clock, networkReachable: _ => true);

        var result = scheduler.RunJobNow("docs");
        Assert.True(result.Success);

        // The destination is a usable repo layout (chunks + manifests +
        // repo.json): open it and verify the backup end to end.
        using var shareRepo = BackupRepository.Open(shareDir, Passphrase);
        Assert.Empty(shareRepo.Verify(result.BackupId!));
    }

    [Fact]
    public void Run_RetargetedJob_RestagesChunksTheCatalogOnlyKnows()
    {
        // DL-1 (error-handling audit 2026-10-21): run 1 delivers to
        // share "one" and prunes locally; the catalog still "knows"
        // every chunk. Run 2 targets share "two" instead, over a file
        // whose first chunk is unchanged. Staging used to skip that
        // chunk on catalog knowledge alone, so share two never received
        // it — while the run reported success. Now the chunk is only
        // skippable with delivery evidence for THIS run's targets, so
        // it is re-staged, delivered, and share two verifies clean.
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        string bigFile = Path.Combine(_srcDir, "big.bin");
        byte[] head = new byte[64 * 1024];
        Array.Fill(head, (byte)'a');
        byte[] v1 = head.Concat(new byte[100]).ToArray();
        Array.Fill(v1, (byte)'b', 64 * 1024, 100);
        File.WriteAllBytes(bigFile, v1);

        var config1 = MakeMultiTargetConfig(LocalShare("one"));
        var scheduler1 = new BackupScheduler(config1, clock, networkReachable: _ => true);
        var run1 = scheduler1.RunJobNow("docs");
        Assert.True(run1.Success);
        // Everything delivered and pruned: the local repo holds no
        // chunk blobs at all — only the catalog "knows" them.
        string localChunks = Path.Combine(_repoDir, "chunks");
        Assert.Empty(Directory.GetFiles(localChunks, "*", SearchOption.AllDirectories));

        // Change only the tail: the first 64 KiB chunk is identical
        // (same chunk ID), the tail chunk is new.
        byte[] v2 = head.Concat(new byte[100]).ToArray();
        Array.Fill(v2, (byte)'c', 64 * 1024, 100);
        File.WriteAllBytes(bigFile, v2);

        // Full mode for run 2: the assertion is about THIS backup's
        // chunks being complete on share two (an incremental's parent
        // chain legitimately still lives on share one).
        var config2 = MakeMultiTargetConfig(LocalShare("two"));
        config2.Jobs[0].Mode = "full";
        var scheduler2 = new BackupScheduler(config2, clock, networkReachable: _ => true);
        var run2 = scheduler2.RunJobNow("docs");
        Assert.True(run2.Success);

        string shareTwo = Path.Combine(_workDir, "two");
        using var shareRepo = BackupRepository.Open(shareTwo, Passphrase);
        Assert.Empty(shareRepo.Verify(run2.BackupId!));
    }

    [Fact]
    public async Task RunAsync_ScanFault_LogsAndKeepsSchedulerAlive()
    {
        // SF-18: one faulting due-job scan must not kill the scheduler
        // loop. Here the reachability probe throws (standing in for the
        // audit's trigger: the dashboard mutating the config's
        // collections while the scan enumerates them).
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 8, 0, 0) };
        var logs = new ConcurrentQueue<string>();
        var config = MakeMultiTargetConfig(LocalShare("one"));
        var scheduler = new BackupScheduler(config, clock,
            pollInterval: TimeSpan.FromMilliseconds(50),
            log: logs.Enqueue,
            networkReachable: _ => throw new InvalidOperationException("probe blew up"));
        clock.Now = new DateTime(2026, 10, 5, 9, 1, 0); // job due (interval 60)
        using var cts = new CancellationTokenSource();
        Task run = scheduler.RunAsync(cts.Token);
        for (int i = 0; i < 100 && !logs.Any(m => m.Contains("due-job scan failed")); i++)
            await Task.Delay(50);
        Assert.Contains(logs, m => m.Contains("due-job scan failed"));
        cts.Cancel();
        // The loop ends cleanly on cancellation instead of faulting.
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(logs, m => m.Contains("scheduler stopped"));
    }

    [Fact]
    public void RetentionIfDue_FailedPass_RetriesAfterShortDelay_NotFullInterval()
    {
        // SF-12: the check timestamp used to be stamped BEFORE the pass
        // ran, so one transient failure suppressed retention checks for
        // the whole 24h interval. The stamp now advances only on
        // success; a failure retries after ~15 minutes.
        var logs = new List<string>();
        var config = MakeMultiTargetConfig(LocalShare("one"));
        config.RetentionEnabled = true;
        config.RetentionCheckIntervalHours = 24;
        config.Nntp = new NntpConfig { Host = "unused.invalid", Port = 119 };
        config.Jobs[0].Repo = Path.Combine(_workDir, "missing-repo");
        var t0 = new DateTime(2026, 10, 5, 10, 0, 0);
        var scheduler = new BackupScheduler(config, new ManualClock { Now = t0 },
            log: logs.Add, networkReachable: _ => true);

        scheduler.RunRetentionIfDue(t0);
        Assert.Equal(1, logs.Count(m => m.Contains("retention check failed for repo")));

        scheduler.RunRetentionIfDue(t0.AddMinutes(14));
        Assert.Equal(1, logs.Count(m => m.Contains("retention check failed for repo")));

        scheduler.RunRetentionIfDue(t0.AddMinutes(16));
        Assert.Equal(2, logs.Count(m => m.Contains("retention check failed for repo")));
    }

    [Fact]
    public void RetentionIfDue_NothingToCheck_StampsTheInterval()
    {
        // A no-providers pass is a no-op SUCCESS: it stamps, so the
        // "skipped" note appears once per interval, not every cycle —
        // and the pass runs again once the interval has elapsed.
        var logs = new List<string>();
        var config = MakeMultiTargetConfig(LocalShare("one"));
        config.RetentionEnabled = true;
        config.RetentionCheckIntervalHours = 24;
        var t0 = new DateTime(2026, 10, 5, 10, 0, 0);
        var scheduler = new BackupScheduler(config, new ManualClock { Now = t0 },
            log: logs.Add, networkReachable: _ => true);

        scheduler.RunRetentionIfDue(t0);
        scheduler.RunRetentionIfDue(t0.AddHours(1));
        Assert.Equal(1, logs.Count(m => m.Contains("retention check skipped")));

        scheduler.RunRetentionIfDue(t0.AddHours(25));
        Assert.Equal(2, logs.Count(m => m.Contains("retention check skipped")));
    }

    [Fact]
    public void UploadBackup_PartialProviderFailure_ThrowsNamingTheFailure()
    {
        // SF-14: providers [blank host, password-missing]. The old rule
        // threw only when errors == ALL configured entries, blank hosts
        // included — 1 error vs 2 entries meant the upload "succeeded"
        // while the only real provider never received the backup.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        var config = new ServiceConfig
        {
            NntpProviders = new List<NntpConfig>
            {
                new() { Host = "" },
                new() { Host = "news.invalid", Port = 119, Username = "user" },
            },
        };
        var scheduler = new BackupScheduler(config,
            new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) },
            networkReachable: _ => true);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            scheduler.UploadBackup(_repoDir, manifest.BackupId));
        Assert.Contains("1 of 1 provider(s)", ex.Message);
        Assert.Contains("password not configured", ex.Message);
    }

    [Fact]
    public void UploadBackup_AllProvidersBlankHost_Throws()
    {
        // SF-14's limiting case: every provider blank used to
        // "succeed" while uploading nothing at all.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);
        var config = new ServiceConfig
        {
            NntpProviders = new List<NntpConfig> { new() { Host = "" } },
        };
        var scheduler = new BackupScheduler(config,
            new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) },
            networkReachable: _ => true);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            scheduler.UploadBackup(_repoDir, manifest.BackupId));
        Assert.Contains("blank host", ex.Message);
    }

    [Fact]
    public void Run_ProviderlessNntpAlongsideShare_SucceedsDegraded_NamesNntp()
    {
        // DL-3 in its Phase 6 form: the share really receives the backup
        // (the prune is earned), but the "nntp" target — which names no
        // location, the bare-target fallback having been dropped — must
        // appear as a miss in the degraded note instead of counting as
        // delivered.
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 10, 0, 0) };
        string shareDir = Path.Combine(_workDir, "vault");
        Directory.CreateDirectory(shareDir);
        var config = new ServiceConfig
        {
            DashboardBind = "127.0.0.1",
            DashboardPort = 15789,
            Locations = new List<BackupLocation>
            {
                new() { Name = "vault", Type = "smb", Share = shareDir },
            },
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "interval 60", Mode = "incremental",
                        Targets = new List<string> { "vault", "nntp" } },
            },
        };
        var scheduler = new BackupScheduler(config, clock, networkReachable: _ => true);

        var result = scheduler.RunJobNow("docs");
        Assert.True(result.Success);
        Assert.NotNull(result.DegradedNote);
        Assert.Contains("nntp", result.DegradedNote);
        Assert.Contains("didn't receive", result.DegradedNote);
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

public sealed class ScheduleAnchorTests
{
    [Fact]
    public void AnchorAfter_FallBackOccurrenceAlreadyRan_SkipsToNextDay()
    {
        // P2-D9: a daily 01:30 job ran at 01:30 (first pass through the
        // repeated hour). A restart at 01:10 wall (second pass) must
        // not select today's 01:30 again.
        var daily = new Schedule.Daily(1, 30);
        var restart = new DateTime(2026, 11, 1, 1, 10, 0);
        var lastRun = new DateTime(2026, 11, 1, 1, 30, 0);
        Assert.Equal(new DateTime(2026, 11, 2, 1, 30, 0),
            daily.AnchorAfter(restart, lastRun));
        // No prior run: the occurrence stands.
        Assert.Equal(new DateTime(2026, 11, 1, 1, 30, 0),
            daily.AnchorAfter(restart, null));
        // A run EARLIER today doesn't consume a later occurrence.
        Assert.Equal(new DateTime(2026, 11, 1, 1, 30, 0),
            daily.AnchorAfter(restart, new DateTime(2026, 11, 1, 0, 15, 0)));
        // Interval schedules are exempt (no date identity).
        var interval = new Schedule.Interval(30);
        Assert.Equal(restart.AddMinutes(30), interval.AnchorAfter(restart, lastRun));
    }
}
