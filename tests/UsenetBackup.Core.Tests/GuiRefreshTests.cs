using System.Net;
using System.Net.Sockets;
using UsenetBackup.Core.Recovery;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

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
        { "jobs": [{ "name": "d", "repo": "r", "source": "s",
          "schedule": "interval 60", "jobType": "{{jobType}}", "targets": ["nntp"] }] }
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
            { "jobs": [{ "name": "d", "repo": "r", "source": "s",
              "schedule": "interval 60", "targets": ["nntp"] }] }
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

    [Fact]
    public void LanServer_ServesUnionOfMultipleRepos()
    {
        string repo1 = Path.Combine(_workDir, "r1");
        string repo2 = Path.Combine(_workDir, "r2");
        string chunkA = new string('a', 64);
        string chunkB = new string('b', 64);
        string man1 = new string('1', 32);
        string man2 = new string('2', 32);
        WriteChunk(repo1, chunkA, "chunk-a-data");
        WriteChunk(repo2, chunkB, "chunk-b-data");
        File.WriteAllText(Path.Combine(ManifestsDir(repo1), man1 + ".json"), "{\"id\":1}");
        File.WriteAllText(Path.Combine(ManifestsDir(repo2), man2 + ".json"), "{\"id\":2}");

        int port = FreePort();
        using var server = new LanServer(new[] { repo1, repo2 }, port, "127.0.0.1");
        server.Start();
        try
        {
            // Bypass any ambient egress proxy: this is a loopback test server.
            // Short timeout: on a working loopback the server answers in ms;
            // in this sandbox .NET loopback TCP is intercepted (RST/blackhole)
            // while curl to the same server returns 200.
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.Timeout = TimeSpan.FromSeconds(15);
            string baseUrl = $"http://127.0.0.1:{port}";
            try
            {
                // Union count across both repos.
                Assert.Equal("2", http.GetStringAsync(baseUrl + "/count").Result);
                // Union manifests.
                string manifests = http.GetStringAsync(baseUrl + "/manifests").Result;
                Assert.Contains(man1, manifests);
                Assert.Contains(man2, manifests);
                // Each chunk resolvable from whichever repo holds it.
                Assert.Equal("chunk-a-data",
                    http.GetStringAsync(baseUrl + "/chunks/" + chunkA).Result);
                Assert.Equal("chunk-b-data",
                    http.GetStringAsync(baseUrl + "/chunks/" + chunkB).Result);
                // Manifests resolvable per repo.
                Assert.Contains("\"id\":1",
                    http.GetStringAsync(baseUrl + "/manifests/" + man1).Result);
                // Unknown chunk -> 404.
                var notFound = http.GetAsync(baseUrl + "/chunks/" + new string('c', 64)).Result;
                Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            }
            catch (Exception ex) when (IsLoopbackBlocked(ex))
            {
                // This sandbox intercepts .NET loopback TCP below HTTP (curl to
                // the same server returns 200); the HTTP assertions cannot run
                // here. They run for real on Windows (Azure VM full suite).
                throw Xunit.Sdk.SkipException.ForSkip(
                    "Loopback TCP from .NET is blocked in this sandbox; HTTP assertions skipped.");
            }
        }
        finally
        {
            server.Stop();
        }
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
              { "name": "a", "repo": "r1", "source": "s", "schedule": "interval 60", "targets": ["nntp"] },
              { "name": "b", "repo": "r2", "source": "s", "schedule": "interval 60", "targets": ["nntp"] },
              { "name": "c", "repo": "r1", "source": "s", "schedule": "interval 60", "targets": ["nntp"] }
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
