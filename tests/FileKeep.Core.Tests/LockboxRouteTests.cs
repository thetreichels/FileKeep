using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;
using Xunit.Sdk;

namespace FileKeep.Core.Tests;

/// <summary>
/// Lockbox (.kfb) ROUTE tests: the five phase-2 routes in
/// Dashboard.cs, driven over real HTTP against the production server
/// (<see cref="Dashboard.RunAsync"/>) started in-process on a free
/// loopback port, with the CSRF token read from the served page —
/// the same through-Kestrel shape as
/// <see cref="OfflineDavKestrelTests"/> (probe + graceful skip where
/// loopback TCP is intercepted, i.e. this sandbox; runs for real in
/// the Windows suite). The route handlers are inline lambdas, so
/// there is no <c>MapLockboxRoutes</c> seam to call — starting the
/// real server is the only way to exercise the route layer itself:
/// CSRF, multipart parsing (file-part filenames are the in-Lockbox
/// paths, "options" is a JSON text field), JSON deserialization via
/// <c>DashboardJsonContext</c>, the missing-"options" default, and
/// the <c>Results.File</c> vs <c>JsonPayload</c> shaping.
/// The API functions underneath are covered by
/// <see cref="LockboxApiTests"/>; these tests cover only what the
/// routes add.
///
/// YubiKey note: <c>RunAsync</c> constructs its own
/// <see cref="SimulatedYubiKeyProvider"/> when
/// FILEKEEP_YUBIKEY_SIMULATE=1, and no dashboard route programs a
/// slot — so a YubiKey method through the route always fails with
/// the provider's "not programmed" 500 (asserted here). A successful
/// YubiKey / AND round-trip through the route is therefore not
/// exercisable here; it is covered at API level by
/// <see cref="LockboxApiTests.CreateFromFiles_YubiKeyOr_RoundTrips"/>
/// and <c>CreateFromFiles_And_PassphrasePlusYubiKey_RoundTrips</c>.
/// The AND route test below uses the same passphrase twice, which
/// proves the <c>requireAll</c> wiring and the split/recombine path
/// through the route.
/// </summary>
public sealed class LockboxRouteTests : IDisposable
{
    // Same value as LockboxApiTests: test classes that set the
    // passphrase env var run in parallel under dotnet test, so
    // sharing the value keeps a concurrent class from flipping the
    // secret mid-test (the repo's established ctor-set / Dispose-
    // restore pattern; the reflection runner runs sequentially).
    private const string Pass = "lockbox-test-passphrase";
    private const string LockPass = "lockbox-unlock-passphrase";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly string _shareDir;
    private readonly string? _savedPassphrase;
    private readonly string? _savedSimulate;

    public LockboxRouteTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "fk-lockbox-route-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        _shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_shareDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        _savedSimulate = Environment.GetEnvironmentVariable("FILEKEEP_YUBIKEY_SIMULATE");
        Environment.SetEnvironmentVariable("FILEKEEP_YUBIKEY_SIMULATE", "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        Environment.SetEnvironmentVariable("FILEKEEP_YUBIKEY_SIMULATE", _savedSimulate);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed record ServerCtx(string BaseUrl, string Csrf, HttpClient Client);

    // ----- server harness (OfflineDavKestrelTests shape) -----

    private ServiceConfig BuildConfig()
    {
        var config = new ServiceConfig
        {
            PassphraseProtected = "dpapi-blob",
            DashboardBind = "127.0.0.1",
            DashboardPort = FreePort(),
        };
        config.Locations.Add(new BackupLocation { Name = "loc1", Type = "smb", Share = _shareDir });
        // Far-future schedule + a 1h scheduler poll interval: no job
        // can fire while a test runs.
        config.Jobs.Add(new BackupJobConfig
        {
            Name = "docs", Repo = _repoDir, Sources = new List<string> { _srcDir },
            Schedule = "interval 1000000", Mode = "incremental", BackupPrivilege = false,
        });
        return config;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Starts the production dashboard, waits for the served
    /// page, reads the CSRF token from its meta tag, runs
    /// <paramref name="work"/>, then stops the server. Skips (does not
    /// fail) where loopback HTTP is intercepted.</summary>
    private async Task WithServerAsync(Func<ServerCtx, Task> work)
    {
        var config = BuildConfig();
        var scheduler = new BackupScheduler(config, pollInterval: TimeSpan.FromHours(1));
        string configPath = Path.Combine(_workDir, "service.json");
        using var cts = new CancellationTokenSource();
        Task serverTask = Dashboard.RunAsync(config, scheduler, configPath, cts.Token);
        try
        {
            string baseUrl = $"http://127.0.0.1:{config.DashboardPort}";
            string csrf = await WaitForServerAsync(baseUrl, serverTask);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
            var ctx = new ServerCtx(baseUrl, csrf, client);
            try
            {
                await work(ctx);
            }
            catch (Exception ex) when (ex is SocketException or HttpRequestException or TaskCanceledException)
            {
                throw SkipException.ForSkip("Loopback HTTP is intercepted in this environment: " + ex.GetType().Name);
            }
        }
        finally
        {
            cts.Cancel();
            // Best-effort shutdown: a hung host must not hang the runner.
            await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(10)));
        }
    }

    private static async Task<string> WaitForServerAsync(string baseUrl, Task serverTask)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (true)
        {
            if (serverTask.IsCompleted)
                Assert.Fail("Dashboard server exited during startup: " +
                    serverTask.Exception?.GetBaseException().Message);
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var resp = await probe.GetAsync(baseUrl + "/");
                if (resp.StatusCode != HttpStatusCode.OK)
                    Assert.Fail("Dashboard page returned " + (int)resp.StatusCode);
                string html = await resp.Content.ReadAsStringAsync();
                const string marker = "<meta name=\"csrf-token\" content=\"";
                int at = html.IndexOf(marker, StringComparison.Ordinal);
                Assert.True(at >= 0, "Served dashboard page has no csrf-token meta tag.");
                int start = at + marker.Length;
                int end = html.IndexOf('"', start);
                Assert.True(end > start, "csrf-token meta tag is empty.");
                return html[start..end];
            }
            catch (Exception ex) when (ex is SocketException or HttpRequestException or TaskCanceledException)
            {
                // Not listening yet: retry. Anything else (reset,
                // blackhole timeout) means loopback is intercepted:
                // skip at once instead of burning the deadline.
                if (IsConnectionRefused(ex) && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(250);
                    continue;
                }
                throw SkipException.ForSkip("Loopback HTTP is not reachable in this environment: " + ex.GetType().Name);
            }
        }
    }

    private static bool IsConnectionRefused(Exception ex)
    {
        Exception? inner = ex is HttpRequestException hre ? hre.InnerException : ex;
        return inner is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused;
    }

    // ----- request / response helpers -----

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> PostJsonAsync(ServerCtx ctx, string path, object body) =>
        ctx.Client.PostAsync(ctx.BaseUrl + path,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    private static async Task<string> ErrorTextAsync(HttpResponseMessage resp)
    {
        string body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").GetString() ?? "";
    }

    private static async Task<byte[]> AssertDownloadAsync(HttpResponseMessage resp, string expectedFileName)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/octet-stream", resp.Content.Headers.ContentType?.MediaType);
        string? cd = resp.Content.Headers.ContentDisposition?.FileNameStar
            ?? resp.Content.Headers.ContentDisposition?.FileName;
        Assert.Equal(expectedFileName, (cd ?? "").Trim('"'));
        return await resp.Content.ReadAsByteArrayAsync();
    }

    private static List<(string Path, byte[] Plaintext)> OpenedFiles(
        byte[] bundleBytes, DashboardApi.LockboxUnlockDto unlock)
    {
        var (header, files, error) = DashboardApi.OpenLockbox(bundleBytes, unlock, null);
        Assert.Null(error);
        Assert.NotNull(header);
        Assert.NotNull(files);
        return files!;
    }

    private static void ZeroFiles(List<(string Path, byte[] Plaintext)> files)
    {
        foreach (var (_, plaintext) in files)
            CryptographicOperations.ZeroMemory(plaintext);
    }

    private static object PassphraseMethod(string passphrase) => new { type = "passphrase", passphrase };

    private static object YubiKeyMethod(int slot = 2) => new { type = "yubikey-hmac-sha1", slot };

    private static string CreateOptionsJson(object[] methods, bool requireAll, string? name = null) =>
        JsonSerializer.Serialize(new { methods, requireAll, name });

    private static MultipartFormDataContent CreateLockboxForm(
        (string Path, byte[] Bytes)[] files, string optionsJson)
    {
        var form = new MultipartFormDataContent();
        foreach (var (path, bytes) in files)
            form.Add(new ByteArrayContent(bytes), "files", path);
        form.Add(JsonContent(optionsJson), "options");
        return form;
    }

    private static async Task<byte[]> CreateLockboxViaRouteAsync(
        ServerCtx ctx, (string Path, byte[] Bytes)[] files, string name = "route-vault")
    {
        var form = CreateLockboxForm(files,
            CreateOptionsJson(new object[] { PassphraseMethod(LockPass) }, requireAll: false, name));
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox", form);
        return await AssertDownloadAsync(resp, name + ".kfb");
    }

    private static async Task UnlockCardAsync(ServerCtx ctx)
    {
        using var resp = await PostJsonAsync(ctx, "/api/offline-folders/unlock", new { proofPassphrase = Pass });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static async Task UnlockRestoreAsync(ServerCtx ctx)
    {
        using var resp = await PostJsonAsync(ctx, "/api/restore/unlock-passphrase", new { passphrase = Pass });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>Unlocks the card, creates folder "Vault" on loc1,
    /// stores a.txt via the batch route and sub/b.txt via DAV PUT
    /// (the batch route takes basenames only).</summary>
    private static async Task SetupVaultAsync(ServerCtx ctx)
    {
        await UnlockCardAsync(ctx);
        using var create = await PostJsonAsync(ctx, "/api/offline-folders",
            new { name = "Vault", locationId = "loc1" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var batch = new MultipartFormDataContent();
        batch.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("folder a")), "files", "a.txt");
        using var stored = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/offline-folders/Vault/files", batch);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        using var doc = JsonDocument.Parse(await stored.Content.ReadAsStringAsync());
        var storedPaths = doc.RootElement.GetProperty("stored").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains("a.txt", storedPaths);
        using var dav = await ctx.Client.PutAsync(ctx.BaseUrl + "/dav/offline/Vault/sub/b.txt",
            new ByteArrayContent(Encoding.UTF8.GetBytes("folder b")));
        Assert.True(dav.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent,
            "DAV PUT status: " + dav.StatusCode);
    }

    private static async Task<List<string>> FolderFilePathsAsync(ServerCtx ctx, string folder)
    {
        using var resp = await ctx.Client.GetAsync(ctx.BaseUrl + $"/api/offline-folders/{folder}/files");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()!).ToList();
    }

    private string MakeBackupWithFiles()
    {
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "backup file a");
        Directory.CreateDirectory(Path.Combine(_srcDir, "sub"));
        File.WriteAllText(Path.Combine(_srcDir, "sub", "b.txt"), "backup file b");
        BackupRepository.Init(_repoDir, Pass, 64 * 1024, 10_000).Dispose();
        using var repo = BackupRepository.Open(_repoDir, Pass);
        return repo.BackupDirectory(_srcDir).BackupId;
    }

    private static string[] LockboxTempFiles() =>
        Directory.EnumerateFiles(Path.GetTempPath(), "fk-lockbox-*.kfb").ToArray();

    // ----- 1. create-lockbox (multipart) -----

    [Fact]
    public Task CreateLockbox_Passphrase_RoundTrips() => WithServerAsync(async ctx =>
    {
        var form = CreateLockboxForm(
            new[] { ("a.txt", Encoding.UTF8.GetBytes("hello")), ("sub/b.txt", Encoding.UTF8.GetBytes("world")) },
            CreateOptionsJson(new object[] { PassphraseMethod(LockPass) }, requireAll: false, "route-vault"));
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox", form);
        byte[] kfb = await AssertDownloadAsync(resp, "route-vault.kfb");
        var opened = OpenedFiles(kfb, new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        Assert.Equal(2, opened.Count);
        Assert.Equal("hello", Encoding.UTF8.GetString(opened.First(f => f.Path == "a.txt").Plaintext));
        Assert.Equal("world", Encoding.UTF8.GetString(opened.First(f => f.Path == "sub/b.txt").Plaintext));
        ZeroFiles(opened);
    });

    [Fact]
    public Task CreateLockbox_OrTwoPassphrases_EitherUnlocks() => WithServerAsync(async ctx =>
    {
        var form = CreateLockboxForm(
            new[] { ("a.txt", Encoding.UTF8.GetBytes("or")) },
            CreateOptionsJson(new object[]
                { PassphraseMethod("first-pass"), PassphraseMethod("second-pass") }, requireAll: false));
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox", form);
        byte[] kfb = await AssertDownloadAsync(resp, "lockbox.kfb");
        var byFirst = OpenedFiles(kfb, new DashboardApi.LockboxUnlockDto { Passphrase = "first-pass" });
        Assert.Single(byFirst);
        ZeroFiles(byFirst);
        var bySecond = OpenedFiles(kfb, new DashboardApi.LockboxUnlockDto { Passphrase = "second-pass" });
        Assert.Single(bySecond);
        ZeroFiles(bySecond);
    });

    [Fact]
    public Task CreateLockbox_AndSamePassphraseTwice_NeedsThatPassphrase() => WithServerAsync(async ctx =>
    {
        var form = CreateLockboxForm(
            new[] { ("a.txt", Encoding.UTF8.GetBytes("and")) },
            CreateOptionsJson(new object[]
                { PassphraseMethod(LockPass), PassphraseMethod(LockPass) }, requireAll: true));
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox", form);
        byte[] kfb = await AssertDownloadAsync(resp, "lockbox.kfb");
        // The AND group (same passphrase twice) opens with that passphrase...
        var opened = OpenedFiles(kfb, new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        Assert.Single(opened);
        Assert.Equal("and", Encoding.UTF8.GetString(opened[0].Plaintext));
        ZeroFiles(opened);
        // ...and with no other passphrase.
        var (_, _, error) = DashboardApi.OpenLockbox(kfb,
            new DashboardApi.LockboxUnlockDto { Passphrase = "wrong-passphrase" });
        Assert.NotNull(error);
    });

    [Fact]
    public Task CreateLockbox_YubiKey_UnprogrammedSimulatedSlot_Returns500() => WithServerAsync(async ctx =>
    {
        // Single YubiKey method: the route reaches the provider, whose
        // slot 2 was never programmed (no route programs it).
        var form = CreateLockboxForm(
            new[] { ("a.txt", Encoding.UTF8.GetBytes("yk")) },
            CreateOptionsJson(new object[] { YubiKeyMethod() }, requireAll: false));
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox", form);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Contains("not programmed", await ErrorTextAsync(resp), StringComparison.OrdinalIgnoreCase);
        // AND (passphrase + YubiKey) fails at the same provider step,
        // not at validation: requireAll with 2 methods is valid.
        var andForm = CreateLockboxForm(
            new[] { ("a.txt", Encoding.UTF8.GetBytes("yk")) },
            CreateOptionsJson(new object[] { PassphraseMethod(LockPass), YubiKeyMethod() }, requireAll: true));
        using var andResp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox", andForm);
        Assert.Equal(HttpStatusCode.InternalServerError, andResp.StatusCode);
        Assert.Contains("not programmed", await ErrorTextAsync(andResp), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task CreateLockbox_ValidationErrors() => WithServerAsync(async ctx =>
    {
        var oneFile = new[] { ("a.txt", Encoding.UTF8.GetBytes("x")) };
        async Task<(HttpStatusCode Status, string Error)> PostAsync(string optionsJson, bool withFiles = true)
        {
            var form = withFiles
                ? CreateLockboxForm(oneFile, optionsJson)
                : CreateLockboxForm(Array.Empty<(string, byte[])>(), optionsJson);
            using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox", form);
            return (resp.StatusCode, await ErrorTextAsync(resp));
        }

        // No methods.
        var (status, error) = await PostAsync(
            CreateOptionsJson(Array.Empty<object>(), requireAll: false));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("At least one unlock method", error, StringComparison.Ordinal);
        // Unknown method type.
        (status, error) = await PostAsync(
            CreateOptionsJson(new object[] { new { type = "faceid" } }, requireAll: false));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Unsupported unlock method", error, StringComparison.Ordinal);
        // Passphrase method without a passphrase.
        (status, error) = await PostAsync(
            CreateOptionsJson(new object[] { new { type = "passphrase" } }, requireAll: false));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Passphrase is required", error, StringComparison.Ordinal);
        // AND requires 2 methods.
        (status, error) = await PostAsync(
            CreateOptionsJson(new object[] { PassphraseMethod(LockPass) }, requireAll: true));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("AND requires at least 2 methods", error, StringComparison.Ordinal);
        // No files.
        (status, error) = await PostAsync(
            CreateOptionsJson(new object[] { PassphraseMethod(LockPass) }, requireAll: false), withFiles: false);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("At least one file is required", error, StringComparison.Ordinal);
    });

    [Fact]
    public Task CreateLockbox_MissingOptions_DefaultsToEmpty_400() => WithServerAsync(async ctx =>
    {
        // Files but no "options" field: the route defaults to an empty
        // DTO, so core validation returns the 400.
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("x")), "files", "a.txt");
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("At least one unlock method", await ErrorTextAsync(resp), StringComparison.Ordinal);
    });

    [Fact]
    public Task CreateLockbox_NotMultipart_Returns400() => WithServerAsync(async ctx =>
    {
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox",
            JsonContent("{\"methods\":[]}"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Expected multipart form", await ErrorTextAsync(resp), StringComparison.Ordinal);
    });

    [Fact]
    public Task CreateLockbox_MissingCsrf_Returns403() => WithServerAsync(async ctx =>
    {
        using var bare = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var form = CreateLockboxForm(
            new[] { ("a.txt", Encoding.UTF8.GetBytes("x")) },
            CreateOptionsJson(new object[] { PassphraseMethod(LockPass) }, requireAll: false));
        using var resp = await bare.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox", form);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    });

    // ----- 2. create-lockbox-from-backup (JSON) -----

    [Fact]
    public Task CreateFromBackup_RoundTrips() => WithServerAsync(async ctx =>
    {
        string backupId = MakeBackupWithFiles();
        await UnlockRestoreAsync(ctx);
        using var resp = await PostJsonAsync(ctx, "/api/operations/create-lockbox-from-backup", new
        {
            repo = _repoDir,
            backupId,
            methods = new object[] { PassphraseMethod(LockPass) },
            requireAll = false,
            name = "docs-backup",
        });
        byte[] kfb = await AssertDownloadAsync(resp, "docs-backup.kfb");
        var opened = OpenedFiles(kfb, new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        Assert.Equal(2, opened.Count);
        Assert.Equal("backup file a", Encoding.UTF8.GetString(opened.First(f => f.Path == "a.txt").Plaintext));
        Assert.Equal("backup file b", Encoding.UTF8.GetString(opened.First(f => f.Path == "sub/b.txt").Plaintext));
        ZeroFiles(opened);
    });

    [Fact]
    public Task CreateFromBackup_LockedRestoreSession_Returns401() => WithServerAsync(async ctx =>
    {
        // No restore unlock: ExportBundle's passphrase gate fires first.
        using var resp = await PostJsonAsync(ctx, "/api/operations/create-lockbox-from-backup", new
        {
            repo = _repoDir,
            backupId = "whatever",
            methods = new object[] { PassphraseMethod(LockPass) },
            requireAll = false,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("locked", await ErrorTextAsync(resp), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task CreateFromBackup_UnknownBackup_Returns500() => WithServerAsync(async ctx =>
    {
        _ = MakeBackupWithFiles();
        await UnlockRestoreAsync(ctx);
        using var resp = await PostJsonAsync(ctx, "/api/operations/create-lockbox-from-backup", new
        {
            repo = _repoDir,
            backupId = "no-such-backup",
            methods = new object[] { PassphraseMethod(LockPass) },
            requireAll = false,
        });
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Contains("not found", await ErrorTextAsync(resp), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task CreateFromBackup_UnknownRepo_Returns400() => WithServerAsync(async ctx =>
    {
        using var resp = await PostJsonAsync(ctx, "/api/operations/create-lockbox-from-backup", new
        {
            repo = Path.Combine(_workDir, "no-such-repo"),
            backupId = "whatever",
            methods = new object[] { PassphraseMethod(LockPass) },
            requireAll = false,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Unknown repo", await ErrorTextAsync(resp), StringComparison.Ordinal);
    });

    [Fact]
    public Task CreateFromBackup_MissingCsrf_Returns403() => WithServerAsync(async ctx =>
    {
        using var bare = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var resp = await bare.PostAsync(ctx.BaseUrl + "/api/operations/create-lockbox-from-backup",
            JsonContent("{\"repo\":\"x\",\"backupId\":\"y\",\"methods\":[]}"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    });

    // ----- 3. folder-to-lockbox (JSON) -----

    [Fact]
    public Task FolderToLockbox_FolderScope_RoundTrips() => WithServerAsync(async ctx =>
    {
        await SetupVaultAsync(ctx);
        using var resp = await PostJsonAsync(ctx, "/api/operations/folder-to-lockbox", new
        {
            folderName = "Vault",
            scope = "folder",
            methods = new object[] { PassphraseMethod(LockPass) },
            requireAll = false,
        });
        byte[] kfb = await AssertDownloadAsync(resp, "Vault.kfb");
        var opened = OpenedFiles(kfb, new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        Assert.Equal(2, opened.Count);
        Assert.Equal("folder a", Encoding.UTF8.GetString(opened.First(f => f.Path == "a.txt").Plaintext));
        Assert.Equal("folder b", Encoding.UTF8.GetString(opened.First(f => f.Path == "sub/b.txt").Plaintext));
        ZeroFiles(opened);
    });

    [Fact]
    public Task FolderToLockbox_FileScope_OnlyThatFile() => WithServerAsync(async ctx =>
    {
        await SetupVaultAsync(ctx);
        using var resp = await PostJsonAsync(ctx, "/api/operations/folder-to-lockbox", new
        {
            folderName = "Vault",
            scope = "file",
            filePath = "sub/b.txt",
            methods = new object[] { PassphraseMethod(LockPass) },
            requireAll = false,
        });
        byte[] kfb = await AssertDownloadAsync(resp, "Vault-b.txt.kfb");
        var opened = OpenedFiles(kfb, new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        var only = Assert.Single(opened);
        Assert.Equal("sub/b.txt", only.Path);
        Assert.Equal("folder b", Encoding.UTF8.GetString(only.Plaintext));
        ZeroFiles(opened);
    });

    [Fact]
    public Task FolderToLockbox_Errors() => WithServerAsync(async ctx =>
    {
        await SetupVaultAsync(ctx);
        var methods = new object[] { PassphraseMethod(LockPass) };
        async Task<(HttpStatusCode Status, string Error)> PostAsync(object body)
        {
            using var resp = await PostJsonAsync(ctx, "/api/operations/folder-to-lockbox", body);
            return (resp.StatusCode, await ErrorTextAsync(resp));
        }

        // Unknown folder.
        var (status, error) = await PostAsync(new { folderName = "Nope", scope = "folder", methods, requireAll = false });
        Assert.Equal(HttpStatusCode.NotFound, status);
        // Bad scope.
        (status, error) = await PostAsync(new { folderName = "Vault", scope = "everything", methods, requireAll = false });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        // File scope without a filePath.
        (status, error) = await PostAsync(new { folderName = "Vault", scope = "file", methods, requireAll = false });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        // File scope naming a file that is not in the folder.
        (status, error) = await PostAsync(new { folderName = "Vault", scope = "file", filePath = "ghost.txt", methods, requireAll = false });
        Assert.Equal(HttpStatusCode.NotFound, status);
        // Empty folder.
        using var empty = await PostJsonAsync(ctx, "/api/offline-folders",
            new { name = "Empty", locationId = "loc1" });
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        (status, error) = await PostAsync(new { folderName = "Empty", scope = "folder", methods, requireAll = false });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("no files", error, StringComparison.OrdinalIgnoreCase);
        // Locked card: lock it, then the same call is refused.
        using var lockResp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/offline-folders/lock",
            JsonContent("{}"));
        Assert.Equal(HttpStatusCode.OK, lockResp.StatusCode);
        (status, error) = await PostAsync(new { folderName = "Vault", scope = "folder", methods, requireAll = false });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("locked", error, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task FolderToLockbox_SendViaRelay_Unconfigured_Returns400_AndDeletesTemp() => WithServerAsync(async ctx =>
    {
        await SetupVaultAsync(ctx);
        string[] before = LockboxTempFiles();
        using var resp = await PostJsonAsync(ctx, "/api/operations/folder-to-lockbox", new
        {
            folderName = "Vault",
            scope = "folder",
            methods = new object[] { PassphraseMethod(LockPass) },
            requireAll = false,
            sendViaRelay = true,
            name = "vault-drop",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Relay is not configured", await ErrorTextAsync(resp), StringComparison.Ordinal);
        // The staged temp .kfb is deleted even though beaming failed.
        Assert.Equal(before.Length, LockboxTempFiles().Length);
        Assert.Empty(LockboxTempFiles().Except(before));
    });

    [Fact]
    public Task FolderToLockbox_MissingCsrf_Returns403() => WithServerAsync(async ctx =>
    {
        using var bare = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var resp = await bare.PostAsync(ctx.BaseUrl + "/api/operations/folder-to-lockbox",
            JsonContent("{\"folderName\":\"Vault\",\"methods\":[]}"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    });

    // ----- 4. lockbox-to-folder (multipart) -----

    [Fact]
    public Task LockboxToFolder_NewFolder_RoundTrips() => WithServerAsync(async ctx =>
    {
        byte[] kfb = await CreateLockboxViaRouteAsync(ctx,
            new[]
            {
                ("hello.txt", Encoding.UTF8.GetBytes("imported hello")),
                ("sub/world.txt", Encoding.UTF8.GetBytes("imported world")),
            });
        await UnlockCardAsync(ctx);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(kfb), "lockbox", "import.kfb");
        form.Add(JsonContent(JsonSerializer.Serialize(new
        {
            folderName = "Imported",
            location = "loc1",
            unlock = new { passphrase = LockPass, useYubiKey = false },
        })), "options");
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/lockbox-to-folder", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("Imported", root.GetProperty("folderName").GetString());
        Assert.Equal(2, root.GetProperty("fileCount").GetInt32());
        Assert.False(root.GetProperty("merged").GetBoolean());
        var paths = await FolderFilePathsAsync(ctx, "Imported");
        Assert.Equal(2, paths.Count);
        Assert.Contains("hello.txt", paths);
        Assert.Contains("sub/world.txt", paths);
    });

    [Fact]
    public Task LockboxToFolder_Merge_AppendsNewPaths() => WithServerAsync(async ctx =>
    {
        await SetupVaultAsync(ctx);
        byte[] kfb = await CreateLockboxViaRouteAsync(ctx,
            new[] { ("new.txt", Encoding.UTF8.GetBytes("fresh")) }, "merge-drop");
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(kfb), "lockbox", "merge-drop.kfb");
        form.Add(JsonContent(JsonSerializer.Serialize(new
        {
            folderName = "Vault",
            location = "loc1",
            unlock = new { passphrase = LockPass, useYubiKey = false },
        })), "options");
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/lockbox-to-folder", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("merged").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("fileCount").GetInt32());
        var paths = await FolderFilePathsAsync(ctx, "Vault");
        Assert.Equal(3, paths.Count);
        Assert.Contains("a.txt", paths);
        Assert.Contains("sub/b.txt", paths);
        Assert.Contains("new.txt", paths);
    });

    [Fact]
    public Task LockboxToFolder_SamePathConflict_Refused400_NothingImported() => WithServerAsync(async ctx =>
    {
        await SetupVaultAsync(ctx);
        byte[] kfb = await CreateLockboxViaRouteAsync(ctx,
            new[]
            {
                ("a.txt", Encoding.UTF8.GetBytes("replacement")),
                ("b.txt", Encoding.UTF8.GetBytes("innocent")),
            }, "conflict-drop");
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(kfb), "lockbox", "conflict-drop.kfb");
        form.Add(JsonContent(JsonSerializer.Serialize(new
        {
            folderName = "Vault",
            location = "loc1",
            unlock = new { passphrase = LockPass, useYubiKey = false },
        })), "options");
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/lockbox-to-folder", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string error = await ErrorTextAsync(resp);
        Assert.Contains("Already in folder 'Vault'", error, StringComparison.Ordinal);
        Assert.Contains("a.txt", error, StringComparison.Ordinal);
        // Nothing imported — not even the non-conflicting file.
        var paths = await FolderFilePathsAsync(ctx, "Vault");
        Assert.Equal(2, paths.Count);
        Assert.Contains("a.txt", paths);
        Assert.Contains("sub/b.txt", paths);
        Assert.DoesNotContain("b.txt", paths);
    });

    [Fact]
    public Task LockboxToFolder_WrongPassphrase_Returns400() => WithServerAsync(async ctx =>
    {
        byte[] kfb = await CreateLockboxViaRouteAsync(ctx,
            new[] { ("a.txt", Encoding.UTF8.GetBytes("x")) });
        await UnlockCardAsync(ctx);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(kfb), "lockbox", "import.kfb");
        form.Add(JsonContent(JsonSerializer.Serialize(new
        {
            folderName = "Imported",
            location = "loc1",
            unlock = new { passphrase = "wrong", useYubiKey = false },
        })), "options");
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/lockbox-to-folder", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Could not unlock", await ErrorTextAsync(resp), StringComparison.Ordinal);
        using var list = await ctx.Client.GetAsync(ctx.BaseUrl + "/api/offline-folders/Imported/files");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
    });

    [Fact]
    public Task LockboxToFolder_MissingLockboxPart_Returns400() => WithServerAsync(async ctx =>
    {
        var form = new MultipartFormDataContent();
        form.Add(JsonContent(JsonSerializer.Serialize(new
        {
            folderName = "Imported",
            location = "loc1",
            unlock = new { passphrase = LockPass, useYubiKey = false },
        })), "options");
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/lockbox-to-folder", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("No Lockbox file was uploaded", await ErrorTextAsync(resp), StringComparison.Ordinal);
    });

    [Fact]
    public Task LockboxToFolder_MissingOptions_Returns400() => WithServerAsync(async ctx =>
    {
        byte[] kfb = await CreateLockboxViaRouteAsync(ctx,
            new[] { ("a.txt", Encoding.UTF8.GetBytes("x")) });
        // Lockbox part but no "options" field: the route defaults to
        // an empty DTO, whose blank folder name fails validation.
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(kfb), "lockbox", "import.kfb");
        using var resp = await ctx.Client.PostAsync(ctx.BaseUrl + "/api/operations/lockbox-to-folder", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Enter a folder name", await ErrorTextAsync(resp), StringComparison.Ordinal);
    });

    // ----- 5. received Lockboxes -----

    [Fact]
    public Task ReceivedLockboxes_Unconfigured_Returns400() => WithServerAsync(async ctx =>
    {
        using var resp = await ctx.Client.GetAsync(ctx.BaseUrl + "/api/lockboxes/received");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Relay is not configured", await ErrorTextAsync(resp), StringComparison.Ordinal);
    });
}
