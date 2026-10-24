using UsenetBackup.Core.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Multi-folder backup sources: a job (or one-time backup) can list
/// several source folders; each gets its own manifest per run, and nested
/// or duplicate folders are rejected.
/// </summary>
public sealed class MultiSourceTests : IDisposable
{
    private const string Passphrase = "test-passphrase-123";
    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcA;
    private readonly string _srcB;
    private readonly string? _savedPassphrase;

    public MultiSourceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-ms-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcA = Path.Combine(_workDir, "a");
        _srcB = Path.Combine(_workDir, "b");
        Directory.CreateDirectory(_srcA);
        Directory.CreateDirectory(_srcB);
        File.WriteAllBytes(Path.Combine(_srcA, "a.txt"), "aaa"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_srcB, "b.txt"), "bbb"u8.ToArray());
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Passphrase);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EffectiveSources_ReturnsSourcesList()
    {
        var job = new BackupJobConfig { Sources = new List<string> { _srcA, _srcB } };
        Assert.Equal(new[] { _srcA, _srcB }, job.EffectiveSources);
    }

    [Fact]
    public void EffectiveSources_DropsBlankEntries()
    {
        var job = new BackupJobConfig { Sources = new List<string> { "", "  ", _srcA } };
        Assert.Equal(new[] { _srcA }, job.EffectiveSources);
    }

    [Fact]
    public void EffectiveSources_EmptyWhenNothingSet()
    {
        Assert.Empty(new BackupJobConfig().EffectiveSources);
    }

    [Fact]
    public void ValidateNoNestedSources_RejectsNested()
    {
        var job = new BackupJobConfig
        {
            Name = "j",
            Sources = new List<string> { _srcA, Path.Combine(_srcA, "sub") },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceConfig.ValidateNoNestedSources(job));
        Assert.Contains("inside", ex.Message);
    }

    [Fact]
    public void ValidateNoNestedSources_RejectsDuplicate()
    {
        var job = new BackupJobConfig
        {
            Name = "j",
            Sources = new List<string> { _srcA, _srcA + Path.DirectorySeparatorChar },
        };
        Assert.Throws<InvalidOperationException>(() => ServiceConfig.ValidateNoNestedSources(job));
    }

    [Fact]
    public void ValidateNoNestedSources_AcceptsSiblings()
    {
        var job = new BackupJobConfig
        {
            Name = "j",
            Sources = new List<string> { _srcA, _srcB },
        };
        ServiceConfig.ValidateNoNestedSources(job); // must not throw
    }

    [Fact]
    public void RunOneTimeBackup_CreatesOneManifestPerSource()
    {
        var config = new ServiceConfig { DashboardBind = "127.0.0.1", DashboardPort = 15789 };
        var scheduler = new BackupScheduler(config);
        var result = scheduler.RunOneTimeBackup(new[] { _srcA, _srcB }, _repoDir);
        Assert.True(result.Success, result.Error);
        string[] ids = (result.BackupId ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, ids.Length);

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var sources = repo.ListBackups()
            .Where(b => ids.Contains(b.BackupId))
            .Select(b => b.Source)
            .OrderBy(s => s)
            .ToList();
        Assert.Equal(2, sources.Count);
        // Each manifest records its own source folder.
        Assert.Contains(Path.GetFullPath(_srcA), sources);
        Assert.Contains(Path.GetFullPath(_srcB), sources);
    }

    [Fact]
    public void RunOneTimeBackup_EachSourceRestoresItsOwnFiles()
    {
        var config = new ServiceConfig { DashboardBind = "127.0.0.1", DashboardPort = 15789 };
        var scheduler = new BackupScheduler(config);
        var result = scheduler.RunOneTimeBackup(new[] { _srcA, _srcB }, _repoDir);
        Assert.True(result.Success, result.Error);
        string[] ids = (result.BackupId ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        string dest = Path.Combine(_workDir, "restored");
        foreach (string id in ids)
        {
            var manifest = repo.LoadManifest(id);
            repo.Restore(id, dest);
            // The manifest for _srcA restores a.txt; the one for _srcB restores b.txt.
            string expected = manifest.Source == Path.GetFullPath(_srcA) ? "a.txt" : "b.txt";
            Assert.True(File.Exists(Path.Combine(dest, expected)), $"missing {expected} for source {manifest.Source}");
        }
    }
}
