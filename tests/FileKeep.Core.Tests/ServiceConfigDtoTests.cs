using System.Text.Json;
using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Config-lifecycle split, Stage 4: the DTO classes moved out of
/// ServiceConfig.cs into their own files (same namespace, same names,
/// same JSON attributes), so serialization must be unchanged. These
/// tests pin that: a representative config round-trips through the
/// real Save/Load path with every field intact, and the saved JSON
/// carries the same top-level and nested keys the move must not
/// rename, drop, or re-attribute. All file IO here is deliberate.
/// </summary>
public sealed class ServiceConfigDtoTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "fk-dto-" + Guid.NewGuid().ToString("N"));

    public ServiceConfigDtoTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static ServiceConfig RepresentativeConfig() => new()
    {
        DashboardPort = 18002,
        DashboardBind = "127.0.0.1",
        Jobs = new List<BackupJobConfig>
        {
            new()
            {
                Name = "docs",
                Repo = "r",
                Sources = new List<string> { "s" },
                Schedule = "interval 60",
                Mode = "incremental",
                JobType = "files",
                Targets = new List<string> { "nas" },
                RedundancyMode = "none",
                VerificationMode = "fast",
                ParityGroupSize = 10,
                Sparse = true,
                AutoVerify = true,
            },
        },
        Locations = new List<BackupLocation>
        {
            new()
            {
                Name = "nas",
                Type = "smb",
                Share = "\\\\nas\\backups",
                SmbUser = "u",
                RedundancyMode = "none",
            },
        },
        Nntp = new NntpConfig
        {
            Host = "news.example",
            Port = 563,
            Ssl = true,
            Username = "u",
            Connections = 10,
            Newsgroup = "alt.binaries.test",
            RetentionDays = 1095,
        },
        NntpProviders = new List<NntpConfig>
        {
            new()
            {
                Host = "news2.example",
                Port = 563,
                RedundancyMode = "none",
            },
        },
        PreviousVaults = new List<MigratedVaultRecord>
        {
            new()
            {
                Jobs = new List<string> { "docs" },
                OldRepo = "old",
                NewRepo = "new",
                MigratedUtc = "2026-10-28T00:00:00Z",
                OldTargets = new List<string> { "oldloc" },
            },
        },
        ProtectionLinkMode = "or",
        YubiKeys = new List<string> { "YubiKey 1" },
        RetentionEnabled = true,
        RetentionCheckIntervalHours = 24,
        RetentionWarnDays = 90,
        RetentionRepostThresholdDays = 30,
    };

    [Fact]
    public void DtoMove_RoundTrip_PreservesRepresentativeConfig()
    {
        string path = Path.Combine(_workDir, "service.json");
        RepresentativeConfig().Save(path);

        var loaded = ServiceConfig.Load(path);

        Assert.Equal(18002, loaded.DashboardPort);
        Assert.Equal("127.0.0.1", loaded.DashboardBind);

        var job = Assert.Single(loaded.Jobs);
        Assert.Equal("docs", job.Name);
        Assert.Equal("r", job.Repo);
        Assert.Equal(new[] { "s" }, job.Sources);
        Assert.Equal("interval 60", job.Schedule);
        Assert.Equal("incremental", job.Mode);
        Assert.Equal("files", job.JobType);
        Assert.Equal(new[] { "nas" }, job.EffectiveTargets);
        Assert.Equal("none", job.RedundancyMode);
        Assert.Equal("fast", job.VerificationMode);
        Assert.Equal(10, job.ParityGroupSize);
        Assert.True(job.Sparse);
        Assert.True(job.AutoVerify);
        // Pure computed members ride along unchanged.
        Assert.Equal(new[] { "s" }, job.EffectiveSources);
        Assert.Equal("s", job.FirstSource);

        var loc = Assert.Single(loaded.Locations);
        Assert.Equal("nas", loc.Name);
        Assert.Equal("smb", loc.Type);
        Assert.Equal("\\\\nas\\backups", loc.Share);
        Assert.Equal("u", loc.SmbUser);
        Assert.Equal("none", loc.RedundancyMode);

        Assert.NotNull(loaded.Nntp);
        Assert.Equal("news.example", loaded.Nntp!.Host);
        Assert.Equal(563, loaded.Nntp.Port);
        Assert.True(loaded.Nntp.Ssl);
        Assert.Equal("alt.binaries.test", loaded.Nntp.Newsgroup);
        Assert.Equal(1095, loaded.Nntp.RetentionDays);

        var provider = Assert.Single(loaded.NntpProviders);
        Assert.Equal("news2.example", provider.Host);
        Assert.Equal("none", provider.RedundancyMode);

        var vault = Assert.Single(loaded.PreviousVaults);
        Assert.Equal(new[] { "docs" }, vault.Jobs);
        Assert.Equal("old", vault.OldRepo);
        Assert.Equal("new", vault.NewRepo);
        Assert.Equal("2026-10-28T00:00:00Z", vault.MigratedUtc);
        Assert.Equal(new[] { "oldloc" }, vault.OldTargets);

        Assert.Equal("or", loaded.ProtectionLinkMode);
        Assert.Equal(new[] { "YubiKey 1" }, loaded.YubiKeys);
        Assert.True(loaded.RetentionEnabled);
        Assert.Equal(24, loaded.RetentionCheckIntervalHours);
        Assert.Equal(90, loaded.RetentionWarnDays);
        Assert.Equal(30, loaded.RetentionRepostThresholdDays);
    }

    [Fact]
    public void DtoMove_JsonShape_HasExpectedKeys()
    {
        string path = Path.Combine(_workDir, "service.json");
        RepresentativeConfig().Save(path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        foreach (string key in new[]
        {
            "dashboardPort", "dashboardBind", "jobs", "locations", "nntp",
            "nntpProviders", "previousVaults", "protectionLinkMode",
            "yubiKeys", "retentionEnabled", "retentionCheckIntervalHours",
            "retentionWarnDays", "retentionRepostThresholdDays",
        })
            Assert.True(root.TryGetProperty(key, out _), $"missing top-level key '{key}'");
        Assert.Equal(18002, root.GetProperty("dashboardPort").GetInt32());

        var jobJson = root.GetProperty("jobs")[0];
        foreach (string key in new[]
            { "name", "repo", "sources", "schedule", "mode", "jobType",
              "targets", "redundancyMode", "verificationMode", "parityGroupSize" })
            Assert.True(jobJson.TryGetProperty(key, out _), $"missing job key '{key}'");

        var locJson = root.GetProperty("locations")[0];
        foreach (string key in new[] { "name", "type", "share", "smbUser", "redundancyMode" })
            Assert.True(locJson.TryGetProperty(key, out _), $"missing location key '{key}'");

        var nntpJson = root.GetProperty("nntp");
        foreach (string key in new[] { "host", "port", "ssl", "username", "connections", "newsgroup", "retentionDays" })
            Assert.True(nntpJson.TryGetProperty(key, out _), $"missing nntp key '{key}'");

        var vaultJson = root.GetProperty("previousVaults")[0];
        foreach (string key in new[] { "jobs", "oldRepo", "newRepo", "migratedUtc", "oldTargets" })
            Assert.True(vaultJson.TryGetProperty(key, out _), $"missing vault key '{key}'");
    }
}
