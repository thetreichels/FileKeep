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
                    Schedule = "interval 60", Mode = "incremental", BackupPrivilege = false,
                    Targets = new List<string> { "nntp" } },
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
        Assert.Equal("full", doc.RootElement[0].GetProperty("type").GetString());
        // a.txt is 4 bytes -> one catalogued chunk -> storedBytes 4.
        Assert.Equal(4, doc.RootElement[0].GetProperty("storedBytes").GetInt64());

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
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.Contains("<title>FileKeep</title>", html);
        Assert.Contains("name=\"csrf-token\" content=\"test-csrf-token\"", html);
        Assert.Contains("X-CSRF-Token", html);
        Assert.Contains("/api/status", html);
        Assert.Contains("/api/jobs/", html);
        Assert.Contains("/api/backups", html);
        Assert.Contains("/api/log", html);
    }

    [Fact]
    public void HtmlPage_BakesBuildTokenAndSidebarCheck()
    {
        string html = DashboardHtml.Page("test-csrf-token");
        // The build placeholder must be fully substituted: an unsubstituted
        // "{{APP_VERSION}}" would bake in as a literal build token and the
        // sidebar stale-page check would reload on every navigation.
        Assert.Contains("name=\"app-build\"", html);
        Assert.False(html.Contains("{{APP_VERSION}}"), "APP_VERSION placeholder was not substituted");
        // The sidebar stale-page gate ships in the page.
        Assert.Contains("reloadIfBuildChanged", html);
        // The build token resolves without throwing and is stable across calls.
        string token = DashboardApi.GetBuildToken();
        Assert.NotNull(token);
        Assert.Equal(token, DashboardApi.GetBuildToken());
    }

    [Fact]
    public void ValidateCsrfToken_AcceptsOnlyExactMatch()
    {
        Assert.True(DashboardApi.ValidateCsrfToken("abc123", "abc123"));
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", "abc124"));
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", "abc12"));   // length differs
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", "abc1234")); // length differs
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", null));
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", ""));
        Assert.False(DashboardApi.ValidateCsrfToken("", "abc123"));
    }

    // ---------- walk fixes (2026-10-20 GUI verification walk) ----------

    [Fact]
    public void ResolveRestoreSmb_UsesJobsLocationWhenRequestIsBlank()
    {
        // WALK-3: a location-based job's restore must source its share
        // and user from the job's SMB target location — the job's own
        // legacy fields are empty for such jobs.
        var config = MakeConfig();
        var job = config.Jobs[0];
        job.Targets = new List<string> { "vault" };
        job.SmbUser = "";
        config.Locations.Add(new BackupLocation
        {
            Name = "vault", Type = "smb",
            Share = @"\\nas\backups", SmbUser = @"nas\alice",
        });
        var (share, user, _) = DashboardApi.ResolveRestoreSmb(config, _repoDir, null, null);
        Assert.Equal(@"\\nas\backups", share);
        Assert.Equal(@"nas\alice", user);
    }

    [Fact]
    public void ResolveRestoreSmb_ExplicitRequestWins()
    {
        var config = MakeConfig();
        config.Jobs[0].Targets = new List<string> { "vault" };
        config.Locations.Add(new BackupLocation
        {
            Name = "vault", Type = "smb",
            Share = @"\\nas\backups", SmbUser = @"nas\alice",
        });
        var (share, user, _) = DashboardApi.ResolveRestoreSmb(
            config, _repoDir, @"\\other\share", "bob");
        Assert.Equal(@"\\other\share", share);
        Assert.Equal("bob", user);
    }

    [Fact]
    public void TestSmbConnection_CredentialsTakeTheConnectPath()
    {
        // WALK-1: with credentials supplied, the test must go through
        // SmbShare.Connect (which rejects credentials for non-UNC
        // paths) instead of a bare existence check that would happily
        // pass for this existing local directory.
        var (status, payload) = DashboardApi.TestSmbConnection(_srcDir, "alice", "secret");
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("UNC", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void TestSmbConnection_NoCredentials_ExistenceCheck()
    {
        var (status, payload) = DashboardApi.TestSmbConnection(_srcDir);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void HtmlPage_HasSessionUnlockButtonAndHonestPlaceholder()
    {
        // WALK-2: the restore session row carries its own Unlock button
        // (the backups table's Restore buttons never render while
        // locked). WALK-5: the restore destination placeholder reads as
        // an example, not a prefilled value.
        string html = DashboardHtml.Page("test-csrf-token");
        Assert.Contains("restore-unlock-btn", html);
        Assert.Contains("Unlock with YubiKey", html);
        Assert.Contains("placeholder=\"e.g. C:\\Restore\"", html);
    }

    [Fact]
    public void DeleteJob_DeletesTheLastJob()
    {
        // Zero jobs is a valid state (the fresh-install state), so the
        // only job deletes like any other. The former last-job guard
        // (7943f06) was an invented invariant; removed at the user's
        // decision 2026-10-20.
        var config = MakeConfig();
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var (status, _) = DashboardApi.DeleteJob(config, scheduler, configPath, "docs");
        Assert.Equal(200, status);
        Assert.Empty(config.Jobs);
        Assert.Empty(ServiceConfig.Load(configPath).Jobs);
    }

    [Fact]
    public void ServiceConfig_RejectsAutoUploadWithoutProvider()
    {
        var config = new ServiceConfig
        {
            DashboardPort = 15789,
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "daily 02:00", AutoUpload = true },
            },
            Nntp = null,
        };
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("auto-upload", ex.Message);
    }

    [Fact]
    public void ServiceConfig_AcceptsAutoUploadWithProvider()
    {
        var config = new ServiceConfig
        {
            DashboardPort = 15789,
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "daily 02:00", AutoUpload = true },
            },
            Nntp = new NntpConfig { Host = "news.example.com", Port = 119 },
        };
        config.Validate(); // should not throw
    }

    [Fact]
    public void UpsertJob_AcceptsAutoUploadWhenProviderConfigured()
    {
        // Regression: UpsertJob built its validation config without Nntp,
        // so any job with AutoUpload=true failed with 400 even when a
        // provider was configured.
        var config = MakeConfig();
        config.Nntp = new NntpConfig { Host = "news.example.com", Port = 119 };
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var job = new BackupJobConfig
        {
            Name = "upload-job", Repo = _repoDir, Source = _srcDir,
            Schedule = "interval 60", Mode = "incremental",
            BackupPrivilege = false, AutoUpload = true,
        };
        var (status, _) = DashboardApi.UpsertJob(config, scheduler, configPath, job);
        Assert.Equal(200, status);
        Assert.Contains(config.Jobs, j => j.Name == "upload-job" && j.AutoUpload);
    }

    [Fact]
    public void UpsertJob_PreservesAutoUploadOnUpdate()
    {
        // Regression: UpsertJob did not copy AutoUpload when updating an
        // existing job, silently clearing the flag.
        var config = MakeConfig();
        config.Nntp = new NntpConfig { Host = "news.example.com", Port = 119 };
        config.Jobs[0].AutoUpload = true;
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var updated = new BackupJobConfig
        {
            Name = "docs", Repo = _repoDir, Source = _srcDir,
            Schedule = "interval 120", Mode = "incremental",
            BackupPrivilege = true, AutoUpload = true,
        };
        var (status, _) = DashboardApi.UpsertJob(config, scheduler, configPath, updated);
        Assert.Equal(200, status);
        Assert.True(config.Jobs[0].AutoUpload);
        Assert.True(config.Jobs[0].BackupPrivilege);
        Assert.Equal("interval 120", config.Jobs[0].Schedule);
    }

    [Fact]
    public void NntpConfig_PasswordBlobRoundTrips()
    {
        var config = new ServiceConfig
        {
            DashboardPort = 15789,
            Jobs = new List<BackupJobConfig>
            {
                new() { Name = "docs", Repo = _repoDir, Source = _srcDir,
                        Schedule = "daily 02:00" },
            },
            Nntp = new NntpConfig
            {
                Host = "news.example.com",
                Port = 563,
                Username = "user",
                Ssl = true,
                PasswordProtected = "dGVzdC1ibG9i", // base64 "test-blob"
            },
        };
        string path = Path.Combine(_workDir, "nntp-test.json");
        config.Save(path);
        string json = File.ReadAllText(path);
        Assert.Contains("passwordProtected", json);
        Assert.DoesNotContain("\"password\":", json); // plaintext never persisted

        var loaded = ServiceConfig.Load(path);
        Assert.Equal("dGVzdC1ibG9i", loaded.Nntp?.PasswordProtected);
        Assert.True(loaded.Nntp!.HasPassword);
    }

    [Fact]
    public void BackupJobConfig_JsonRoundTripsCamelCaseFlags()
    {
        // The dashboard UI sends camelCase; the model must accept it.
        string uiJson = @"{""name"":""Test"",""repo"":""/tmp/r"",""source"":""/tmp/s"",""schedule"":""daily 02:00"",""mode"":""incremental"",""backupPrivilege"":true,""autoUpload"":true}";
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var job = JsonSerializer.Deserialize<BackupJobConfig>(uiJson, opts);
        Assert.NotNull(job);
        Assert.True(job.BackupPrivilege);
        Assert.True(job.AutoUpload);
    }

    [Fact]
    public void UpdateNntp_ClearsWhenHostEmpty()
    {
        var config = MakeConfig();
        config.Nntp = new NntpConfig { Host = "news.example.com", Port = 119 };
        var scheduler = new BackupScheduler(config);
        string configPath = Path.Combine(_workDir, "service.json");
        config.Save(configPath);

        var (status, _) = DashboardApi.UpdateNntp(config, scheduler, configPath,
            new NntpConfig { Host = "" });
        Assert.Equal(200, status);
        Assert.Null(config.Nntp);
    }

    [Fact]
    public void Dpapi_ThrowsPlatformNotSupportedOnLinux()
    {
        if (OperatingSystem.IsWindows())
            return; // Real DPAPI test needs Windows; validated manually there.
        Assert.Throws<PlatformNotSupportedException>(() => Dpapi.Protect("test"));
        Assert.Throws<PlatformNotSupportedException>(() => Dpapi.Unprotect("dGVzdA=="));
    }

    // ---- GET /api/browse-folders (server-backed folder picker) ----

    [Fact]
    public void BrowseFolders_ListsSubfoldersOnlyWithParent()
    {
        string root = Path.Combine(_workDir, "browse");
        string subA = Path.Combine(root, "alpha");
        string subB = Path.Combine(root, "Beta");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);
        File.WriteAllText(Path.Combine(root, "file.txt"), "not a folder");

        var (status, payload) = DashboardApi.BrowseFolders(root);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        var el = doc.RootElement;
        Assert.Equal(Path.GetFullPath(root), el.GetProperty("path").GetString());
        Assert.Equal(Path.GetFullPath(_workDir), el.GetProperty("parent").GetString());
        var folders = el.GetProperty("folders");
        Assert.Equal(2, folders.GetArrayLength());
        // Sorted by name (case-insensitive); files excluded.
        Assert.Equal("alpha", folders[0].GetProperty("name").GetString());
        Assert.Equal(Path.GetFullPath(subA), folders[0].GetProperty("path").GetString());
        Assert.Equal("Beta", folders[1].GetProperty("name").GetString());
        Assert.Equal(Path.GetFullPath(subB), folders[1].GetProperty("path").GetString());
        Assert.Equal(0, el.GetProperty("drives").GetArrayLength());
    }

    [Fact]
    public void BrowseFolders_NestedFolder_ParentIsImmediateParent()
    {
        string deep = Path.Combine(_workDir, "a", "b", "c");
        Directory.CreateDirectory(deep);

        var (status, payload) = DashboardApi.BrowseFolders(deep);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.Equal(Path.GetFullPath(Path.Combine(_workDir, "a", "b")),
            doc.RootElement.GetProperty("parent").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("folders").GetArrayLength());
    }

    [Fact]
    public void BrowseFolders_TrailingSeparator_IsNormalized()
    {
        string root = Path.Combine(_workDir, "browse2");
        Directory.CreateDirectory(root);

        var (status, payload) = DashboardApi.BrowseFolders(root + Path.DirectorySeparatorChar);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.Equal(Path.GetFullPath(root), doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void BrowseFolders_EmptyPath_ReturnsDrivesView()
    {
        var (status, payload) = DashboardApi.BrowseFolders("");
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        var el = doc.RootElement;
        Assert.Equal("", el.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, el.GetProperty("parent").ValueKind);
        Assert.Equal(0, el.GetProperty("folders").GetArrayLength());
        var drives = el.GetProperty("drives");
        Assert.True(drives.GetArrayLength() >= 1);
        foreach (var d in drives.EnumerateArray())
            Assert.True(Directory.Exists(d.GetProperty("path").GetString()));
        // Home is the picker's default start folder; it exists on any
        // machine with a user profile.
        Assert.False(string.IsNullOrEmpty(el.GetProperty("home").GetString()));

        var (nullStatus, _) = DashboardApi.BrowseFolders(null);
        Assert.Equal(200, nullStatus);
    }

    [Fact]
    public void BrowseFolders_DriveRoot_HasNoParent()
    {
        string root = Path.GetPathRoot(_workDir)!;
        var (status, payload) = DashboardApi.BrowseFolders(root);
        Assert.Equal(200, status);
        using var doc = ToJson(payload);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("parent").ValueKind);
        Assert.Equal(root, doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void BrowseFolders_MissingFolder_ReturnsCleanError()
    {
        string missing = Path.Combine(_workDir, "no-such-folder");
        var (status, payload) = DashboardApi.BrowseFolders(missing);
        Assert.Equal(400, status);
        using var doc = ToJson(payload);
        Assert.Contains("not found", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void BrowseFolders_FilePath_ReturnsCleanError()
    {
        string file = Path.Combine(_workDir, "a-file.txt");
        File.WriteAllText(file, "x");
        var (status, payload) = DashboardApi.BrowseFolders(file);
        Assert.Equal(400, status);
        using var doc = ToJson(payload);
        Assert.Contains("not a folder", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void BrowseFolders_AccessDenied_ReturnsCleanError()
    {
        if (OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("chmod-based access denial is Unix-only.");
        string locked = Path.Combine(_workDir, "locked");
        Directory.CreateDirectory(Path.Combine(locked, "child"));
        var prev = File.GetUnixFileMode(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            bool enforced;
            try { Directory.GetDirectories(locked); enforced = false; }
            catch (UnauthorizedAccessException) { enforced = true; }
            if (!enforced)
                throw Xunit.Sdk.SkipException.ForSkip(
                    "Filesystem does not enforce permissions for this user (e.g. running as root).");
            var (status, payload) = DashboardApi.BrowseFolders(locked);
            Assert.Equal(400, status);
            using var doc = ToJson(payload);
            Assert.Contains("Access denied", doc.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            File.SetUnixFileMode(locked, prev);
        }
    }
}
