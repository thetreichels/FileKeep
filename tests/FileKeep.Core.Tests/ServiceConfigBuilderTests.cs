using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Config-lifecycle split, Stage 5: the builder replaces the dashboard's
/// temp-config validation trick (build a throwaway config, validate it).
/// These tests pin the builder to the trick's behavior: same composition
/// (live port/bind/Nntp, snapshot locations, jobs with the candidate
/// swapped in by name), same rejections, and output that passes Load's
/// validation through a real file. All file IO here is deliberate.
/// </summary>
public sealed class ServiceConfigBuilderTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "fk-builder-" + Guid.NewGuid().ToString("N"));

    public ServiceConfigBuilderTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static ServiceConfig LiveConfig() => new()
    {
        DashboardPort = 18003,
        DashboardBind = "127.0.0.1",
        Locations = new List<BackupLocation>
        {
            new() { Name = "nas", Type = "smb", Share = "\\\\nas\\backups" },
        },
        Jobs = new List<BackupJobConfig> { ValidJob("docs") },
    };

    private static BackupJobConfig ValidJob(string name) => new()
    {
        Name = name,
        Repo = "r",
        Sources = new List<string> { "s" },
        Schedule = "interval 60",
    };

    [Fact]
    public void Build_ValidNewJob_ReturnsCandidateWithJobSwappedIn()
    {
        var live = LiveConfig();
        var candidate = new ServiceConfigBuilder(live)
            .WithJob(new BackupJobConfig
            {
                Name = "photos",
                Repo = "r2",
                Sources = new List<string> { "p" },
                Schedule = "interval 60",
                Targets = new List<string> { "nas" },
            })
            .Build();

        Assert.Equal(18003, candidate.DashboardPort);
        Assert.Equal("127.0.0.1", candidate.DashboardBind);
        Assert.Single(candidate.Locations);
        Assert.Equal("nas", candidate.Locations[0].Name);
        Assert.Equal(2, candidate.Jobs.Count);
        Assert.Equal("docs", candidate.Jobs[0].Name);
        Assert.Equal("photos", candidate.Jobs[1].Name);
        // The live config is untouched: the candidate is a new object.
        Assert.Single(live.Jobs);
    }

    [Fact]
    public void Build_EditExistingJob_ReplacesByName()
    {
        var candidate = new ServiceConfigBuilder(LiveConfig())
            .WithJob(new BackupJobConfig
            {
                Name = "docs",
                Repo = "r-new",
                Sources = new List<string> { "s" },
                Schedule = "interval 60",
            })
            .Build();

        var job = Assert.Single(candidate.Jobs);
        Assert.Equal("docs", job.Name);
        Assert.Equal("r-new", job.Repo);
    }

    [Fact]
    public void Build_UnknownTarget_Throws()
    {
        var job = ValidJob("bad");
        job.Targets = new List<string> { "nope" };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceConfigBuilder(LiveConfig()).WithJob(job).Build());
        Assert.Contains("unknown backup location", ex.Message);
    }

    [Fact]
    public void Build_UnknownMode_Throws()
    {
        var job = ValidJob("bad");
        job.Mode = "bogus";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceConfigBuilder(LiveConfig()).WithJob(job).Build());
        Assert.Contains("unknown mode", ex.Message);
    }

    [Fact]
    public void Build_WithoutJob_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceConfigBuilder(LiveConfig()).Build());
        Assert.Contains("WithJob", ex.Message);
    }

    [Fact]
    public void Build_Output_PassesLoadValidation()
    {
        var candidate = new ServiceConfigBuilder(LiveConfig())
            .WithJob(new BackupJobConfig
            {
                Name = "photos",
                Repo = "r2",
                Sources = new List<string> { "p" },
                Schedule = "interval 60",
                Targets = new List<string> { "nas" },
            })
            .Build();

        string path = Path.Combine(_workDir, "service.json");
        candidate.Save(path);

        var loaded = ServiceConfig.Load(path);
        Assert.Equal(18003, loaded.DashboardPort);
        Assert.Equal(2, loaded.Jobs.Count);
        Assert.Equal("photos", loaded.Jobs[1].Name);
        Assert.Equal("r2", loaded.Jobs[1].Repo);
    }
}
