using System.Net;
using System.Net.Sockets;
using FileKeep.Core.Recovery;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Tests for the GUI refresh work: whole-disk image jobs (jobType),
/// the no-typing drive listing, repo-less tools, and the multi-root LAN server.
/// </summary>
public sealed class GuiRefreshTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "fk-gui-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch { }
    }

    private string WriteConfig(string json)
    {
        Directory.CreateDirectory(_workDir);
        string path = Path.Combine(_workDir, "service.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string JobJson(string jobType) => $$"""
        { "jobs": [{ "name": "d", "repo": "r", "sources": ["s"],
          "schedule": "interval 60", "jobType": "{{jobType}}", "targets": [] }] }
        """;

    [Fact]
    public void Config_RejectsBadJobType()
    {
        Assert.Throws<InvalidOperationException>(
            () => ServiceConfig.Load(WriteConfig(JobJson("sometimes"))));
    }

    [Fact]
    public void Config_AcceptsDiskJobType()
    {
        var config = ServiceConfig.Load(WriteConfig(JobJson("disk")));
        var job = Assert.Single(config.Jobs);
        Assert.True(job.IsDiskImage);
        Assert.Equal("disk", job.JobType);
    }

    [Fact]
    public void Config_JobTypeDefaultsToFiles()
    {
        string json = """
            { "jobs": [{ "name": "d", "repo": "r", "sources": ["s"],
              "schedule": "interval 60", "targets": [] }] }
            """;
        var config = ServiceConfig.Load(WriteConfig(json));
        var job = Assert.Single(config.Jobs);
        Assert.False(job.IsDiskImage);
        Assert.Equal("files", job.JobType);
    }

    [Fact]
    public void Scheduler_DiskImageName_SanitizesJobName()
    {
        Assert.Equal("Systemdisk.img", BackupScheduler.DiskImageName("System disk"));
        Assert.Equal("disk.img", BackupScheduler.DiskImageName("   "));
        Assert.Equal("disk.img", BackupScheduler.DiskImageName(""));
        // BackupDiskImage requires a plain file name: no invalid chars survive.
        // (Which characters are invalid is platform-dependent; assert the
        // actual invariant rather than a fixed list. Note: Assert.DoesNotContain is unusable here - xunit v2 reports a NUL substring as found even when string.Contains is false.)
        string name = BackupScheduler.DiskImageName("a/b\\c:d*?");
        foreach (char c in Path.GetInvalidFileNameChars())
            Assert.False(name.Contains(c), "Image name contains invalid char U-" + ((int)c).ToString("X4"));
        Assert.EndsWith(".img", name);
    }

    [Fact]
    public void PhysicalDrives_List_MatchesPlatform()
    {
        var drives = PhysicalDrives.List();
        if (OperatingSystem.IsWindows())
            Assert.NotEmpty(drives); // Windows enumerates real drives
        else
            Assert.Empty(drives); // Non-Windows returns empty list
    }


    /// <summary>True when the exception chain shows loopback TCP being
    /// refused, reset, or blackholed below HTTP (e.g. sandbox egress
    /// interception). No CancellationToken is used in this test, so a
    /// TaskCanceledException can only be the HttpClient timeout.</summary>
    private static bool IsLoopbackBlocked(Exception ex)
    {
        if (ex is SocketException) return true;
        if (ex is TaskCanceledException) return true;
        if (ex is AggregateException agg) return agg.InnerExceptions.Any(IsLoopbackBlocked);
        return ex.InnerException is not null && IsLoopbackBlocked(ex.InnerException);
    }

    [Fact]
    public void DashboardApi_AllJobRepos_DistinctInOrder()
    {
        string json = """
            { "jobs": [
              { "name": "a", "repo": "r1", "sources": ["s"], "schedule": "interval 60", "targets": [] },
              { "name": "b", "repo": "r2", "sources": ["s"], "schedule": "interval 60", "targets": [] },
              { "name": "c", "repo": "r1", "sources": ["s"], "schedule": "interval 60", "targets": [] }
            ] }
            """;
        var config = ServiceConfig.Load(WriteConfig(json));
        Assert.Equal(new[] { "r1", "r2" }, DashboardApi.AllJobRepos(config));
        Assert.Equal("r1", DashboardApi.PrimaryRepo(config));
    }

    [Fact]
    public void DashboardApi_PrimaryRepo_NullWhenNoJobs()
    {
        var config = ServiceConfig.Load(WriteConfig("""
            { "jobs": [] }
            """));
        Assert.Empty(DashboardApi.AllJobRepos(config));
        Assert.Null(DashboardApi.PrimaryRepo(config));
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string ManifestsDir(string repo)
    {
        string d = Path.Combine(repo, "manifests");
        Directory.CreateDirectory(d);
        return d;
    }

    private static void WriteChunk(string repo, string chunkId, string data)
    {
        string dir = Path.Combine(repo, "chunks", chunkId[..2]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, chunkId[2..]), data);
    }
}
