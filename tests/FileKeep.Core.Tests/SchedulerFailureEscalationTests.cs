using System.Collections.Concurrent;
using FileKeep.Core;
using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// W-2: a persistent per-cycle scheduler fault used to be one identical
/// log line per poll forever, with no escalation and no back-off. After
/// 3 consecutive failures the loop emits a distinct
/// SCHEDULER-FAULT-ESCALATED event (once per streak) and backs off
/// between retries; a success resets the streak.
/// </summary>
public sealed class SchedulerFailureEscalationTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly string? _savedPassphrase;

    public SchedulerFailureEscalationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "escalation"u8.ToArray());
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

    [Fact]
    public void RecordScanFailure_EscalatesOnceAtThreshold_ThenResets()
    {
        var scheduler = new BackupScheduler(new ServiceConfig());
        Assert.Equal(0, scheduler.RecordScanFailure());
        Assert.Equal(0, scheduler.RecordScanFailure());
        // Third consecutive failure crosses the threshold: escalation.
        Assert.Equal(3, scheduler.RecordScanFailure());
        // Still failing, but the event fired once for this streak.
        Assert.Equal(0, scheduler.RecordScanFailure());
        Assert.Equal(0, scheduler.RecordScanFailure());
        // A success resets the streak: the next fault escalates again.
        scheduler.ResetScanFailures();
        Assert.Equal(0, scheduler.RecordScanFailure());
        Assert.Equal(0, scheduler.RecordScanFailure());
        Assert.Equal(3, scheduler.RecordScanFailure());
    }

    [Fact]
    public void RecordRetentionFailure_EscalatesOnceAtThreshold_ThenResets()
    {
        var scheduler = new BackupScheduler(new ServiceConfig());
        Assert.Equal(0, scheduler.RecordRetentionFailure());
        Assert.Equal(0, scheduler.RecordRetentionFailure());
        Assert.Equal(3, scheduler.RecordRetentionFailure());
        Assert.Equal(0, scheduler.RecordRetentionFailure());
        scheduler.ResetRetentionFailures();
        Assert.Equal(0, scheduler.RecordRetentionFailure());
        Assert.Equal(0, scheduler.RecordRetentionFailure());
        Assert.Equal(3, scheduler.RecordRetentionFailure());
    }

    [Fact]
    public void BackoffDelay_HealthyIsPollInterval_FailingGrowsExponentially_Capped()
    {
        var scheduler = new BackupScheduler(new ServiceConfig(),
            pollInterval: TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.BackoffDelay());
        scheduler.RecordScanFailure();
        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.BackoffDelay());
        scheduler.RecordScanFailure();
        Assert.Equal(TimeSpan.FromSeconds(60), scheduler.BackoffDelay());
        scheduler.RecordScanFailure();
        Assert.Equal(TimeSpan.FromSeconds(120), scheduler.BackoffDelay());
        // Retention failures count toward the same back-off.
        var other = new BackupScheduler(new ServiceConfig(),
            pollInterval: TimeSpan.FromSeconds(30));
        for (int i = 0; i < 10; i++)
            other.RecordRetentionFailure();
        Assert.Equal(BackupScheduler.MaxCycleBackoff, other.BackoffDelay());
    }

    [Fact]
    public async Task RunAsync_PersistentScanFault_EscalatesWithDistinctEvent_AndKeepsLoopAlive()
    {
        // Same trigger as RunAsync_ScanFault_LogsAndKeepsSchedulerAlive
        // (SF-18): the reachability probe throws on every scan. The
        // loop must survive AND, after 3 consecutive failures, emit
        // the distinct escalation event instead of only repeating the
        // per-cycle line.
        var logs = new ConcurrentQueue<string>();
        var clock = new ManualClock { Now = new DateTime(2026, 10, 5, 8, 0, 0) };
        var scheduler = new BackupScheduler(new ServiceConfig
        {
            Locations = new List<BackupLocation>
            {
                new() { Name = "vault", Type = "smb", Share = Path.Combine(_workDir, "vault") },
            },
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Sources = new List<string> { _srcDir },
                        Schedule = "interval 60", Mode = "incremental",
                        Targets = new List<string> { "vault" } },
            },
        }, clock,
        pollInterval: TimeSpan.FromMilliseconds(50), log: logs.Enqueue,
        networkReachable: _ => throw new InvalidOperationException("probe blew up"));
        // NextRunLocal is anchored at construction (09:00 for
        // "interval 60"), so the clock must start before the due time
        // and advance past it — constructing with the clock already at
        // 09:01 anchors the next run at 10:00 and the job is never due,
        // so the probe never throws and nothing ever escalates.
        clock.Now = new DateTime(2026, 10, 5, 9, 1, 0); // job due
        using var cts = new CancellationTokenSource();
        Task run = scheduler.RunAsync(cts.Token);
        try
        {
            for (int i = 0; i < 200 && !logs.Any(m => m.Contains("SCHEDULER-FAULT-ESCALATED")); i++)
                await Task.Delay(50);
            Assert.Contains(logs, m => m.Contains("SCHEDULER-FAULT-ESCALATED"));
            Assert.Contains(logs, m => m.Contains("due-job scan has failed 3 consecutive times"));
            // Exactly one escalation line per streak, even as the
            // per-cycle failures keep coming.
            Assert.Single(logs, m => m.Contains("SCHEDULER-FAULT-ESCALATED"));
            Assert.True(logs.Count(m => m.Contains("due-job scan failed")) >= 3);
        }
        finally
        {
            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Contains(logs, m => m.Contains("scheduler stopped"));
    }

    [Fact]
    public void RunRetentionIfDue_RepeatedFalsePass_EscalatesOnce()
    {
        // RunRetentionChecks returns false (no throw) when a per-repo
        // pass fails; that must count toward escalation too.
        var logs = new List<string>();
        var config = new ServiceConfig
        {
            RetentionEnabled = true,
            RetentionCheckIntervalHours = 24,
            NntpProviders = new List<NntpConfig> { new NntpConfig { Host = "unused.invalid", Port = 119 } },
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = Path.Combine(_workDir, "missing-repo"),
                        Sources = new List<string> { _srcDir }, Schedule = "interval 60",
                        Targets = new List<string>() },
            },
        };
        var t0 = new DateTime(2026, 10, 5, 10, 0, 0);
        var scheduler = new BackupScheduler(config, new ManualClock { Now = t0 },
            log: logs.Add, networkReachable: _ => true);

        scheduler.RunRetentionIfDue(t0);
        scheduler.RunRetentionIfDue(t0.AddMinutes(16));
        scheduler.RunRetentionIfDue(t0.AddMinutes(32));
        Assert.Single(logs, m => m.Contains("SCHEDULER-FAULT-ESCALATED"));
        Assert.Contains(logs, m => m.Contains("retention pass has failed 3 consecutive times"));
    }
}
