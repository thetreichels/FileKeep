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

    public static async Task RunAsync(ServiceConfig config, BackupScheduler scheduler,
        string configPath, CancellationToken ct, Action<string>? log = null)
    {
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

        app.MapGet("/", () => Results.Content(DashboardHtml.Page(csrfToken), "text/html; charset=utf-8"));

        app.MapGet("/api/status", () => Results.Json(DashboardApi.GetStatus(scheduler)));

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
                    log($"dashboard: manual run of job '{name}' FAILED: {ex.Message}");
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
            string sourceDir = GetBodyString(body, "sourceDir");
            string repoPath = GetBodyString(body, "repoPath");
            var (status, payload) = DashboardApi.StartOneTimeBackup(scheduler, sourceDir, repoPath, config);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        // Repo-scoped endpoints only accept repos belonging to configured
        // jobs, so the dashboard cannot be pointed at arbitrary paths.
        app.MapGet("/api/backups", (string repo) =>
        {
            var (status, payload) = DashboardApi.GetBackups(config, repo, restoreSession);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapGet("/api/backups/remote", (string repo) =>
        {
            var (status, payload) = DashboardApi.GetRemoteBackups(config, repo, restoreSession);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/backups/remote/import", async (string repo, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string[] ids = body.TryGetProperty("backupIds", out var el) && el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToArray()
                : Array.Empty<string>();
            var (status, payload) = DashboardApi.ImportRemoteBackups(config, repo, ids, restoreSession);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapGet("/api/log", (string repo, int lines = 100) =>
        {
            var (status, payload) = DashboardApi.GetLog(config, repo, lines);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/smb-sync", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string share = GetBodyString(body, "share");
            string user = body.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            var (status, payload) = DashboardApi.StartSmbSync(config, share, user);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/verify", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = GetBodyString(body, "repo");
            string backupId = GetBodyString(body, "backupId");
            var (status, payload) = DashboardApi.StartVerify(config, repo, backupId, restoreSession);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/retention-check", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            bool dryRun = body.TryGetProperty("dryRun", out var d) && d.GetBoolean();
            var (status, payload) = DashboardApi.StartRetentionCheck(config, scheduler, dryRun);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/diagnose", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string host = GetBodyString(body, "host");
            var (status, payload) = DashboardApi.RunDiagnose(host);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/nntp-test", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string host = GetBodyString(body, "host");
            int port = body.TryGetProperty("port", out var p) ? p.GetInt32() : 119;
            string user = body.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            string pass = body.TryGetProperty("pass", out var pw) ? pw.GetString() ?? "" : "";
            bool ssl = body.TryGetProperty("ssl", out var s) && s.GetBoolean();
            var (status, payload) = DashboardApi.TestNntpConnection(host, port, user, pass, ssl);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/smb-test", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string share = GetBodyString(body, "share");
            var (status, payload) = DashboardApi.TestSmbConnection(share);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapGet("/api/operations/removable-drives", () =>
        {
            var (status, payload) = DashboardApi.ListRemovableDrives();
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapGet("/api/operations/drives", () =>
        {
            var (status, payload) = DashboardApi.ListPhysicalDrives();
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
            var (status, payload) = DashboardApi.StartRestore(config, repo, backupId, destDir, mergePolicy, smbShare, smbUser, restoreSession);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/backup-now", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = GetBodyString(body, "repo");
            string sourceDir = GetBodyString(body, "sourceDir");
            var (status, payload) = DashboardApi.StartAdhocBackup(config, repo, sourceDir);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapGet("/api/operations/nzb", (string repo, string backupId) =>
        {
            var (status, payload, fileName) = DashboardApi.GenerateNzb(config, repo, backupId, restoreSession);
            if (status != 200) return Results.BadRequest(payload);
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
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
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
            var (status, payload) = DashboardApi.StartDiskBackup(config, repo, device, imageName, smbShare, sparse);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
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
            var (status, payload) = DashboardApi.StartDiskRestore(config, repo, backupId, device, confirmed, restoreSession);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
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
            var (status, payload) = DashboardApi.StartWinReBuild(driveLetter, diskNumber, imagePath, sourceDir, bootMode, imageSizeMB);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/winre-build/cancel", (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.CancelWinReBuild();
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });


        // Settings UI: read and update the backup job configuration.
        // All writes require the CSRF token and are validated before saving.
        app.MapGet("/api/config", () =>
            Results.Json(new ConfigSnapshotDto(
                config.DashboardPort,
                config.DashboardBind,
                // Never expose the encrypted password blob to the UI.
                // Snapshot under JobsLock: UpsertJob/DeleteJob mutate concurrently.
                config.SnapshotJobs().Select(j => new ConfigJobDto(
                    j.Name,
                    j.Repo,
                    j.Source,
                    j.Schedule,
                    j.Mode,
                    j.JobType,
                    j.BackupPrivilege,
                    j.Vss,
                    j.AutoUpload,
                    j.Targets,
                    j.SmbShare,
                    j.SmbUser,
                    !string.IsNullOrEmpty(j.SmbPasswordProtected),
                    j.AutoVerify,
                    j.VerificationMode,
                    j.RedundancyMode)).ToList(),
                // Never expose the encrypted password blob to the UI.
                config.Nntp == null ? null : new ConfigNntpDto(
                    config.Nntp.Host,
                    config.Nntp.Port,
                    config.Nntp.Username,
                    config.Nntp.Ssl,
                    config.Nntp.Connections,
                    config.Nntp.HasPassword),
                config.NntpProviders.Select(p => new ConfigNntpProviderDto(
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
                config.Locations.Select(l => new ConfigLocationDto(
                    l.Name,
                    l.Type,
                    l.Host,
                    l.Port,
                    l.Username,
                    l.Ssl,
                    l.Connections,
                    l.Newsgroup,
                    l.RetentionDays,
                    l.Share,
                    l.SmbUser,
                    l.HasPassword,
                    config.SnapshotJobs()
                        .Where(j => j.EffectiveTargets.Any(t =>
                            t.Equals(l.Name, StringComparison.OrdinalIgnoreCase)))
                        .Select(j => j.Name).ToList())).ToList())));

        app.MapGet("/api/expiration", (string repo, int warnDays = 90) =>
        {
            try
            {
                // Build per-provider retention map from service config
                var retentionByHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (config.Nntp is not null && !string.IsNullOrWhiteSpace(config.Nntp.Host))
                    retentionByHost[config.Nntp.Host] = config.Nntp.RetentionDays;
                foreach (var p in config.NntpProviders)
                {
                    if (!string.IsNullOrWhiteSpace(p.Host))
                        retentionByHost[p.Host] = p.RetentionDays;
                }
                int GetRetention(string host) =>
                    retentionByHost.TryGetValue(host, out int days) ? days : 1095;

                var tracker = new UsenetUploadTracker(repo);
                var expiring = tracker.GetExpiring(GetRetention, warnDays);
                return Results.Json(expiring.Select(x => new ExpiringBackupDto(
                    x.Record.BackupId,
                    x.Record.ProviderHost,
                    x.Record.UploadedUtc,
                    x.ExpiresUtc,
                    x.DaysLeft,
                    GetRetention(x.Record.ProviderHost))).ToList());
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorDto(ex.Message));
            }
        });

        app.MapGet("/api/upload-progress", () =>
        {
            var tracker = scheduler.ActiveUpload;
            if (tracker is null)
                return Results.Json(new ActiveDto(false));
            return Results.Json(new ActiveProgressDto(true, tracker.GetSnapshot()));
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
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapDelete("/api/config/jobs/{name}", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.DeleteJob(config, scheduler, configPath, name, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapDelete("/api/config/locations/{name}", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.DeleteLocation(config, configPath, name, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/locations/{name}/test", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.TestLocation(config, name);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/locations/{name}/scan", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.ScanLocationForBackups(config, name, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/config/nntp", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            NntpConfig? nntp;
            try { nntp = await request.ReadFromJsonAsync<NntpConfig>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid NNTP JSON.")); }
            if (nntp is null)
                return Results.BadRequest(new ErrorDto("Empty NNTP config."));
            var (status, payload) = DashboardApi.UpdateNntp(config, scheduler, configPath, nntp, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/config/nntp-providers", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            List<NntpConfig>? providers;
            try { providers = await request.ReadFromJsonAsync<List<NntpConfig>>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid providers JSON.")); }
            if (providers is null)
                return Results.BadRequest(new ErrorDto("Empty providers list."));
            var (status, payload) = DashboardApi.UpdateNntpProviders(config, scheduler, configPath, providers, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapGet("/api/config/passphrase-status", (HttpRequest request) =>
        {
            var (status, payload) = DashboardApi.GetPassphraseStatus(config);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
            var (status, payload) = DashboardApi.SetPassphrase(config, configPath, passphrase, useYubiKey, yubiKeyProvider, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
            var (status, payload) = DashboardApi.SetProtectionProfile(config, configPath, profile, log);
            if (status != 200)
                return Results.BadRequest(payload);
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
            var (status, payload) = DashboardApi.AddYubiKeyName(config, configPath, body.Name, log);
            if (status != 200)
                return Results.BadRequest(payload);
            return Results.Content(FileKeepJsonOptions.Serialize((AddYubiKeyResponse)payload,
                FileKeepJsonOptions.Default, FileKeepJsonContext.Default.AddYubiKeyResponse),
                "application/json");
        });

        app.MapPost("/api/protection/phone/setup", (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.SetupPhoneTotp(config, configPath, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/protection/phone/verify", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            System.Text.Json.JsonElement body;
            try { body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return Results.BadRequest(new ErrorDto("Invalid JSON.")); }
            string? code = body.TryGetProperty("code", out var c) ? c.GetString() : null;
            var (status, payload) = DashboardApi.VerifyPhoneTotp(config, code);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapGet("/api/yubikey/status", () =>
        {
            var (status, payload) = DashboardApi.GetYubiKeyStatus(yubiKeyProvider);
            return Results.Json(payload);
        });

        app.MapPost("/api/yubikey/program-spare", (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (status, payload) = DashboardApi.ProgramSpareKey(config, configPath, yubiKeyProvider, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/restore/unlock", (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            // The touch blocks until the user presses the key (or 60s timeout).
            var (status, payload) = DashboardApi.UnlockRestoreSession(config, restoreSession, yubiKeyProvider, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
