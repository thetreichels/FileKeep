using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsenetBackup.Core;
using UsenetBackup.Core.Json;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Security;
using UsenetBackup.Core.Service;

namespace UsenetBackup.Service;

/// <summary>
/// Localhost web dashboard (the v0.7 GUI): service status, manual job
/// triggers, backup lists and the operations log. Binds to loopback by
/// default; it has no authentication, so it must not be exposed to a
/// network without a reverse proxy in front. Backup triggering never
/// exposes the passphrase — it comes from the service's own environment.
/// </summary>
public static class Dashboard
{
    /// <summary>
    /// Safely reads a string property from a JSON body. Missing properties
    /// yield "" (which downstream validation rejects with 400) instead of
    /// throwing KeyNotFoundException (which becomes a 500).
    /// </summary>
    private static string GetBodyString(System.Text.Json.JsonElement body, string name) =>
        body.TryGetProperty(name, out var el) ? el.GetString() ?? "" : "";

    /// <summary>Reads the raw request body as a string for source-generated JSON deserialization.</summary>
    private static async Task<string> ReadBodyStringAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Reads the optional security-proof fields (<c>proofPassphrase</c> /
    /// <c>proofYubiKey</c>) from a JSON request body, for routes whose
    /// normal shape carries no body or a body parsed elsewhere (phone
    /// setup, program-spare). An absent or malformed body simply means
    /// "no proof supplied" — the DashboardApi gate then decides whether
    /// that is acceptable (first-time setup) or a refusal.
    /// </summary>
    private static async Task<(string? ProofPassphrase, bool ProofYubiKey)>
        ReadProofFieldsAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0)
            return (null, false);
        try
        {
            string raw = await ReadBodyStringAsync(request);
            if (string.IsNullOrWhiteSpace(raw))
                return (null, false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? pass = root.TryGetProperty("proofPassphrase", out var pp)
                ? pp.GetString() : null;
            bool yubiKey = root.TryGetProperty("proofYubiKey", out var py) &&
                           py.ValueKind == JsonValueKind.True;
            return (pass, yubiKey);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>Extracts the security-proof fields from an already-parsed JSON body.</summary>
    private static (string? ProofPassphrase, bool ProofYubiKey) ProofFieldsOf(JsonElement body) =>
        (body.TryGetProperty("proofPassphrase", out var pp) ? pp.GetString() : null,
         body.TryGetProperty("proofYubiKey", out var py) && py.ValueKind == JsonValueKind.True);

    /// <summary>
    /// True only for loopback bind values: "localhost" (any case), any
    /// 127.x.x.x literal, ::1 (bracketed or not). Classification is purely
    /// textual — no DNS resolution — so a hostname that merely resolves to
    /// loopback does not qualify, and neither do wildcards (0.0.0.0, ::, *)
    /// or LAN addresses.
    /// </summary>
    public static bool IsLoopbackBind(string? bind)
    {
        string value = (bind ?? "").Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
            value = value[1..^1];
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// The dashboard has no authentication, so a non-loopback bind exposes
    /// a LocalSystem-privilege API to the network (finding D-1). Refuse
    /// loudly at startup — the same presentation as an unparseable bind
    /// (Kestrel throws and SF-6's supervisor takes the service down rather
    /// than run half a service) — instead of silently serving. This lives
    /// here, not in ServiceConfig.Validate, so the config still loads for
    /// the CLI and other non-dashboard paths.
    /// </summary>
    internal static void RequireLoopbackBind(string? bind)
    {
        if (!IsLoopbackBind(bind))
            throw new InvalidOperationException(
                $"dashboardBind '{bind}' is not a loopback address. The dashboard has no " +
                "authentication and must not be exposed to a network — set dashboardBind " +
                "to 127.0.0.1, ::1, or localhost, or remove the setting.");
    }

    public static async Task RunAsync(ServiceConfig config, BackupScheduler scheduler,
        string configPath, CancellationToken ct, Action<string>? log = null)
    {
        // D-1: never bind a non-loopback dashboard (see RequireLoopbackBind).
        RequireLoopbackBind(config.DashboardBind);

        // Per-startup CSRF token for state-changing endpoints. The dashboard
        // has no login, so without this any website you visit could trigger
        // backups via cross-origin POSTs to loopback. The token is embedded
        // in the served HTML (unreadable cross-origin) and never logged.
        string csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        log?.Invoke("dashboard CSRF token generated (embedded in served pages)");

        // YubiKey integration: the provider talks to the key over HID;
        // the restore session holds the touch-unwrapped passphrase in memory.
        // FILEKEEP_YUBIKEY_SIMULATE=1 selects the software emulation (tests).
        var restoreSession = new RestoreSession();
        IYubiKeyProvider? yubiKeyProvider =
            Environment.GetEnvironmentVariable("FILEKEEP_YUBIKEY_SIMULATE") == "1"
                ? new SimulatedYubiKeyProvider()
                : OperatingSystem.IsWindows() ? new YubicoSdkYubiKeyProvider() : null;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{config.DashboardBind}:{config.DashboardPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Use source-generated JSON metadata so Results.Json() works under trimming.
        // DashboardJsonContext covers dashboard DTOs; FileKeepJsonContext covers Core types.
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Add(DashboardJsonContext.Default);
            o.SerializerOptions.TypeInfoResolverChain.Add(UsenetBackup.Core.Json.FileKeepJsonContext.Default);
        });
        var app = builder.Build();

        // CX-3: a malformed request body used to escape ~15 operations
        // routes as a raw JsonException → bare 500 with an empty body,
        // and the page showed an empty error message. Body-parse
        // failures are client errors: translate them into the standard
        // 400 ErrorDto shape, once, for every /api route.
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex) when (
                (ex is JsonException || ex is BadHttpRequestException) &&
                ctx.Request.Path.StartsWithSegments("/api"))
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await Results.Json(
                        new ErrorDto("The request body was not valid JSON."),
                        statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(ctx);
                }
            }
        });

        app.MapGet("/", (HttpContext ctx) =>
        {
            // The page must never come from a browser/WebView2 cache: a cached
            // page keeps running the old build's code against a newer service
            // after an upgrade (the stale-page trap). no-store makes every
            // load and reload fetch the page the running service serves.
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Content(DashboardHtml.Page(csrfToken), "text/html; charset=utf-8");
        });

        app.MapGet("/api/status", () => Results.Json(DashboardApi.GetStatus(scheduler)));

        app.MapGet("/api/version", () =>
        {
            // Note: Results.Json with anonymous types breaks under trimmer in
            // single-file deployment (returns empty). Build JSON manually.
            try
            {
                string v = DashboardApi.GetAppVersion().Replace("\"", "");
                // "build" is the full token (git SHA included) the served page
                // compares against its baked-in copy to detect that the app
                // was upgraded while the window stayed open.
                string b = DashboardApi.GetBuildToken().Replace("\"", "");
                return Results.Content("{\"version\":\"" + v + "\",\"build\":\"" + b + "\"}", "application/json");
            }
            catch { return Results.Content("{\"version\":\"0.0.0\",\"build\":\"\"}", "application/json"); }
        });

        // Update check: compares the installed version against the VERSION
        // file committed and pushed to GitHub main, and reports any of the
        // latest version's requirements (REQUIREMENTS.json) this machine
        // does not meet.
        app.MapPost("/api/update-check", async (HttpRequest request) =>
        {
            // Security fix (A-4/D-7): this was the one POST route with
            // no CSRF check, so any website could make the service call
            // out to GitHub via a simple cross-site form POST. Same
            // check as every other state-changing route.
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            // Manual JSON: Results.Json with anonymous types breaks under trimmer.
            static string Jq(string? s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            string current = DashboardApi.GetAppVersion();
            var (ok, latest, error, unmet) = await DashboardApi.CheckForUpdatesAsync();
            if (!ok)
                return Results.Content("{\"current\":" + Jq(current) + ",\"error\":" + Jq(error) + "}", "application/json");
            string unmetJson = "[" + string.Join(",", (unmet ?? Array.Empty<string>()).Select(Jq)) + "]";
            return Results.Content("{\"current\":" + Jq(current) +
                ",\"latest\":" + Jq(latest) +
                ",\"updateAvailable\":" + (DashboardApi.IsNewerVersion(current, latest!) ? "true" : "false") +
                ",\"unmetRequirements\":" + unmetJson + "}", "application/json");
        });

        // Windows known user folders (Documents, Pictures, etc.) for the GUI's
        // quick-pick. Uses Environment.GetFolderPath (SHGetKnownFolderPath wrapper).
        app.MapGet("/api/user-folders", () =>
        {
            var folders = new List<FolderDto>();
            var known = new (string name, Environment.SpecialFolder folder)[]
            {
                ("Desktop", Environment.SpecialFolder.Desktop),
                ("Documents", Environment.SpecialFolder.MyDocuments),
                ("Pictures", Environment.SpecialFolder.MyPictures),
                ("Music", Environment.SpecialFolder.MyMusic),
                ("Videos", Environment.SpecialFolder.MyVideos),
                ("Downloads", Environment.SpecialFolder.UserProfile),
            };
            foreach (var (name, sf) in known)
            {
                string path = "";
                try { path = Environment.GetFolderPath(sf); } catch { }
                if (name == "Downloads" && !string.IsNullOrEmpty(path))
                    path = Path.Combine(path, "Downloads");
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    folders.Add(new FolderDto(name, path));
            }
            return Results.Json(folders);
        });

        // Server-side folder listing for the dashboard's folder picker —
        // the browser path when the GUI's native folder dialog is absent.
        // Read-only GET like /api/user-folders, so no CSRF token. An empty
        // path returns the drives view; errors are ErrorDto payloads the
        // picker displays inline.
        app.MapGet("/api/browse-folders", (string? path) =>
        {
            var (status, payload) = DashboardApi.BrowseFolders(path);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/jobs/{name}/run", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!DashboardApi.IsKnownJob(scheduler, name))
                return Results.NotFound(new ErrorDto($"Unknown job '{name}'." ));
            // Run in the background; the scheduler serializes runs.
            Task.Run(() =>
            {
                try { scheduler.RunJobNow(name); }
                catch (Exception ex)
                {
                    log?.Invoke($"dashboard: manual run of job '{name}' FAILED: {ex.Message}");
                }
            });
            return Results.Accepted();
        });

        // One-time manual backup to a local folder chosen at run time.
        // This is the only supported path to a local-only backup: no
        // scheduled job is created, and the repo is initialized if needed.
        app.MapPost("/api/backup/once", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            var sources = new List<string>();
            if (body.TryGetProperty("sources", out var srcEl) && srcEl.ValueKind == JsonValueKind.Array)
                foreach (var el in srcEl.EnumerateArray())
                    if (el.GetString() is string s && !string.IsNullOrWhiteSpace(s))
                        sources.Add(s);
            string repoPath = GetBodyString(body, "repoPath");
            var (status, payload) = DashboardApi.StartOneTimeBackup(scheduler, sources, repoPath, config);
            return Results.Json(payload, statusCode: status);
        });

        // Repo-scoped endpoints only accept repos belonging to configured
        // jobs, so the dashboard cannot be pointed at arbitrary paths.
        app.MapGet("/api/backups", (string repo) =>
        {
            var (status, payload) = DashboardApi.GetBackups(config, repo, restoreSession);
            return Results.Json(payload, statusCode: status);
        });

        app.MapGet("/api/log", (string repo, int lines = 100) =>
        {
            var (status, payload) = DashboardApi.GetLog(config, repo, lines);
            return Results.Json(payload, statusCode: status);
        });

        // Operations: manual actions for every CLI capability, run in the
        // background like job runs. The UI polls /api/log and /api/status.
        app.MapPost("/api/operations/upload", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = GetBodyString(body, "repo");
            string backupId = GetBodyString(body, "backupId");
            var (status, payload) = DashboardApi.StartUpload(config, scheduler, repo, backupId);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/verify", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = GetBodyString(body, "repo");
            string backupId = GetBodyString(body, "backupId");
            var (status, payload) = DashboardApi.StartVerify(config, repo, backupId, restoreSession, scheduler);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/retention-check", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            bool dryRun = body.TryGetProperty("dryRun", out var d) && d.GetBoolean();
            var (status, payload) = DashboardApi.StartRetentionCheck(config, scheduler, dryRun);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/nntp-test", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string host = GetBodyString(body, "host");
            // Omitted fields default to the TLS pair (563 + on) — security
            // audit D-2: an API caller that doesn't state a choice must
            // not silently get a plaintext test connection.
            int port = body.TryGetProperty("port", out var p) ? p.GetInt32() : 563;
            string user = body.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            string pass = body.TryGetProperty("pass", out var pw) ? pw.GetString() ?? "" : "";
            bool ssl = !body.TryGetProperty("ssl", out var s) || s.GetBoolean();
            var (status, payload) = DashboardApi.TestNntpConnection(host, port, user, pass, ssl);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/smb-test", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string share = GetBodyString(body, "share");
            string user = body.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            string pass = body.TryGetProperty("pass", out var pw) ? pw.GetString() ?? "" : "";
            var (status, payload) = DashboardApi.TestSmbConnection(share, user, pass);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/file-exists", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string path = GetBodyString(body, "path");
            bool exists = !string.IsNullOrEmpty(path) && File.Exists(path);
            return Results.Json(new ExistsDto(exists));
        });

        app.MapGet("/api/operations/usb-drives", () =>
        {
            var (status, payload) = DashboardApi.ListUsbDrives();
            return Results.Json(payload, statusCode: status);
        });

        app.MapGet("/api/operations/removable-drives", () =>
        {
            var (status, payload) = DashboardApi.ListRemovableDrives();
            return Results.Json(payload, statusCode: status);
        });

        app.MapGet("/api/operations/drives", () =>
        {
            var (status, payload) = DashboardApi.ListPhysicalDrives();
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/restore", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = GetBodyString(body, "repo");
            string backupId = GetBodyString(body, "backupId");
            string destDir = GetBodyString(body, "destDir");
            string smbShare = body.TryGetProperty("smbShare", out var ss) ? ss.GetString() ?? "" : "";
            string smbUser = body.TryGetProperty("smbUser", out var su) ? su.GetString() ?? "" : "";
            string mergePolicyRaw = body.TryGetProperty("mergePolicy", out var mpol) ? mpol.GetString() ?? "" : "";
            // Unknown/absent policy falls back to Overwrite (classic behavior).
            UsenetBackup.Core.MergePolicyInfo.TryParse(mergePolicyRaw, out var mergePolicy);
            // P2-C8: the host token lets a service stop interrupt the
            // restore between files/chunks so its outcome gets logged
            // instead of the process dying mid-write in silence.
            var (status, payload) = DashboardApi.StartRestore(config, repo, backupId, destDir, mergePolicy, smbShare, smbUser, restoreSession, ct, scheduler);
            return Results.Json(payload, statusCode: status);
        });

        app.MapGet("/api/operations/restores", () =>
        {
            var (status, payload) = DashboardApi.ListActiveRestores();
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/restore/cancel", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string destDir = body.TryGetProperty("destDir", out var dd) ? dd.GetString() ?? "" : "";
            string device = body.TryGetProperty("device", out var dv) ? dv.GetString() ?? "" : "";
            var (status, payload) = DashboardApi.CancelRestore(
                string.IsNullOrWhiteSpace(destDir) ? null : destDir,
                string.IsNullOrWhiteSpace(device) ? null : device);
            return Results.Json(payload, statusCode: status);
        });

        app.MapGet("/api/operations/nzb", (string repo, string backupId) =>
        {
            var (status, payload, fileName) = DashboardApi.GenerateNzb(config, repo, backupId, restoreSession);
            if (status != 200) return Results.Json(payload, statusCode: status);
            return Results.File((byte[])payload, "application/x-nzb", fileName);
        });

        app.MapPost("/api/operations/download-nzb", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!request.HasFormContentType) return Results.BadRequest(new ErrorDto("Expected multipart form."));
            var form = await request.ReadFormAsync();
            var file = form.Files["nzb"];
            if (file is null || file.Length == 0) return Results.BadRequest(new ErrorDto("No NZB file uploaded."));
            string tmp = Path.Combine(Path.GetTempPath(), "filekeep-" + Guid.NewGuid().ToString("N") + ".nzb");
            await using (var fs = File.Create(tmp)) await file.CopyToAsync(fs);
            var (status, payload) = DashboardApi.StartNzbDownload(config, scheduler, tmp, restoreSession);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/disk-backup", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = GetBodyString(body, "repo");
            string device = GetBodyString(body, "device");
            string imageName = body.TryGetProperty("imageName", out var n) ? n.GetString() ?? "disk.img" : "disk.img";
            string? smbShare = body.TryGetProperty("smbShare", out var s) ? s.GetString() : null;
            bool sparse = !body.TryGetProperty("sparse", out var sp) || sp.GetBoolean();
            var (status, payload) = DashboardApi.StartDiskBackup(config, repo, device, imageName, smbShare, sparse, scheduler);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/disk-restore", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = GetBodyString(body, "repo");
            string backupId = GetBodyString(body, "backupId");
            string device = GetBodyString(body, "device");
            bool confirmed = body.TryGetProperty("confirmed", out var c) && c.GetBoolean();
            string? deviceModel = body.TryGetProperty("deviceModel", out var dm) ? dm.GetString() : null;
            long? deviceSizeBytes = body.TryGetProperty("deviceSizeBytes", out var ds) && ds.TryGetInt64(out long s) ? s : null;
            var (status, payload) = DashboardApi.StartDiskRestore(config, repo, backupId, device, confirmed, restoreSession, deviceModel, deviceSizeBytes, ct, scheduler);
            return Results.Json(payload, statusCode: status);
        });



        app.MapGet("/api/operations/winre-prerequisites", () =>
            Results.Json(DashboardApi.CheckWinRePrerequisites()));

        app.MapGet("/api/operations/winre-build-status", () =>
            Results.Json(DashboardApi.GetWinReBuildStatus()));

        app.MapPost("/api/operations/winre-build", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string driveLetter = body.TryGetProperty("driveLetter", out var dl) ? dl.GetString() ?? "" : "";
            int diskNumber = body.TryGetProperty("diskNumber", out var dn) && dn.ValueKind == JsonValueKind.Number ? dn.GetInt32() : -1;
            string imagePath = body.TryGetProperty("imagePath", out var ip) ? ip.GetString() ?? "" : "";
            string? sourceDir = body.TryGetProperty("sourceDir", out var sd) ? sd.GetString() : null;
            string bootMode = body.TryGetProperty("bootMode", out var bm) ? bm.GetString() ?? "Both" : "Both";
            int imageSizeMB = body.TryGetProperty("imageSizeMB", out var ism) && ism.ValueKind == JsonValueKind.Number ? ism.GetInt32() : 4096;
            string? expectedModel = body.TryGetProperty("expectedModel", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() : null;
            long? expectedSizeBytes = body.TryGetProperty("expectedSizeBytes", out var es) && es.ValueKind == JsonValueKind.Number ? es.GetInt64() : null;
            var (status, payload) = DashboardApi.StartWinReBuild(driveLetter, diskNumber, imagePath, sourceDir, bootMode, imageSizeMB, expectedModel, expectedSizeBytes);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/operations/winre-build/cancel", (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.CancelWinReBuild();
            return Results.Json(payload, statusCode: status);
        });


        // Settings UI: read and update the backup job configuration.
        // All writes require the CSRF token and are validated before saving.
        app.MapGet("/api/config", () =>
            Results.Json(new ConfigSnapshotDto(
                config.DashboardPort,
                config.DashboardBind,
                // Never expose the encrypted password blob to the UI.
                // Snapshot under JobsLock: UpsertJob/DeleteJob mutate concurrently.
                config.SnapshotJobs().Select(DashboardApi.ToDto).ToList(),
                // Never expose the encrypted password blob to the UI.
                config.Nntp == null ? null : new ConfigNntpDto(
                    config.Nntp.Host,
                    config.Nntp.Port,
                    config.Nntp.Username,
                    config.Nntp.Ssl,
                    config.Nntp.Connections,
                    config.Nntp.HasPassword),
                config.SnapshotProviders().Select(p => new ConfigNntpProviderDto(
                    p.Host,
                    p.Port,
                    p.Username,
                    p.Ssl,
                    p.Connections,
                    p.RetentionDays,
                    p.RedundancyMode,
                    p.HasPassword)).ToList(),
                // Backup locations (Usenet providers + SMB shares). Never
                // expose encrypted password blobs to the UI.
                config.SnapshotLocations().Select(l => DashboardApi.ToDto(config, l)).ToList())));

        // Disk usage for disk-backed locations (USB drives, network
        // shares), fetched by the locations card after it renders —
        // probing a dead share can block, so it never rides /api/config.
        app.MapGet("/api/config/location-disk", () =>
        {
            var (status, payload) = DashboardApi.GetLocationDiskUsage(config);
            return Results.Json(payload, statusCode: status);
        });

        app.MapGet("/api/run-progress", () =>
        {
            var progress = scheduler.GetRunProgress();
            if (progress is null)
                return Results.Json(new ActiveDto(false));
            return Results.Json(new RunProgressDto(true, progress));
        });

        app.MapPost("/api/jobs/{name}/cancel", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!DashboardApi.IsKnownJob(scheduler, name))
                return Results.NotFound(new ErrorDto($"Unknown job '{name}'."));
            return scheduler.CancelRun(name)
                ? Results.Json(new OkDto(true))
                : Results.Json(new ErrorDto($"Job '{name}' is not running."),
                    statusCode: StatusCodes.Status409Conflict);
        });

        app.MapPost("/api/config/jobs", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            BackupJobConfig? job;
            try { job = await request.ReadFromJsonAsync<BackupJobConfig>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid job JSON.")); }
            if (job is null)
                return Results.BadRequest(new ErrorDto("Empty job."));
            var (status, payload) = DashboardApi.UpsertJob(config, scheduler, configPath, job, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapDelete("/api/config/jobs/{name}", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.DeleteJob(config, scheduler, configPath, name, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/config/locations", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            BackupLocation? loc;
            try { loc = await request.ReadFromJsonAsync<BackupLocation>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid location JSON.")); }
            if (loc is null)
                return Results.BadRequest(new ErrorDto("Empty location."));
            var (status, payload) = DashboardApi.UpsertLocation(config, configPath, loc, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapDelete("/api/config/locations/{name}", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.DeleteLocation(config, scheduler, configPath, name, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/config/jobs/{name}/pause", async (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            bool paused = body.TryGetProperty("paused", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True;
            var (status, payload) = DashboardApi.SetJobPaused(config, scheduler, configPath, name, paused, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/config/jobs/{name}/attach-location", async (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? location = body.TryGetProperty("location", out var l) ? l.GetString() : null;
            if (string.IsNullOrWhiteSpace(location))
                return Results.BadRequest(new ErrorDto("location is required."));
            var (status, payload) = DashboardApi.AttachLocation(config, scheduler, configPath, name, location, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/locations/{name}/test", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.TestLocation(config, name);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/locations/{name}/scan", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.ScanLocationForBackups(config, name, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/vault/scan-usb", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string drivePath = body.TryGetProperty("drivePath", out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? ""
                : "";
            var (status, payload) = DashboardApi.ScanUsbForBackups(config, drivePath, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/vault/import-discovered", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = body.TryGetProperty("repo", out var rEl) && rEl.ValueKind == JsonValueKind.String
                ? rEl.GetString() ?? ""
                : "";
            string backupId = body.TryGetProperty("backupId", out var bEl) && bEl.ValueKind == JsonValueKind.String
                ? bEl.GetString() ?? ""
                : "";
            string sourcePath = body.TryGetProperty("sourcePath", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString() ?? ""
                : "";
            var (status, payload) = DashboardApi.ImportDiscoveredBackup(config, repo, backupId, sourcePath, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapGet("/api/config/passphrase-status", (HttpRequest request) =>
        {
            var (status, payload) = DashboardApi.GetPassphraseStatus(config);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/config/passphrase", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? passphrase = body.TryGetProperty("passphrase", out var pp) ? pp.GetString() : null;
            bool useYubiKey = body.TryGetProperty("yubikey", out var yk) && yk.GetBoolean();
            var (proofPassphrase, proofYubiKey) = ProofFieldsOf(body);
            var (status, payload) = DashboardApi.SetPassphrase(config, configPath, passphrase, useYubiKey, yubiKeyProvider, log, proofPassphrase, proofYubiKey);
            return Results.Json(payload, statusCode: status);
        });

        app.MapGet("/api/protection/profile", () =>
        {
            var profile = DashboardApi.GetProtectionProfile(config);
            return Results.Content(FileKeepJsonOptions.Serialize(profile,
                FileKeepJsonOptions.Default, FileKeepJsonContext.Default.ProtectionProfileResponse),
                "application/json");
        });

        app.MapPost("/api/protection/profile", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            ProtectionProfileRequest? profile;
            try
            {
                profile = FileKeepJsonOptions.Deserialize(await ReadBodyStringAsync(request),
                    FileKeepJsonOptions.Default, FileKeepJsonContext.Default.ProtectionProfileRequest);
            }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            if (profile is null)
                return Results.BadRequest(new ErrorDto("Empty profile."));
            var (status, payload) = DashboardApi.SetProtectionProfile(config, configPath, profile, log, yubiKeyProvider);
            if (status != 200)
                return Results.Json(payload, statusCode: status);
            return Results.Content(FileKeepJsonOptions.Serialize((ProtectionProfileResponse)payload,
                FileKeepJsonOptions.Default, FileKeepJsonContext.Default.ProtectionProfileResponse),
                "application/json");
        });

        app.MapPost("/api/protection/yubikey/add", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            AddYubiKeyRequest? body;
            try
            {
                body = FileKeepJsonOptions.Deserialize(await ReadBodyStringAsync(request),
                    FileKeepJsonOptions.Default, FileKeepJsonContext.Default.AddYubiKeyRequest);
            }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            if (body is null)
                return Results.BadRequest(new ErrorDto("Empty request."));
            var (status, payload) = DashboardApi.AddYubiKeyName(config, configPath, body.Name, log, body.ProofPassphrase, body.ProofYubiKey, yubiKeyProvider);
            if (status != 200)
                return Results.Json(payload, statusCode: status);
            return Results.Content(FileKeepJsonOptions.Serialize((AddYubiKeyResponse)payload,
                FileKeepJsonOptions.Default, FileKeepJsonContext.Default.AddYubiKeyResponse),
                "application/json");
        });

        app.MapPost("/api/protection/phone/setup", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            // The proof rides in an optional JSON body; first-time
            // setup sends none, exactly like the other gated routes.
            var (proofPassphrase, proofYubiKey) = await ReadProofFieldsAsync(request);
            var (status, payload) = DashboardApi.SetupPhoneTotp(config, configPath, log, proofPassphrase, proofYubiKey, yubiKeyProvider);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/protection/phone/verify", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? code = body.TryGetProperty("code", out var c) ? c.GetString() : null;
            var (proofPassphrase, proofYubiKey) = ProofFieldsOf(body);
            var (status, payload) = DashboardApi.VerifyPhoneTotp(config, code, configPath, log, proofPassphrase, proofYubiKey, yubiKeyProvider);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/yubikey/program-spare", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (proofPassphrase, proofYubiKey) = await ReadProofFieldsAsync(request);
            var (status, payload) = DashboardApi.ProgramSpareKey(config, configPath, yubiKeyProvider, log, proofPassphrase, proofYubiKey);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/yubikey/link", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            YubiKeyLinkRequest? body;
            try
            {
                body = FileKeepJsonOptions.Deserialize(await ReadBodyStringAsync(request),
                    FileKeepJsonOptions.Default, FileKeepJsonContext.Default.YubiKeyLinkRequest);
            }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            // The setup blocks until the user touches the key twice
            // (program + wrap, 60s timeout each).
            var (status, payload) = DashboardApi.LinkYubiKey(config, configPath, body?.Slot, yubiKeyProvider, log, body?.ProofPassphrase, body?.ProofYubiKey ?? false);
            if (status != 200)
                return Results.Json(payload, statusCode: status);
            return Results.Content(
                JsonSerializer.Serialize((OkYubiKeyDto)payload, DashboardJsonContext.Default.OkYubiKeyDto),
                "application/json");
        });

        app.MapPost("/api/yubikey/setup-generated", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            YubiKeyLinkRequest? body;
            try
            {
                body = FileKeepJsonOptions.Deserialize(await ReadBodyStringAsync(request),
                    FileKeepJsonOptions.Default, FileKeepJsonContext.Default.YubiKeyLinkRequest);
            }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            // The setup blocks until the user touches the key twice
            // (program + wrap, 60s timeout each). The generated secret is
            // returned in this response only — never again.
            var (status, payload) = DashboardApi.SetupGeneratedSecret(config, configPath, body?.Slot, yubiKeyProvider, log, body?.ProofPassphrase, body?.ProofYubiKey ?? false);
            if (status != 200)
                return Results.Json(payload, statusCode: status);
            return Results.Content(
                JsonSerializer.Serialize((YubiKeySetupGeneratedDto)payload, DashboardJsonContext.Default.YubiKeySetupGeneratedDto),
                "application/json");
        });

        app.MapPost("/api/restore/unlock", (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            // The touch blocks until the user presses the key (or 60s timeout).
            var (status, payload) = DashboardApi.UnlockRestoreSession(config, restoreSession, yubiKeyProvider, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/restore/unlock-secret", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? secret = body.TryGetProperty("secret", out var s) ? s.GetString() : null;
            var (status, payload) = DashboardApi.UnlockRestoreSessionWithSecret(config, restoreSession, secret, log);
            if (status != 200)
                return Results.Json(payload, statusCode: status);
            return Results.Content(
                JsonSerializer.Serialize((OkDto)payload, DashboardJsonContext.Default.OkDto),
                "application/json");
        });

        app.MapPost("/api/restore/unlock-phone", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? code = body.TryGetProperty("code", out var c) ? c.GetString() : null;
            var (status, payload) = DashboardApi.UnlockRestoreSessionWithPhone(config, restoreSession, code, log);
            if (status != 200)
                return Results.Json(payload, statusCode: status);
            return Results.Content(
                JsonSerializer.Serialize((OkDto)payload, DashboardJsonContext.Default.OkDto),
                "application/json");
        });

        app.MapPost("/api/restore/lock", (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.LockRestoreSession(restoreSession);
            return Results.Json(payload);
        });

        app.MapGet("/api/restore/session", () =>
        {
            var (status, payload) = DashboardApi.GetRestoreSessionStatus(config, restoreSession);
            return Results.Json(payload);
        });

        await app.RunAsync(ct);
    }
}
