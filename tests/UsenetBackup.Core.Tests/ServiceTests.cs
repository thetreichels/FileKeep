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
        Directory.CreateDirectory(Path.Combine(_workDir, "share"));
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

    // The job targets a local-folder share ("vault") so scheduler runs
    // really deliver. (It used to target a bare "nntp" with no providers
    // configured and "succeed" vacuously — DL-3 removed that path; a
    // provider-less run now fails, as it should.)
    private string ValidConfigJson(string schedule = "interval 60", string mode = "incremental") => $$"""
        {
          "dashboardPort": 15789,
          "dashboardBind": "127.0.0.1",
          "locations": [
            { "name": "vault", "type": "smb",
              "share": "{{Path.Combine(_workDir, "share").Replace("\\", "\\\\")}}" }
          ],
          "jobs": [
            { "name": "docs", "repo": "{{_repoDir.Replace("\\", "\\\\")}}",
              "source": "{{_srcDir.Replace("\\", "\\\\")}}",
              "schedule": "{{schedule}}", "mode": "{{mode}}", "backupPrivilege": false,
              "targets": ["vault"] }
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
        Assert.False(job.BackupPrivilege);
    }

    [Fact]
    public void Config_ShippedExampleLoads()
    {
        // The example shipped in the MSI seeds fresh installs. If it fails
        // strict JSON parsing, the service can't start on a new machine.
        // Locate it relative to the test assembly: ../../../../../src/UsenetBackup.Service/
        string? dir = Path.GetDirectoryName(typeof(ServiceTests).Assembly.Location);
        string example = Path.GetFullPath(Path.Combine(dir!, "..", "..", "..", "..", "..",
            "src", "UsenetBackup.Service", "service.example.json"));
        Assert.True(File.Exists(example), $"Example not found at {example}");
        var config = ServiceConfig.Load(example);
        // Fresh installs start with an empty job list by design (2bb45a5
        // removed the baked-in sample jobs); the user creates jobs through
        // the dashboard. The example must still load and validate cleanly.
        Assert.Empty(config.Jobs);
        config.Validate();
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
    public void Config_RejectsDuplicateNamesAndBadMode()
    {
        string dup = ValidConfigJson().Replace("\"name\": \"docs\"", "\"name\": \"DOCS\"");
        // Two jobs with the same name (case-insensitive).
        string two = dup.Replace("]", ",{\"name\":\"docs\",\"repo\":\"r\",\"source\":\"s\",\"schedule\":\"interval 60\"}]");
        Assert.Throws<InvalidOperationException>(() => ServiceConfig.Load(WriteConfig(_workDir, two)));

        Assert.Throws<InvalidOperationException>(() =>
            ServiceConfig.Load(WriteConfig(_workDir, ValidConfigJson(mode: "sometimes"))));
    }

    [Fact]
    public void Config_AllowsEmptyJobs()
    {
        // A fresh install has no jobs yet; the service must start so the
        // dashboard is available for creating the first job.
        string noJobs = """{ "jobs": [] }""";
        var config = ServiceConfig.Load(WriteConfig(_workDir, noJobs));
        Assert.Empty(config.Jobs);
    }

    // ---------- SF-21/SF-22: locations, providers, numeric/enum fields ----------

    private static ServiceConfig ValidObjectConfig() => new()
    {
        Jobs = new List<BackupJobConfig>
        {
            new() { Name = "docs", Repo = "r", Source = "s", Schedule = "interval 60" },
        },
    };

    private static BackupLocation SmbLocation(string name) =>
        new() { Name = name, Type = "smb", Share = @"\\server\share" };

    [Fact]
    public void TryGetPassphrase_UndecryptableStoredBlob_ReportsTheRealProblem()
    {
        // MU-12: a stored passphrase that no longer decrypts (service
        // account/machine changed) must not be reported as "no
        // passphrase configured" — the remedy is different.
        string? saved = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        try
        {
            // Nothing configured at all: no passphrase, NO problem —
            // callers phrase the not-configured case themselves.
            var empty = new ServiceConfig();
            Assert.False(BackupScheduler.TryGetPassphrase(empty, out string? p0, out string? problem0));
            Assert.Null(p0);
            Assert.Null(problem0);

            // A stored blob that cannot be decrypted here: still no
            // passphrase, but now WITH the honest problem.
            var broken = new ServiceConfig
            {
                PassphraseProtected = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
            };
            Assert.False(BackupScheduler.TryGetPassphrase(broken, out string? p1, out string? problem1));
            Assert.Null(p1);
            Assert.NotNull(problem1);
            Assert.Contains("cannot be decrypted", problem1);
            Assert.Contains("Re-enter the passphrase", problem1);
            // And the legacy wrapper keeps its null contract.
            Assert.Null(BackupScheduler.GetPassphrase(broken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, saved);
        }
    }

    [Fact]
    public void Validate_DuplicateLocationNames_Rejected()
    {
        // SF-21: resolution is FirstOrDefault — a duplicate silently
        // shadowed the first location.
        var config = ValidObjectConfig();
        config.Locations = new List<BackupLocation> { SmbLocation("nas"), SmbLocation("NAS") };
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Duplicate location name", ex.Message);
    }

    [Fact]
    public void Validate_LocationMissingFields_Rejected()
    {
        var unknownType = ValidObjectConfig();
        unknownType.Locations = new List<BackupLocation>
            { new() { Name = "x", Type = "ftp", Share = @"\\s\x" } };
        Assert.Throws<InvalidOperationException>(() => unknownType.Validate());

        var smbNoShare = ValidObjectConfig();
        smbNoShare.Locations = new List<BackupLocation>
            { new() { Name = "x", Type = "smb", Share = "" } };
        Assert.Throws<InvalidOperationException>(() => smbNoShare.Validate());

        var usbNoSerial = ValidObjectConfig();
        usbNoSerial.Locations = new List<BackupLocation>
            { new() { Name = "x", Type = "usb", VolumeSerial = "" } };
        Assert.Throws<InvalidOperationException>(() => usbNoSerial.Validate());

        var nntpNoHost = ValidObjectConfig();
        nntpNoHost.Locations = new List<BackupLocation>
            { new() { Name = "x", Type = "nntp", Host = "" } };
        Assert.Throws<InvalidOperationException>(() => nntpNoHost.Validate());
    }

    [Fact]
    public void Validate_JobTargets_MustBeBareOrDefinedLocations()
    {
        // SF-21: an unknown target name was treated as Active by the
        // gate and silently dropped whenever another target resolved.
        var bad = ValidObjectConfig();
        bad.Locations = new List<BackupLocation> { SmbLocation("nas") };
        bad.Jobs[0].Targets = new List<string> { "nas", "typo-location" };
        var ex = Assert.Throws<InvalidOperationException>(() => bad.Validate());
        Assert.Contains("typo-location", ex.Message);

        // Defined locations and legacy bare targets validate fine.
        var ok = ValidObjectConfig();
        ok.Locations = new List<BackupLocation> { SmbLocation("nas") };
        ok.Jobs[0].Targets = new List<string> { "nas" };
        ok.Validate();

        var bare = ValidObjectConfig();
        bare.Jobs[0].Targets = new List<string> { "nntp" };
        bare.Validate();
    }

    [Fact]
    public void Validate_JobEnumAndNumericFields_Rejected()
    {
        // SF-22: unknown redundancyMode silently behaved as "none";
        // unknown verificationMode surfaced only at backup start;
        // parityGroupSize < 2 with xor hung or silently made no parity.
        var badRedundancy = ValidObjectConfig();
        badRedundancy.Jobs[0].RedundancyMode = "raid6";
        Assert.Throws<InvalidOperationException>(() => badRedundancy.Validate());

        var badVerify = ValidObjectConfig();
        badVerify.Jobs[0].VerificationMode = "thorough";
        Assert.Throws<InvalidOperationException>(() => badVerify.Validate());

        var xorTooSmall = ValidObjectConfig();
        xorTooSmall.Jobs[0].RedundancyMode = "xor";
        xorTooSmall.Jobs[0].ParityGroupSize = 1;
        Assert.Throws<InvalidOperationException>(() => xorTooSmall.Validate());

        var xorZero = ValidObjectConfig();
        xorZero.Jobs[0].RedundancyMode = "xor";
        xorZero.Jobs[0].ParityGroupSize = 0;
        Assert.Throws<InvalidOperationException>(() => xorZero.Validate());

        // Dormant values are fine when xor is not in play.
        var dormant = ValidObjectConfig();
        dormant.Jobs[0].ParityGroupSize = 0;
        dormant.Validate();

        var xorOk = ValidObjectConfig();
        xorOk.Jobs[0].RedundancyMode = "xor";
        xorOk.Jobs[0].ParityGroupSize = 10;
        xorOk.Validate();
    }

    [Fact]
    public void Validate_ProvidersCheckedLikeTheLegacySingleton()
    {
        var badPort = ValidObjectConfig();
        badPort.NntpProviders = new List<NntpConfig>
            { new() { Host = "news.example.com", Port = 0 } };
        Assert.Throws<InvalidOperationException>(() => badPort.Validate());

        var badRedundancy = ValidObjectConfig();
        badRedundancy.NntpProviders = new List<NntpConfig>
            { new() { Host = "news.example.com", RedundancyMode = "raid6" } };
        Assert.Throws<InvalidOperationException>(() => badRedundancy.Validate());

        // A blank-host placeholder entry is tolerated (uploads skip it
        // with a log line — SF-14), even with dormant bad fields.
        var placeholder = ValidObjectConfig();
        placeholder.NntpProviders = new List<NntpConfig>
            { new() { Host = "", Port = 0 } };
        placeholder.Validate();
    }

    [Fact]
    public void Validate_NegativeRetentionInterval_Rejected()
    {
        var config = ValidObjectConfig();
        config.RetentionCheckIntervalHours = -5;
        Assert.Throws<InvalidOperationException>(() => config.Validate());
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
        // Point the job at a source dir that doesn't exist. (Repos auto-initialize
        // on first use now, so a missing repo no longer fails the run.)
        // Note: the JSON escapes backslashes, so replace the escaped form.
        string escapedSrc = _srcDir.Replace("\\", "\\\\");
        string noSource = Path.Combine(_workDir, "no-source").Replace("\\", "\\\\");
        string json = ValidConfigJson().Replace(escapedSrc, noSource);
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
