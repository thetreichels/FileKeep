using System.Text.Json;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Job-card run progress: the worst-case TOTAL estimate model
/// ("No more than XX minutes total"), the tracker's snapshot
/// aggregation, production-context serialization of the dashboard
/// DTO, and the scheduler's publish/clear + cancel surface.
/// </summary>
public sealed class RunProgressTests
{
    private const long MiB = 1024 * 1024;

    private static WorstCaseEstimate.DestinationInput Dest(
        string kind, string unit, long done, long total,
        long bytes = 0, double? observed = null, bool failed = false)
        => new(kind, unit, done, total, bytes, observed, failed);

    // ---------- WorstCaseEstimate.RemainingWorstCaseSeconds ----------

    [Fact]
    public void Remaining_NullWhenNoTotalsKnown()
    {
        var dests = new[] { Dest("usb", "chunks", 5, -1) };
        Assert.Null(WorstCaseEstimate.RemainingWorstCaseSeconds(dests, false, 0));
    }

    [Fact]
    public void Remaining_NullWhenEveryDestinationFailed()
    {
        var dests = new[] { Dest("nntp", "items", 0, 100, failed: true) };
        Assert.Null(WorstCaseEstimate.RemainingWorstCaseSeconds(dests, false, 0));
    }

    [Fact]
    public void Remaining_NntpFloorDoubledForRetries()
    {
        // 100 items at the 1 MiB default, floor 1 MiB/s: 100 s of
        // transfer, doubled for the per-batch retry budget.
        var dests = new[] { Dest("nntp", "items", 0, 100) };
        double? r = WorstCaseEstimate.RemainingWorstCaseSeconds(dests, false, 0);
        Assert.NotNull(r);
        Assert.Equal(200.0, r!.Value, 2);
    }

    [Fact]
    public void Remaining_ObservedSlowerThanFloorWins()
    {
        var dests = new[] { Dest("nntp", "items", 0, 100, observed: 0.5 * MiB) };
        double? r = WorstCaseEstimate.RemainingWorstCaseSeconds(dests, false, 0);
        Assert.Equal(400.0, r!.Value, 2);
    }

    [Fact]
    public void Remaining_ObservedFasterThanFloorIgnored()
    {
        var dests = new[] { Dest("nntp", "items", 0, 100, observed: 40.0 * MiB) };
        double? r = WorstCaseEstimate.RemainingWorstCaseSeconds(dests, false, 0);
        Assert.Equal(200.0, r!.Value, 2);
    }

    [Fact]
    public void Remaining_SlowestDestinationBoundsTheRun()
    {
        var dests = new[]
        {
            Dest("usb", "chunks", 0, 10),    // ~0.3 s worst case
            Dest("nntp", "items", 0, 100),   // 200 s worst case
        };
        double? r = WorstCaseEstimate.RemainingWorstCaseSeconds(dests, false, 0);
        Assert.Equal(200.0, r!.Value, 2);
    }

    [Fact]
    public void Remaining_FailedDestinationExcludedAndEstimateRebuilt()
    {
        var dests = new[]
        {
            Dest("nntp", "items", 0, 100, failed: true),  // would be 200 s
            Dest("usb", "chunks", 0, 10),                // ~0.31 s
        };
        double? r = WorstCaseEstimate.RemainingWorstCaseSeconds(dests, false, 0);
        Assert.NotNull(r);
        Assert.Equal(0.3125, r!.Value, 3);
    }

    [Fact]
    public void Remaining_UsesObservedAverageUnitBytes()
    {
        // 50 chunks moved 100 MiB -> 2 MiB each; 50 left to a USB
        // destination at its 4 MiB/s floor: 25 s, doubled to 50 s.
        var dests = new[] { Dest("usb", "chunks", 50, 100, bytes: 100 * MiB) };
        double? r = WorstCaseEstimate.RemainingWorstCaseSeconds(dests, false, 0);
        Assert.Equal(50.0, r!.Value, 2);
    }

    [Fact]
    public void Remaining_VerifyAllowanceAddedOnlyWhenExpected()
    {
        var dests = new[] { Dest("nntp", "items", 0, 100) };
        double? withVerify = WorstCaseEstimate.RemainingWorstCaseSeconds(
            dests, expectVerify: true, verifyBytes: 500 * MiB);
        double? without = WorstCaseEstimate.RemainingWorstCaseSeconds(
            dests, expectVerify: false, verifyBytes: 500 * MiB);
        Assert.Equal(210.0, withVerify!.Value, 2); // 200 + 500 MiB / 50 MiB/s
        Assert.Equal(200.0, without!.Value, 2);
    }

    // ---------- WorstCaseEstimate.FormatTotal ----------

    [Fact]
    public void Format_NullRemainingGivesNoEstimate()
        => Assert.Null(WorstCaseEstimate.FormatTotal(120, null));

    [Fact]
    public void Format_Minutes()
        => Assert.Equal("No more than 13 minutes total",
            WorstCaseEstimate.FormatTotal(0, 780));

    [Fact]
    public void Format_RoundsUpAndCountsElapsed()
        => Assert.Equal("No more than 2 minutes total",
            WorstCaseEstimate.FormatTotal(30, 31)); // 61 s total -> 2 min

    [Fact]
    public void Format_OneMinuteFloor()
        => Assert.Equal("No more than 1 minute total",
            WorstCaseEstimate.FormatTotal(10, 5));

    [Fact]
    public void Format_Hours()
    {
        Assert.Equal("No more than 1 hour total",
            WorstCaseEstimate.FormatTotal(0, 3600));
        Assert.Equal("No more than 1 hour 5 minutes total",
            WorstCaseEstimate.FormatTotal(0, 3900));
        Assert.Equal("No more than 2 hours 1 minute total",
            WorstCaseEstimate.FormatTotal(0, 7260));
    }

    // ---------- RunProgressTracker ----------

    [Fact]
    public void Snapshot_HeadlineMirrorsSlowestChunkDestination()
    {
        var t = new RunProgressTracker("docs");
        t.RegisterDestination("usb-stick", "usb", "chunks");
        t.RegisterDestination("nas", "smb", "chunks");
        t.SetDestinationTotal("usb-stick", 351);
        t.SetDestinationTotal("nas", 351);
        t.SetDestinationDone("usb-stick", 127);
        t.SetDestinationDone("nas", 300);
        var snap = t.Snapshot();
        // Same chunks mirrored to both: the headline is one plan,
        // paced by the destination furthest behind.
        Assert.Equal(351, snap.TotalUnits);
        Assert.Equal(127, snap.DoneUnits);
        Assert.Equal(36.2, snap.Percent);
        Assert.Equal("chunks", snap.HeadlineUnit);
        Assert.False(snap.Degraded);
    }

    [Fact]
    public void Snapshot_DegradedOnlyWhenAnotherDestinationRemains()
    {
        var t = new RunProgressTracker("docs");
        t.RegisterDestination("usb-stick", "usb", "chunks");
        t.RegisterDestination("Usenet", "nntp", "items");
        t.FailDestination("usb-stick", "drive gone");
        Assert.True(t.Snapshot().Degraded);

        var solo = new RunProgressTracker("solo");
        solo.RegisterDestination("usb-stick", "usb", "chunks");
        solo.FailDestination("usb-stick", "drive gone");
        Assert.False(solo.Snapshot().Degraded);
    }

    [Fact]
    public void Snapshot_EstimateRebuiltAroundSurvivingDestination()
    {
        var t = new RunProgressTracker("docs");
        t.RegisterDestination("Usenet", "nntp", "items");
        t.RegisterDestination("usb-stick", "usb", "chunks");
        t.SetDestinationTotal("Usenet", 100);
        t.SetDestinationTotal("usb-stick", 10);
        t.SetDestinationDone("usb-stick", 10);
        Assert.NotEqual("No more than 1 minute total", t.Snapshot().EstimateText);
        // Usenet drops out: only the finished USB destination bounds
        // the run, so the total collapses to the floor.
        t.FailDestination("Usenet", "provider down");
        var snap = t.Snapshot();
        Assert.True(snap.Degraded);
        Assert.Equal("No more than 1 minute total", snap.EstimateText);
        var usb = snap.Destinations.Single(d => d.Name == "usb-stick");
        Assert.Equal(100.0, usb.Percent);
    }

    [Fact]
    public void Snapshot_NoEstimateOrPercentBeforeTotals()
    {
        var t = new RunProgressTracker("docs");
        t.RegisterDestination("usb-stick", "usb", "chunks");
        t.AddDestinationDone("usb-stick", 3);
        var snap = t.Snapshot();
        Assert.Null(snap.EstimateText);
        Assert.Equal(-1, snap.Percent);
        Assert.Equal(-1, snap.TotalUnits);
    }

    [Fact]
    public void Snapshot_TotalsGrowOnlyAndDoneNeverExceedsTotal()
    {
        var t = new RunProgressTracker("docs");
        t.RegisterDestination("usb-stick", "usb", "chunks");
        t.SetDestinationTotal("usb-stick", 100);
        t.SetDestinationTotal("usb-stick", 50);   // shrink attempt ignored
        t.SetDestinationDone("usb-stick", 150);   // over-run grows the total
        var d = t.Snapshot().Destinations.Single();
        Assert.Equal(150, d.TotalUnits);
        Assert.Equal(150, d.DoneUnits);
        Assert.Equal(100.0, d.Percent);
    }

    [Fact]
    public void Snapshot_PhaseAndFilesScanned()
    {
        var t = new RunProgressTracker("docs");
        t.AddFilesScanned(12);
        t.AddFilesScanned(3);
        t.SetPhase(RunPhases.Uploading);
        var snap = t.Snapshot();
        Assert.Equal(15, snap.FilesScanned);
        Assert.Equal("uploading", snap.Phase);
        Assert.Equal("docs", snap.JobName);
    }

    // ---------- Dashboard DTO through the production JSON context ----------

    [Fact]
    public void RunProgressDto_SerializesThroughProductionContext()
    {
        var t = new RunProgressTracker("docs");
        t.RegisterDestination("usb-stick", "usb", "chunks");
        t.SetDestinationTotal("usb-stick", 10);
        t.SetDestinationDone("usb-stick", 4);
        var dto = new RunProgressDto(true, t.Snapshot());

        var info = ProductionJson.Resolver.GetTypeInfo(typeof(RunProgressDto), ProductionJson.Options);
        Assert.NotNull(info);
        string json = JsonSerializer.Serialize(dto, info!);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("active").GetBoolean());
        var progress = root.GetProperty("progress");
        Assert.Equal("docs", progress.GetProperty("jobName").GetString());
        Assert.Equal("chunks", progress.GetProperty("headlineUnit").GetString());
        var dest = progress.GetProperty("destinations")[0];
        Assert.Equal("usb-stick", dest.GetProperty("name").GetString());
        Assert.Equal(4, dest.GetProperty("doneUnits").GetInt64());
        Assert.Equal(10, dest.GetProperty("totalUnits").GetInt64());
        Assert.False(dest.GetProperty("failed").GetBoolean());
    }

    // ---------- Scheduler surface ----------

    [Fact]
    public void Scheduler_NoRunInFlight_ProgressNullAndCancelRefused()
    {
        var scheduler = new BackupScheduler(new ServiceConfig());
        Assert.Null(scheduler.GetRunProgress());
        Assert.False(scheduler.CancelRun("anything"));
    }
}

/// <summary>
/// The one-time backup path runs through the same Execute wrapper
/// that publishes run progress: it must complete successfully and
/// leave no progress behind (a stuck tracker would pin a phantom
/// progress row on the dashboard forever).
/// </summary>
public sealed class RunProgressSchedulerTests : IDisposable
{
    private const string Passphrase = "test-passphrase-123";
    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly string? _savedPassphrase;

    public RunProgressSchedulerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-rp-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "hello"u8.ToArray());
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Passphrase);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void OneTimeBackup_CompletesAndClearsRunProgress()
    {
        var scheduler = new BackupScheduler(new ServiceConfig());
        var result = scheduler.RunOneTimeBackup(new[] { _srcDir }, _repoDir);
        Assert.True(result.Success);
        Assert.Null(scheduler.GetRunProgress());
        Assert.False(scheduler.CancelRun("One-time backup"));
    }
}
