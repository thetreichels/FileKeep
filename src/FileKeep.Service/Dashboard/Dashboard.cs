using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Json;
using FileKeep.Core.Nntp;
using FileKeep.Core.Security;
using FileKeep.Core.Service;

namespace FileKeep.Service;

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
    /// <c>proofYubiKey</c> / <c>proofTotp</c>) from a JSON request body,
    /// for routes whose normal shape carries no body or a body parsed
    /// elsewhere (phone setup, program-spare). An absent or malformed
    /// body simply means "no proof supplied" — the DashboardApi gate
    /// then decides whether that is acceptable (first-time setup) or
    /// a refusal.
    /// </summary>
    private static async Task<(string? ProofPassphrase, bool ProofYubiKey, string? ProofTotp)>
        ReadProofFieldsAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0)
            return (null, false, null);
        try
        {
            string raw = await ReadBodyStringAsync(request);
            if (string.IsNullOrWhiteSpace(raw))
                return (null, false, null);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? pass = root.TryGetProperty("proofPassphrase", out var pp)
                ? pp.GetString() : null;
            bool yubiKey = root.TryGetProperty("proofYubiKey", out var py) &&
                           py.ValueKind == JsonValueKind.True;
            string? totp = root.TryGetProperty("proofTotp", out var pt)
                ? pt.GetString() : null;
            return (pass, yubiKey, totp);
        }
        catch
        {
            return (null, false, null);
        }
    }

    /// <summary>Extracts the security-proof fields from an already-parsed JSON body.</summary>
    private static (string? ProofPassphrase, bool ProofYubiKey, string? ProofTotp) ProofFieldsOf(JsonElement body) =>
        (body.TryGetProperty("proofPassphrase", out var pp) ? pp.GetString() : null,
         body.TryGetProperty("proofYubiKey", out var py) && py.ValueKind == JsonValueKind.True,
         body.TryGetProperty("proofTotp", out var pt) ? pt.GetString() : null);

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
        // Offline Folders card session: separate from the restore
        // session by design — unlocking one never unlocks the other.
        var offlineFoldersSession = new OfflineFoldersSession();
        string offlineFoldersPath = OfflineFoldersCatalog.CatalogPathFor(configPath);
        // Slice 2 WebDAV share: loopback-only (it lives on this host),
        // gated by the card session, serving decrypted bytes from
        // memory. No endpoint here writes plaintext to disk.
        var offlineDav = new OfflineFoldersDavHandler(config, offlineFoldersSession, offlineFoldersPath, log);
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
            o.SerializerOptions.TypeInfoResolverChain.Add(FileKeep.Core.Json.FileKeepJsonContext.Default);
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
            var (status, payload) = DashboardApi.StartVerify(config, repo, backupId, restoreSession, scheduler, log);
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
            FileKeep.Core.MergePolicyInfo.TryParse(mergePolicyRaw, out var mergePolicy);
            // P2-C8: the host token lets a service stop interrupt the
            // restore between files/chunks so its outcome gets logged
            // instead of the process dying mid-write in silence.
            var (status, payload) = DashboardApi.StartRestore(config, repo, backupId, destDir, mergePolicy, smbShare, smbUser, restoreSession, ct, scheduler, log);
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
            var (status, payload) = DashboardApi.StartDiskRestore(config, repo, backupId, device, confirmed, restoreSession, deviceModel, deviceSizeBytes, ct, scheduler, log);
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
            var (proofPassphrase, proofYubiKey, proofTotp) = ProofFieldsOf(body);
            var (status, payload) = DashboardApi.SetPassphrase(config, configPath, passphrase, useYubiKey, yubiKeyProvider, log, proofPassphrase, proofYubiKey, proofTotp);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/security/passphrase-wrap", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? passphrase = body.TryGetProperty("passphrase", out var pp) ? pp.GetString() : null;
            var (proofPassphrase, proofYubiKey, proofTotp) = ProofFieldsOf(body);
            var (status, payload) = DashboardApi.AddPassphraseWrap(config, configPath, passphrase, yubiKeyProvider, log, proofPassphrase, proofYubiKey, proofTotp);
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
            var (status, payload) = DashboardApi.AddYubiKeyName(config, configPath, body.Name, log, body.ProofPassphrase, body.ProofYubiKey, yubiKeyProvider, body.ProofTotp);
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
            var (proofPassphrase, proofYubiKey, proofTotp) = await ReadProofFieldsAsync(request);
            var (status, payload) = DashboardApi.SetupPhoneTotp(config, configPath, log, proofPassphrase, proofYubiKey, yubiKeyProvider, proofTotp);
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
            var (proofPassphrase, proofYubiKey, proofTotp) = ProofFieldsOf(body);
            var (status, payload) = DashboardApi.VerifyPhoneTotp(config, code, configPath, log, proofPassphrase, proofYubiKey, yubiKeyProvider, proofTotp);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/yubikey/program-spare", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (proofPassphrase, proofYubiKey, proofTotp) = await ReadProofFieldsAsync(request);
            var (status, payload) = DashboardApi.ProgramSpareKey(config, configPath, yubiKeyProvider, log, proofPassphrase, proofYubiKey, proofTotp);
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
            var (status, payload) = DashboardApi.LinkYubiKey(config, configPath, body?.Slot, yubiKeyProvider, log, body?.ProofPassphrase, body?.ProofYubiKey ?? false, body?.ProofTotp);
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
            var (status, payload) = DashboardApi.SetupGeneratedSecret(config, configPath, body?.Slot, yubiKeyProvider, log, body?.ProofPassphrase, body?.ProofYubiKey ?? false, body?.ProofTotp);
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

        app.MapPost("/api/restore/unlock-passphrase", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? passphrase = body.TryGetProperty("passphrase", out var p) ? p.GetString() : null;
            var (status, payload) = DashboardApi.UnlockRestoreSessionWithPassphrase(config, restoreSession, passphrase, log);
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

        // Offline Folders card (slice 1: locked card + encrypted
        // catalog; slice 2: memory-served file use over WebDAV).
        app.MapGet("/api/offline-folders/status", () =>
        {
            var (status, payload) = DashboardApi.GetOfflineFoldersStatus(offlineFoldersSession);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/offline-folders/unlock", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            // The card unlock takes passphrase/YubiKey proofs only;
            // the TOTP element ca1ff44 added to the proof fields is
            // discarded here — card unlock via TOTP is separate
            // follow-up work, not part of this merge.
            var (proofPassphrase, proofYubiKey, _) = await ReadProofFieldsAsync(request);
            var (status, payload) = DashboardApi.UnlockOfflineFolders(config, offlineFoldersSession,
                offlineFoldersPath, proofPassphrase, proofYubiKey, yubiKeyProvider, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/vaults/migrate", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? newSecret = body.TryGetProperty("newSecret", out var nEl) ? nEl.GetString() : null;
            var (proofPassphrase, proofYubiKey, proofTotp) = ProofFieldsOf(body);
            var (status, payload) = DashboardApi.MigrateVaults(config, configPath, newSecret,
                yubiKeyProvider, log, proofPassphrase, proofYubiKey, proofTotp);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/offline-folders/export", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? folder = body.TryGetProperty("folder", out var fEl) ? fEl.GetString() : null;
            string? exportPassphrase = body.TryGetProperty("exportPassphrase", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(folder))
                return Results.BadRequest(new ErrorDto("A folder is required."));
            var (status, payload) = DashboardApi.ExportOfflineFolder(config, offlineFoldersSession,
                folder, exportPassphrase, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/offline-folders/import", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? exportPassphrase = body.TryGetProperty("exportPassphrase", out var pEl) ? pEl.GetString() : null;
            OfflineExportPackageDto? package = null;
            if (body.TryGetProperty("package", out var pkgEl))
            {
                try { package = System.Text.Json.JsonSerializer.Deserialize<OfflineExportPackageDto>(pkgEl.GetRawText()); }
                catch { /* falls through to the null check */ }
            }
            if (package is null)
                return Results.BadRequest(new ErrorDto("An export package is required."));
            var (status, payload) = DashboardApi.ImportOfflineFolder(config, offlineFoldersSession,
                offlineFoldersPath, package, exportPassphrase, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/offline-folders/forget", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? folder = body.TryGetProperty("folder", out var fEl) ? fEl.GetString() : null;
            bool confirm = body.TryGetProperty("confirm", out var cEl) && cEl.GetBoolean();
            if (string.IsNullOrWhiteSpace(folder))
                return Results.BadRequest(new ErrorDto("A folder is required."));
            var (status, payload) = DashboardApi.ForgetOfflineFolder(config, offlineFoldersSession,
                offlineFoldersPath, folder, confirm, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/offline-folders/open-token", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? folder = body.TryGetProperty("folder", out var fEl) ? fEl.GetString() : null;
            string? file = body.TryGetProperty("file", out var fiEl) ? fiEl.GetString() : null;
            bool readOnly = body.TryGetProperty("readOnly", out var rEl) && rEl.GetBoolean();
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(file))
                return Results.BadRequest(new ErrorDto("A folder and a file are required."));
            if (!offlineFoldersSession.IsUnlocked)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var issued = offlineDav.IssueOpenToken(folder, file, readOnly);
            if (issued is null)
                return Results.NotFound(new ErrorDto("No such offline file."));
            return Results.Json(new OfflineOpenTokenDto(issued.Value.Token, issued.Value.Url, readOnly));
        });

        app.MapPost("/api/offline-folders/release-open", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? token = body.TryGetProperty("token", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new ErrorDto("A token is required."));
            if (!offlineDav.ReleaseOpenToken(token))
                return Results.NotFound(new ErrorDto("No such open."));
            return Results.Json(new OkDto(true));
        });

        app.MapGet("/api/offline-folders/opens", () =>
        {
            if (!offlineFoldersSession.IsUnlocked)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Json(offlineDav.ListOpens());
        });

        app.MapPost("/api/offline-folders/save-as", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? folder = body.TryGetProperty("folder", out var fEl) ? fEl.GetString() : null;
            string? file = body.TryGetProperty("file", out var fiEl) ? fiEl.GetString() : null;
            string? destination = body.TryGetProperty("destination", out var dEl) ? dEl.GetString() : null;
            string? locationName = body.TryGetProperty("locationName", out var lEl) ? lEl.GetString() : null;
            string? path = body.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
            string? fileName = body.TryGetProperty("fileName", out var nEl) ? nEl.GetString() : null;
            bool overwrite = body.TryGetProperty("overwrite", out var oEl) && oEl.GetBoolean();
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(file) ||
                string.IsNullOrWhiteSpace(destination))
                return Results.BadRequest(new ErrorDto("A folder, a file, and a destination are required."));
            if (!offlineFoldersSession.IsUnlocked)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.SaveOfflineFileAs(config, offlineFoldersSession,
                folder, file, destination, locationName, path, fileName, overwrite);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/offline-folders/rekey", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? oldSecret = body.TryGetProperty("oldSecret", out var oEl) ? oEl.GetString() : null;
            string? newSecret = body.TryGetProperty("newSecret", out var nEl) ? nEl.GetString() : null;
            var (proofPassphrase, proofYubiKey, proofTotp) = ProofFieldsOf(body);
            var (status, payload) = DashboardApi.RekeyOfflineFolders(config, configPath, offlineFoldersSession,
                oldSecret, newSecret, yubiKeyProvider, log, proofPassphrase, proofYubiKey, proofTotp);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/offline-folders/lock", (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.LockOfflineFolders(offlineFoldersSession);
            return Results.Json(payload, statusCode: status);
        });

        app.MapGet("/api/offline-folders", () =>
        {
            var (status, payload) = DashboardApi.GetOfflineFolders(offlineFoldersSession);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/offline-folders", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? locationId = body.TryGetProperty("locationId", out var l) ? l.GetString() : null;
            var (status, payload) = DashboardApi.AddOfflineFolder(config, offlineFoldersSession,
                offlineFoldersPath, name, locationId, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapDelete("/api/offline-folders/{name}", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.DeleteOfflineFolder(offlineFoldersSession,
                offlineFoldersPath, name, log);
            return Results.Json(payload, statusCode: status);
        });

        // Slice 2: file list / upload / delete within an offline folder.
        app.MapGet("/api/offline-folders/{folder}/files", (string folder) =>
        {
            var (status, payload) = DashboardApi.GetOfflineFolderFiles(offlineFoldersSession, folder);
            return Results.Json(payload, statusCode: status);
        });

        app.MapPost("/api/offline-folders/{folder}/files", async (string folder, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            IFormCollection form;
            try { form = await request.ReadFormAsync(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid upload.")); }
            // Batch semantics differ from files-by-path on purpose:
            // multipart streams cannot be prevalidated without consuming
            // them, so duplicate basenames are refused upfront while
            // per-file store failures are reported per file instead of
            // aborting the batch. Browsers send basenames; the store
            // path validates strictly, so a hostile name is a per-file
            // failure, not a traversal.
            var batch = form.Files.Select(f =>
                (Path.GetFileName(f.FileName), f.OpenReadStream(), DateTimeOffset.UtcNow)).ToList();
            var (status, payload) = DashboardApi.StoreOfflineFilesBatch(config, offlineFoldersSession,
                offlineFoldersPath, folder, batch, log);
            return Results.Json(payload, statusCode: status);
        });

        // Native-picker upload: same store, paths instead of multipart bytes.
        // See DashboardApi.AddOfflineFilesByPath for why this exists.
        app.MapPost("/api/offline-folders/{folder}/files-by-path", async (string folder, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            if (!body.TryGetProperty("paths", out var pathsEl) || pathsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                return Results.BadRequest(new ErrorDto("paths must be an array."));
            var paths = new List<string>();
            foreach (var p in pathsEl.EnumerateArray())
            {
                if (p.ValueKind != System.Text.Json.JsonValueKind.String)
                    return Results.BadRequest(new ErrorDto("paths must be strings."));
                paths.Add(p.GetString()!);
            }
            if (paths.Count == 0)
                return Results.BadRequest(new ErrorDto("No files selected."));
            var (status, payload) = DashboardApi.AddOfflineFilesByPath(config, offlineFoldersSession,
                offlineFoldersPath, folder, paths, log);
            return Results.Json(payload, statusCode: status);
        });

        app.MapDelete("/api/offline-folders/{folder}/files/{*path}", (string folder, string path, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.DeleteOfflineFile(offlineFoldersSession,
                offlineFoldersPath, folder, path, log);
            return Results.Json(payload, statusCode: status);
        });

        // Slice 2 WebDAV share (/dav/offline/**). Deliberately NOT
        // CSRF-checked: Explorer cannot send the dashboard token, and
        // browsers cannot issue these methods cross-origin without a
        // CORS preflight this host never satisfies. The gate is the
        // card session itself, plus loopback-only binding.
        app.MapMethods("/dav/offline/{**rest}",
            new[] { "OPTIONS", "PROPFIND", "GET", "PUT", "DELETE", "LOCK", "UNLOCK", "MKCOL" },
            async (HttpContext ctx, string? rest) =>
            {
                var segments = new List<string>();
                if (!string.IsNullOrEmpty(rest))
                {
                    foreach (string raw in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try { segments.Add(Uri.UnescapeDataString(raw)); }
                        catch
                        {
                            ctx.Response.StatusCode = 400;
                            await ctx.Response.WriteAsync("Invalid path.");
                            return;
                        }
                    }
                }
                int depth = 1;
                if (ctx.Request.Headers.TryGetValue("Depth", out var depthHeader))
                {
                    depth = depthHeader.ToString() switch
                    {
                        "0" => 0,
                        "1" => 1,
                        "infinity" => int.MaxValue,
                        _ => 1,
                    };
                }
                string? token = null;
                if (ctx.Request.Headers.TryGetValue("Lock-Token", out var lockHeader))
                    token = lockHeader.ToString().Trim('<', '>', ' ');
                else if (ctx.Request.Headers.TryGetValue("If", out var ifHeader))
                {
                    string ifValue = ifHeader.ToString();
                    int at = ifValue.IndexOf("opaquelocktoken:", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        int end = ifValue.IndexOfAny(new[] { '>', ' ', ')' }, at);
                        token = end < 0 ? ifValue[at..] : ifValue[at..end];
                    }
                }
                string? openToken = ctx.Request.Query.TryGetValue("open", out var openValues)
                    ? openValues.ToString()
                    : null;
                var davRequest = new OfflineFoldersDavHandler.DavRequest
                {
                    Method = ctx.Request.Method,
                    Segments = segments.ToArray(),
                    Depth = depth,
                    Body = ctx.Request.Method is "PUT" ? ctx.Request.Body : null,
                    LockToken = token,
                    OpenToken = string.IsNullOrEmpty(openToken) ? null : openToken,
                };
                var result = offlineDav.Handle(davRequest);
                foreach (var (name, value) in result.Headers)
                    ctx.Response.Headers[name] = value;
                if (result.ContentType is not null)
                    ctx.Response.ContentType = result.ContentType;
                if (result.ContentLength is not null)
                    ctx.Response.ContentLength = result.ContentLength;
                ctx.Response.StatusCode = result.Status;
                try
                {
                    if (result.BodyBytes is not null)
                        await ctx.Response.Body.WriteAsync(result.BodyBytes);
                    else if (result.BodyWriter is not null)
                        await result.BodyWriter(ctx.Response.Body);
                }
                catch (OfflineFoldersDavHandler.DavStreamException)
                {
                    // Headers already sent: the only safe failure is
                    // aborting the connection, which the client reads
                    // as a failed transfer, never as good bytes.
                    ctx.Abort();
                    return;
                }
                catch (OperationCanceledException)
                {
                    ctx.Abort();
                    return;
                }
            });

        await app.RunAsync(ct);
    }
}
