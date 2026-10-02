using System.Text.Json;
using UsenetBackup.Core;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Milestone 7: the dashboard's endpoint logic (status shaping, job lookup,
/// repo scoping, log tailing). The logic lives in transport-free
/// <see cref="DashboardApi"/> so it is testable without binding TCP —
/// loopback TCP from a test process is blocked in this sandbox, and the
/// Kestrel wiring itself is verified by smoke-testing the service binary.
/// </summary>
public sealed class DashboardTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly string? _savedPassphrase;

    public DashboardTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "dash"u8.ToArray());
        BackupRepository.Init(_repoDir, Passphrase, 64 * 1024, 10_000).Dispose();
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Passphrase);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private ServiceConfig MakeConfig() => new()
    {
        DashboardBind = "127.0.0.1",
        DashboardPort = 15789,
        Jobs = new List<BackupJobConfig>
        {
            new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                    Schedule = "interval 60", Mode = "incremental", Vss = false },
        },
    };

    private static JsonDocument ToJson(object payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload));

    [Fact]
    public void ApiStatus_ReflectsSchedulerState()
    {
        var scheduler = new BackupScheduler(MakeConfig());
        var result = scheduler.RunJobNow("docs");
        Assert.True(result.Success);

        using var doc = ToJson(DashboardApi.GetStatus(scheduler));
        var job = doc.RootElement.GetProperty("jobs")[0];
        Assert.Equal("docs", job.GetProperty("name").GetString());
        Assert.Equal("interval 60", job.GetProperty("schedule").GetString());
        Assert.Equal("incremental", job.GetProperty("mode").GetString());
        var last = job.GetProperty("lastResult");
        Assert.True(last.GetProperty("success").GetBoolean());
        Assert.Equal(result.BackupId, last.GetProperty("backupId").GetString());
        Assert.Equal(1, last.GetProperty("files").GetInt32());
    }

    [Fact]
    public void IsKnownJob_MatchesCaseInsensitively()
    {
        var scheduler = new BackupScheduler(MakeConfig());
        Assert.True(DashboardApi.IsKnownJob(scheduler, "docs"));
        Assert.True(DashboardApi.IsKnownJob(scheduler, "DOCS"));
        Assert.False(DashboardApi.IsKnownJob(scheduler, "nope"));
    }

    [Fact]
    public void GetBackups_ListsBackupsAndScopesRepos()
    {
        var config = MakeConfig();
        var scheduler = new BackupScheduler(config);
        scheduler.RunJobNow("docs");

        var (status, payload) = DashboardApi.GetBackups(config, _repoDir);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("full", doc.RootElement[0].GetProperty("Type").GetString());

        var (bad, _) = DashboardApi.GetBackups(config, "/tmp");
        Assert.Equal(400, bad);
    }

    [Fact]
    public void GetBackups_RequiresPassphrase()
    {
        var config = MakeConfig();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        try
        {
            var (status, _) = DashboardApi.GetBackups(config, _repoDir);
            Assert.Equal(500, status);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Passphrase);
        }
    }

    [Fact]
    public void GetLog_TailsOperationsLogAndScopesRepos()
    {
        var config = MakeConfig();
        var (status, payload) = DashboardApi.GetLog(config, _repoDir, lines: 10);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.True(doc.RootElement.GetArrayLength() >= 1);
        Assert.Contains("init", doc.RootElement[0].GetString());

        // Clamp: asking for 5000 lines returns at most 1000.
        var (_, big) = DashboardApi.GetLog(config, _repoDir, lines: 5000);
        using var bigDoc = ToJson(big);
        Assert.True(bigDoc.RootElement.GetArrayLength() <= 1000);

        var (bad, _) = DashboardApi.GetLog(config, "/tmp");
        Assert.Equal(400, bad);
    }

    [Fact]
    public void HtmlPage_ContainsDashboardElements()
    {
        string html = DashboardHtml.Page();
        Assert.Contains("<title>Usenet Backup</title>", html);
        Assert.Contains("/api/status", html);
        Assert.Contains("/api/jobs/", html);
        Assert.Contains("/api/backups", html);
        Assert.Contains("/api/log", html);
    }
}
