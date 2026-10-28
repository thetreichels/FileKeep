using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Config-lifecycle split, Stage 2: persistence lives in
/// <see cref="ServiceConfigPersistence"/> and
/// <see cref="ServiceConfig.Load"/> / <see cref="ServiceConfig.Save"/>
/// only delegate. These tests pin the frozen save/load semantics
/// through the facade: atomic replace, temp cleanup on failure, and
/// concurrent saves never sharing a temp file. All file IO here is
/// deliberate — unlike the Stage 1 validator tests, persistence can
/// only be proven against real files. No test depends on timing:
/// the concurrent tests assert only what atomic rename guarantees
/// (the final file is one complete write, never torn).
/// </summary>
public sealed class ServiceConfigPersistenceTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "fk-persist-" + Guid.NewGuid().ToString("N"));

    public ServiceConfigPersistenceTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static BackupJobConfig ValidJob(string name = "docs") => new()
    {
        Name = name,
        Repo = "r",
        Sources = new List<string> { "s" },
        Schedule = "interval 60",
    };

    private static ServiceConfig ValidConfig(int port = 15789) => new()
    {
        DashboardPort = port,
        Jobs = new List<BackupJobConfig> { ValidJob() },
    };

    private string PathFor(string name) => Path.Combine(_workDir, name);

    private string[] TempLeftovers() => Directory.GetFiles(_workDir, "*.tmp-*");

    [Fact]
    public void Facade_SaveThenLoad_RoundTripsFields()
    {
        string path = PathFor("service.json");
        var config = ValidConfig(port: 18001);
        config.Save(path);

        var loaded = ServiceConfig.Load(path);
        Assert.Equal(18001, loaded.DashboardPort);
        Assert.Single(loaded.Jobs);
        Assert.Equal("docs", loaded.Jobs[0].Name);
        Assert.Equal("r", loaded.Jobs[0].Repo);
        Assert.Empty(TempLeftovers());
    }

    [Fact]
    public void Save_ReplacesExisting()
    {
        string path = PathFor("service.json");
        ValidConfig(port: 18001).Save(path);
        ValidConfig(port: 18002).Save(path);

        Assert.Equal(18002, ServiceConfig.Load(path).DashboardPort);
        Assert.Empty(TempLeftovers());
    }

    [Fact]
    public void Save_ValidatesOnSave_NothingWritten()
    {
        string path = PathFor("service.json");
        var bad = ValidConfig();
        bad.DashboardPort = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => bad.Save(path));
        Assert.Contains("out of range", ex.Message);
        Assert.False(File.Exists(path));
        Assert.Empty(TempLeftovers());
    }

    [Fact]
    public void Load_MissingFile_ThrowsCannotRead()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ServiceConfig.Load(PathFor("nope.json")));
        Assert.Contains("Cannot read service config", ex.Message);
    }

    [Fact]
    public void Load_Garbage_ThrowsInvalid()
    {
        string path = PathFor("service.json");
        File.WriteAllText(path, "{ not json");
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceConfig.Load(path));
        Assert.Contains("Invalid service config", ex.Message);
    }

    [Fact]
    public void Load_InvalidConfig_ValidatesOnLoad()
    {
        string path = PathFor("service.json");
        File.WriteAllText(path,
            """{"dashboardPort":0,"dashboardBind":"127.0.0.1","jobs":[]}""");
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceConfig.Load(path));
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void Save_MoveFailure_CleansTempAndLeavesTargetUntouched()
    {
        // The target is an existing directory, so the temp writes fine
        // but the move fails. The temp must not survive, and the
        // directory itself must be untouched. The exception shape is
        // platform OS behavior, not product behavior: Linux throws
        // IOException, Windows throws UnauthorizedAccessException
        // ("Access to the path is denied"). The pins are cleanup +
        // untouched target, not the exact type.
        string dirTarget = PathFor("service.json");
        Directory.CreateDirectory(dirTarget);

        if (OperatingSystem.IsWindows())
            Assert.Throws<UnauthorizedAccessException>(() => ValidConfig().Save(dirTarget));
        else
            Assert.Throws<IOException>(() => ValidConfig().Save(dirTarget));
        Assert.Empty(TempLeftovers());
        Assert.True(Directory.Exists(dirTarget));
        Assert.Empty(Directory.GetFileSystemEntries(dirTarget));
    }

    [Fact]
    public void Save_WriteFailure_CleansTemp()
    {
        // The temp's parent is a regular file, so the write itself
        // fails. No temp may survive (this pins the shared-try shape:
        // the original code only cleaned up on move failure).
        string blocker = PathFor("blocker");
        File.WriteAllText(blocker, "x");
        string path = blocker + Path.DirectorySeparatorChar + "service.json";

        // ThrowsAny, not Throws: ENOTDIR surfaces as
        // DirectoryNotFoundException (an IOException subclass), and
        // the pin here is "an IO failure, no temp left", not the
        // exact subclass.
        Assert.ThrowsAny<IOException>(() => ValidConfig().Save(path));
        Assert.Empty(TempLeftovers());
    }

    [Fact]
    public void Save_ConcurrentSamePath_FinalFileIsOneCompleteWrite()
    {
        // Deterministic because the temp-write+move run under Save's
        // static gate: without it, Windows refuses concurrent
        // overwriting moves and the losers throw. Every save succeeds
        // and the final file is one complete write.
        string path = PathFor("service.json");
        var ports = Enumerable.Range(18010, 8).ToArray();
        var tasks = ports.Select(port =>
            Task.Run(() => ValidConfig(port).Save(path))).ToArray();
        Task.WaitAll(tasks);
        Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully));

        var loaded = ServiceConfig.Load(path);
        Assert.Contains(loaded.DashboardPort, ports);
        Assert.Empty(TempLeftovers());
    }

    [Fact]
    public void Save_ConcurrentDistinctPaths_AllCorrectNoLeftovers()
    {
        var paths = Enumerable.Range(18020, 8)
            .Select(port => (port, PathFor($"service-{port}.json"))).ToArray();
        var tasks = paths.Select(p =>
            Task.Run(() => ValidConfig(p.port).Save(p.Item2))).ToArray();
        Task.WaitAll(tasks);
        Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully));

        foreach (var (port, path) in paths)
            Assert.Equal(port, ServiceConfig.Load(path).DashboardPort);
        Assert.Empty(TempLeftovers());
    }

    [Fact]
    public void Persistence_Direct_MatchesFacade()
    {
        string path = PathFor("service.json");
        ServiceConfigPersistence.Save(ValidConfig(port: 18003), path);
        Assert.Equal(18003, ServiceConfig.Load(path).DashboardPort);

        string path2 = PathFor("service2.json");
        ValidConfig(port: 18004).Save(path2);
        Assert.Equal(18004, ServiceConfigPersistence.Load(path2).DashboardPort);
    }
}
