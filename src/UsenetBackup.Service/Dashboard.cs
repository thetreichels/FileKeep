using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsenetBackup.Core;
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
        var app = builder.Build();

        app.MapGet("/", () => Results.Content(DashboardHtml.Page(csrfToken), "text/html; charset=utf-8"));

        app.MapGet("/api/status", () => Results.Json(DashboardApi.GetStatus(scheduler)));

        // Windows known user folders (Documents, Pictures, etc.) for the GUI's
        // quick-pick. Uses Environment.GetFolderPath (SHGetKnownFolderPath wrapper).
        app.MapGet("/api/user-folders", () =>
        {
            var folders = new List<object>();
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
                    folders.Add(new { name, path });
            }
            return Results.Json(folders);
        });

        app.MapPost("/api/jobs/{name}/run", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!DashboardApi.IsKnownJob(scheduler, name))
                return Results.NotFound(new { error = $"Unknown job '{name}'." });
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

        app.MapGet("/api/operations/usb-drives", () =>
        {
            var (status, payload) = DashboardApi.ListUsbDrives();
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
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart form." });
            var form = await request.ReadFormAsync();
            var file = form.Files["nzb"];
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No NZB file uploaded." });
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
            var (status, payload) = DashboardApi.StartDiskBackup(config, repo, device, imageName);
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

        app.MapGet("/api/operations/lan-server", () =>
            Results.Json(DashboardApi.GetLanServerStatus()));


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

        app.MapPost("/api/operations/lan-server/start", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            int port = body.TryGetProperty("port", out var p) ? p.GetInt32() : 8477;
            var (status, payload) = DashboardApi.StartLanServer(config, port);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/lan-server/stop", (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            DashboardApi.StopLanServer();
            return Results.Json(new { stopped = true });
        });

        // Settings UI: read and update the backup job configuration.
        // All writes require the CSRF token and are validated before saving.
        app.MapGet("/api/config", () =>
            Results.Json(new
            {
                dashboardPort = config.DashboardPort,
                dashboardBind = config.DashboardBind,
                // Never expose the encrypted password blob to the UI.
                // Snapshot under JobsLock: UpsertJob/DeleteJob mutate concurrently.
                jobs = config.SnapshotJobs().Select(j => new
                {
                    name = j.Name,
                    repo = j.Repo,
                    source = j.Source,
                    schedule = j.Schedule,
                    mode = j.Mode,
                    jobType = j.JobType,
                    backupPrivilege = j.BackupPrivilege,
                    vss = j.Vss,
                    autoUpload = j.AutoUpload,
                    targets = j.Targets,
                    smbShare = j.SmbShare,
                    smbUser = j.SmbUser,
                    hasSmbPassword = !string.IsNullOrEmpty(j.SmbPasswordProtected),
                    autoVerify = j.AutoVerify,
                    verificationMode = j.VerificationMode,
                    redundancyMode = j.RedundancyMode,
                }).ToList(),
                // Never expose the encrypted password blob to the UI.
                nntp = config.Nntp == null ? null : new
                {
                    host = config.Nntp.Host,
                    port = config.Nntp.Port,
                    username = config.Nntp.Username,
                    ssl = config.Nntp.Ssl,
                    connections = config.Nntp.Connections,
                    hasPassword = config.Nntp.HasPassword,
                },
                nntpProviders = config.NntpProviders.Select(p => new
                {
                    host = p.Host,
                    port = p.Port,
                    username = p.Username,
                    ssl = p.Ssl,
                    connections = p.Connections,
                    retentionDays = p.RetentionDays,
                    redundancyMode = p.RedundancyMode,
                    hasPassword = p.HasPassword,
                }).ToList(),
                // Backup locations (Usenet providers + SMB shares). Never
                // expose encrypted password blobs to the UI.
                locations = config.Locations.Select(l => new
                {
                    name = l.Name,
                    type = l.Type,
                    host = l.Host,
                    port = l.Port,
                    username = l.Username,
                    ssl = l.Ssl,
                    connections = l.Connections,
                    newsgroup = l.Newsgroup,
                    retentionDays = l.RetentionDays,
                    share = l.Share,
                    smbUser = l.SmbUser,
                    hasPassword = l.HasPassword,
                    usedBy = config.SnapshotJobs()
                        .Where(j => j.EffectiveTargets.Any(t =>
                            t.Equals(l.Name, StringComparison.OrdinalIgnoreCase)))
                        .Select(j => j.Name).ToList(),
                }).ToList(),
            }));

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
                return Results.Json(expiring.Select(x => new
                {
                    backupId = x.Record.BackupId,
                    providerHost = x.Record.ProviderHost,
                    uploadedUtc = x.Record.UploadedUtc,
                    expiresUtc = x.ExpiresUtc,
                    daysLeft = x.DaysLeft,
                    retentionDays = GetRetention(x.Record.ProviderHost),
                }).ToList());
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/upload-progress", () =>
        {
            var tracker = scheduler.ActiveUpload;
            if (tracker is null)
                return Results.Json(new { active = false });
            return Results.Json(new { active = true, progress = tracker.GetSnapshot() });
        });

        app.MapPost("/api/config/jobs", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            BackupJobConfig? job;
            try { job = await request.ReadFromJsonAsync<BackupJobConfig>(); }
            catch { return Results.BadRequest(new { error = "Invalid job JSON." }); }
            if (job is null)
                return Results.BadRequest(new { error = "Empty job." });
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
            catch { return Results.BadRequest(new { error = "Invalid location JSON." }); }
            if (loc is null)
                return Results.BadRequest(new { error = "Empty location." });
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

        app.MapPost("/api/config/nntp", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            NntpConfig? nntp;
            try { nntp = await request.ReadFromJsonAsync<NntpConfig>(); }
            catch { return Results.BadRequest(new { error = "Invalid NNTP JSON." }); }
            if (nntp is null)
                return Results.BadRequest(new { error = "Empty NNTP config." });
            var (status, payload) = DashboardApi.UpdateNntp(config, scheduler, configPath, nntp, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/config/nntp-providers", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            List<NntpConfig>? providers;
            try { providers = await request.ReadFromJsonAsync<List<NntpConfig>>(); }
            catch { return Results.BadRequest(new { error = "Invalid providers JSON." }); }
            if (providers is null)
                return Results.BadRequest(new { error = "Empty providers list." });
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
            catch { return Results.BadRequest(new { error = "Invalid JSON." }); }
            string? passphrase = body.TryGetProperty("passphrase", out var pp) ? pp.GetString() : null;
            bool useYubiKey = body.TryGetProperty("yubikey", out var yk) && yk.GetBoolean();
            var (status, payload) = DashboardApi.SetPassphrase(config, configPath, passphrase, useYubiKey, yubiKeyProvider, log);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapGet("/api/yubikey/status", () =>
        {
            var (status, payload) = DashboardApi.GetYubiKeyStatus(yubiKeyProvider);
            return Results.Json(payload);
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

/// <summary>
/// The dashboard's endpoint logic, transport-free so it is unit-testable
/// without binding TCP (loopback TCP is unavailable in some sandboxes;
/// the Kestrel wiring itself is verified by smoke-testing the binary).
/// </summary>
public static class DashboardApi
{
    public static object GetStatus(BackupScheduler scheduler) => new
    {
        startedLocal = scheduler.StartedLocal,
        jobs = scheduler.Jobs.Select(j => new
        {
            name = j.Config.Name,
            repo = j.Config.Repo,
            source = j.Config.Source,
            schedule = j.Schedule.ToString(),
            mode = j.Config.Mode,
            backupPrivilege = j.Config.BackupPrivilege,
            vss = j.Config.Vss,
            nextRunLocal = j.NextRunLocal,
            consecutiveFailures = j.ConsecutiveFailures,
            lastResult = j.LastResult is null ? null : new
            {
                startedLocal = j.LastResult.StartedLocal,
                success = j.LastResult.Success,
                backupId = j.LastResult.BackupId,
                files = j.LastResult.Files,
                error = j.LastResult.Error,
            },
        }),
    };

    public static bool IsKnownJob(BackupScheduler scheduler, string name) =>
        scheduler.Jobs.Any(j => j.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Constant-time CSRF token check for state-changing dashboard endpoints.
    /// </summary>
    public static bool ValidateCsrfToken(string expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
            return false;
        byte[] a = Encoding.UTF8.GetBytes(expected);
        byte[] b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static bool IsKnownRepo(ServiceConfig config, string repo) =>
        config.SnapshotJobs().Any(j => string.Equals(
            Path.GetFullPath(j.Repo), Path.GetFullPath(repo), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// All distinct repository paths from the configured backup jobs, in
    /// job order. Tools operate across these without asking the user to
    /// pick one — the repo is FileKeep's internal storage.
    /// </summary>
    public static IReadOnlyList<string> AllJobRepos(ServiceConfig config) =>
        config.SnapshotJobs()
            .Select(j => j.Repo)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>First configured job's repo (the primary one), or null when no jobs exist.</summary>
    public static string? PrimaryRepo(ServiceConfig config) =>
        AllJobRepos(config).FirstOrDefault();

    /// <summary>
    /// Lists all physical drives (Windows only) for disk pickers.
    /// </summary>
    public static (int Status, object Payload) ListPhysicalDrives()
    {
        if (!OperatingSystem.IsWindows())
            return (400, new { error = "Drive listing requires Windows." });
        try
        {
            var drives = UsenetBackup.Core.Recovery.PhysicalDrives.List()
                .Select(d => new
                {
                    number = d.Number,
                    devicePath = d.DevicePath,
                    model = d.Model,
                    sizeBytes = d.SizeBytes,
                    isSystemDrive = d.IsSystemDrive,
                })
                .ToArray();
            return (200, drives);
        }
        catch (Exception ex)
        {
            return (500, new { error = ex.Message });
        }
    }

    /// <summary>Returns (200, backup list) or (400/500, error payload).</summary>
    public static (int Status, object Payload) GetBackups(ServiceConfig config, string repo, RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new { error = "Restore session is locked. Touch your YubiKey to unlock it." })
                : (500, new { error = $"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})." });
        try
        {
            using var r = BackupRepository.Open(repo, passphrase);
            return (200, r.ListBackups().OrderByDescending(b => b.CreatedUtc).ToArray());
        }
        catch (Exception ex)
        {
            return (500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Discovers backup manifests posted to Usenet (the encrypted manifest
    /// index). Returns manifests not present locally — "all backups FileKeep
    /// is aware of" beyond the local repo. Uses the first configured provider.
    /// </summary>
    public static (int Status, object Payload) GetRemoteBackups(ServiceConfig config, string repo, RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new { error = "No Usenet provider configured." });
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new { error = "Restore session is locked. Touch your YubiKey to unlock it." })
                : (500, new { error = $"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})." });
        try
        {
            string? nntpPassword = Environment.GetEnvironmentVariable(BackupScheduler.NntpPasswordEnvVar);
            if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(provider.PasswordProtected))
                nntpPassword = Dpapi.Unprotect(provider.PasswordProtected);

            using var client = new UsenetBackup.Core.Nntp.NntpClient(provider.Host, provider.Port, provider.Ssl);
            client.Connect();
            if (!string.IsNullOrEmpty(provider.Username))
                client.Authenticate(provider.Username, nntpPassword ?? "");

            using var r = BackupRepository.Open(repo, passphrase);
            string providerKey = UsenetBackup.Core.Nntp.ChunkMessageIndex.MakeProviderKey(provider.Host, provider.Newsgroup);
            using var store = new UsenetBackup.Core.Nntp.NntpBlobStore(
                client, provider.Newsgroup, r.RepoId, r.CatalogPath,
                messageIndex: r.MessageIndex, providerKey: providerKey);
            var remote = r.DiscoverRemoteManifests(store);
            var localIds = new HashSet<string>(r.ListBackups().Select(b => b.BackupId));
            var fresh = remote
                .Where(m => !localIds.Contains(m.Manifest.BackupId))
                .Select(m => new
                {
                    backupId = m.Manifest.BackupId,
                    createdUtc = m.Manifest.CreatedUtc,
                    type = m.Manifest.Type,
                    source = m.Manifest.Source,
                    fileCount = m.Manifest.Files.Count,
                    snapshot = m.Manifest.Snapshot,
                })
                .OrderByDescending(m => m.createdUtc)
                .ToArray();
            return (200, fresh);
        }
        catch (Exception ex)
        {
            return (500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Imports remote-discovered manifests into the local repo so they can
    /// be downloaded/restored like local backups.
    /// </summary>
    public static (int Status, object Payload) ImportRemoteBackups(ServiceConfig config, string repo, string[] backupIds, RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new { error = "No Usenet provider configured." });
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new { error = "Restore session is locked. Touch your YubiKey to unlock it." })
                : (500, new { error = $"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})." });
        try
        {
            string? nntpPassword = Environment.GetEnvironmentVariable(BackupScheduler.NntpPasswordEnvVar);
            if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(provider.PasswordProtected))
                nntpPassword = Dpapi.Unprotect(provider.PasswordProtected);

            using var client = new UsenetBackup.Core.Nntp.NntpClient(provider.Host, provider.Port, provider.Ssl);
            client.Connect();
            if (!string.IsNullOrEmpty(provider.Username))
                client.Authenticate(provider.Username, nntpPassword ?? "");

            using var r = BackupRepository.Open(repo, passphrase);
            string providerKey = UsenetBackup.Core.Nntp.ChunkMessageIndex.MakeProviderKey(provider.Host, provider.Newsgroup);
            using var store = new UsenetBackup.Core.Nntp.NntpBlobStore(
                client, provider.Newsgroup, r.RepoId, r.CatalogPath,
                messageIndex: r.MessageIndex, providerKey: providerKey);
            var remote = r.DiscoverRemoteManifests(store);
            var wanted = new HashSet<string>(backupIds ?? Array.Empty<string>());
            int imported = 0;
            foreach (var m in remote)
            {
                if (wanted.Contains(m.Manifest.BackupId))
                {
                    r.ImportManifest(m.Manifest);
                    imported++;
                }
            }
            OperationLog.Append(repo, "import-remote",
                $"imported={imported} host={provider.Host}");
            return (200, new { imported });
        }
        catch (Exception ex)
        {
            return (500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Starts a manual NNTP upload of a backup in the background.
    /// Returns (202, accepted) or (400, error).
    /// </summary>
    public static (int Status, object Payload) StartUpload(
        ServiceConfig config, BackupScheduler scheduler, string repo, string backupId)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new { error = "backupId is required." });
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new { error = "No Usenet provider configured." });
        Task.Run(() =>
        {
            try
            {
                scheduler.UploadBackup(repo, backupId);
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-upload",
                    $"backupId={backupId} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-upload", $"backupId={backupId} started");
        return (202, new { accepted = true });
    }

    /// <summary>
    /// Starts a manual SMB sync in the background. The password comes from the
    /// service config (per-job or global env var); the dashboard never handles
    /// it. Returns (202, accepted) or (400, error).
    /// </summary>
    public static (int Status, object Payload) StartSmbSync(
        ServiceConfig config, string share, string user)
    {
        if (string.IsNullOrWhiteSpace(share))
            return (400, new { error = "SMB share path is required." });
        var repos = AllJobRepos(config);
        if (repos.Count == 0)
            return (400, new { error = "No backup jobs configured." });
        // Find SMB credentials: first matching job's stored password, else env var.
        var job = config.SnapshotJobs().FirstOrDefault(j =>
            !string.IsNullOrWhiteSpace(j.SmbShare));
        string? password = Environment.GetEnvironmentVariable(BackupScheduler.SmbPasswordEnvVar);
        if (string.IsNullOrEmpty(password) && job is not null && !string.IsNullOrEmpty(job.SmbPasswordProtected))
            password = UsenetBackup.Core.Dpapi.Unprotect(job.SmbPasswordProtected);
        string effectiveUser = string.IsNullOrWhiteSpace(user) ? job?.SmbUser ?? "" : user;
        Task.Run(() =>
        {
            foreach (string repo in repos)
            {
                try
                {
                    using var smb = UsenetBackup.Core.SmbShare.Connect(share, effectiveUser, password);
                    var sync = new UsenetBackup.Core.SmbSync(msg =>
                        OperationLog.Append(repo, "smb-sync", msg));
                    var result = sync.Sync(repo, smb);
                    OperationLog.Append(repo, "smb-sync",
                        $"to {share} complete: {result.ChunksCopied} chunks, {result.ManifestsCopied} manifests");
                }
                catch (Exception ex)
                {
                    OperationLog.Append(repo, "smb-sync", $"to {share} FAILED: {ex.Message}");
                }
            }
        });
        foreach (string repo in repos)
            OperationLog.Append(repo, "smb-sync", $"to {share} started");
        return (202, new { accepted = true, repos = repos.Count });
    }

    /// <summary>
    /// Starts a manual backup verification in the background.
    /// </summary>
    public static (int Status, object Payload) StartVerify(
        ServiceConfig config, string repo, string backupId,
        RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new { error = "backupId is required." });
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new { error = "Restore session is locked. Touch your YubiKey to unlock it." })
                : (500, new { error = "Service passphrase is not configured." });
        Task.Run(() =>
        {
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                var issues = r.Verify(backupId);
                OperationLog.Append(repo, "manual-verify",
                    issues.Count == 0 ? $"backupId={backupId} OK" : $"backupId={backupId} ISSUES: {string.Join("; ", issues.Take(5))}");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-verify",
                    $"backupId={backupId} FAILED: {ex.Message}");
            }
        });
        return (202, new { accepted = true });
    }

    /// <summary>
    /// Starts a manual retention check in the background.
    /// </summary>
    public static (int Status, object Payload) StartRetentionCheck(
        ServiceConfig config, BackupScheduler scheduler, bool dryRun)
    {
        var repos = AllJobRepos(config);
        if (repos.Count == 0)
            return (400, new { error = "No backup jobs configured." });
        Task.Run(() =>
        {
            foreach (string repo in repos)
            {
                try
                {
                    scheduler.RunRetentionCheck(repo, dryRun);
                }
                catch (Exception ex)
                {
                    OperationLog.Append(repo, "manual-retention",
                        $"dryRun={dryRun} FAILED: {ex.Message}");
                }
            }
        });
        foreach (string repo in repos)
            OperationLog.Append(repo, "manual-retention", $"dryRun={dryRun} started");
        return (202, new { accepted = true, repos = repos.Count });
    }

    /// <summary>
    /// Starts a restore of a backup to a destination directory in the background.
    /// </summary>
    public static (int Status, object Payload) StartRestore(
        ServiceConfig config, string repo, string backupId, string destDir,
        UsenetBackup.Core.MergePolicy mergePolicy,
        string? smbShare = null, string? smbUser = null,
        RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new { error = "backupId is required." });
        if (string.IsNullOrWhiteSpace(destDir))
            return (400, new { error = "destDir is required." });
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new { error = "Restore session is locked. Touch your YubiKey to unlock it." })
                : (500, new { error = "Service passphrase is not configured." });
        // SMB credentials: matching job's stored password, else env var.
        string? smbPassword = null;
        if (!string.IsNullOrWhiteSpace(smbShare))
        {
            var job = config.SnapshotJobs().FirstOrDefault(j =>
                j.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
            smbPassword = Environment.GetEnvironmentVariable(BackupScheduler.SmbPasswordEnvVar);
            if (string.IsNullOrEmpty(smbPassword) && job is not null && !string.IsNullOrEmpty(job.SmbPasswordProtected))
                smbPassword = UsenetBackup.Core.Dpapi.Unprotect(job.SmbPasswordProtected);
            if (string.IsNullOrWhiteSpace(smbUser))
                smbUser = job?.SmbUser;
        }
        Task.Run(() =>
        {
            try
            {
                // Pull missing chunks from the SMB share first, if one was given.
                if (!string.IsNullOrWhiteSpace(smbShare))
                {
                    using var ws = new UsenetBackup.Core.Recovery.WizardState
                    {
                        RepoPath = repo,
                        Passphrase = passphrase,
                        SelectedBackupId = backupId,
                        SmbShare = smbShare,
                        SmbUser = smbUser ?? "",
                        SmbPassword = smbPassword ?? "",
                    };
                    ws.EnsureSmbChunks();
                }
                using var r = BackupRepository.Open(repo, passphrase);
                var restoreOptions = new UsenetBackup.Core.RestoreOptions { MergePolicy = mergePolicy };
                var restoreResult = r.Restore(backupId, destDir, restoreOptions);
                OperationLog.Append(repo, "manual-restore",
                    $"backupId={backupId} dest={destDir} policy={mergePolicy} " +
                    $"restored={restoreResult.RestoredFiles} " +
                    $"skippedDiffering={restoreResult.SkippedDiffering.Count} OK");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-restore",
                    $"backupId={backupId} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-restore", $"backupId={backupId} dest={destDir} started");
        return (202, new { accepted = true });
    }

    /// <summary>
    /// Starts an ad-hoc backup of a source directory in the background.
    /// </summary>
    public static (int Status, object Payload) StartAdhocBackup(
        ServiceConfig config, string repo, string sourceDir)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return (400, new { error = "sourceDir must be an existing directory." });
        string? passphrase = BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = "Service passphrase is not configured." });
        Task.Run(() =>
        {
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                var manifest = r.BackupDirectory(sourceDir, snapshotProvider: null);
                OperationLog.Append(repo, "manual-backup",
                    $"id={manifest.BackupId} source={sourceDir} OK");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-backup",
                    $"source={sourceDir} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-backup", $"source={sourceDir} started");
        return (202, new { accepted = true });
    }

    /// <summary>
    /// Starts a one-time manual backup to a local folder chosen at run time.
    /// The only supported path to a local-only backup: no scheduled job is
    /// created. The repo is initialized if it does not exist yet. Runs in
    /// the background via the scheduler; progress lands in the Operations log.
    /// Returns (202, {accepted:true}) or (400, {error}).
    /// </summary>
    public static (int Status, object Payload) StartOneTimeBackup(
        BackupScheduler scheduler, string sourceDir, string repoPath, ServiceConfig? config = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return (400, new { error = "Choose an existing source folder." });
        if (string.IsNullOrWhiteSpace(repoPath))
            return (400, new { error = "Choose a destination folder for the backup." });
        try
        {
            // The repo must not live inside the source: the backup would try
            // to back up its own output.
            string fullSource = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullRepo = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (fullRepo.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                return (400, new { error = "The destination folder cannot be inside the source folder." });
            if (fullSource.StartsWith(fullRepo, StringComparison.OrdinalIgnoreCase) && fullSource.Length != fullRepo.Length)
                return (400, new { error = "The source folder cannot be inside the destination folder." });
        }
        catch
        {
            return (400, new { error = "Invalid folder paths." });
        }
        string? passphrase = config is null
            ? Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar)
            : BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(passphrase))
            return (400, new { error = "Set a repository passphrase first (Settings \u2192 Security)." });
        Task.Run(() =>
        {
            try
            {
                var result = scheduler.RunOneTimeBackup(sourceDir, repoPath);
                OperationLog.Append(repoPath, "one-time-backup",
                    $"source={sourceDir} {(result.Success ? $"OK id={result.BackupId} files={result.Files}" : "FAILED: " + result.Error)}");
            }
            catch (Exception ex)
            {
                try
                {
                    OperationLog.Append(repoPath, "one-time-backup",
                        $"source={sourceDir} FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                catch { /* best effort */ }
            }
        });
        return (202, new { accepted = true });
    }

    /// <summary>
    /// Generates an NZB for a backup and returns the XML bytes.
    /// </summary>
    public static (int Status, object Payload, string FileName) GenerateNzb(
        ServiceConfig config, string repo, string backupId,
        RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." }, "");
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new { error = "backupId is required." }, "");
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new { error = "Restore session is locked. Touch your YubiKey to unlock it." }, "")
                : (500, new { error = "Service passphrase is not configured." }, "");
        try
        {
            var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
            string newsgroup = provider?.Newsgroup ?? "alt.binaries.test";
            using var r = BackupRepository.Open(repo, passphrase);
            var manifest = r.LoadManifest(backupId);
            using var catalog = new Catalog(r.CatalogPath);
            string xml = NzbGenerator.Generate(
                manifest,
                chunkId => r.GetChunkBlob(chunkId),
                chunkId => catalog.GetUploadTimeUtc(chunkId),
                new NzbGenerator.Options(newsgroup, "filekeep", r.RepoId));
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(xml);
            return (200, bytes, $"filekeep-{backupId}.nzb");
        }
        catch (Exception ex)
        {
            return (500, new { error = ex.Message }, "");
        }
    }

    /// <summary>
    /// Starts a download from an uploaded NZB file in the background.
    /// </summary>
    public static (int Status, object Payload) StartNzbDownload(
        ServiceConfig config, BackupScheduler scheduler, string nzbPath,
        RestoreSession? session = null)
    {
        // Chunks are content-addressed; fetch into the primary repo.
        string? repo = PrimaryRepo(config);
        if (repo is null)
            return (400, new { error = "No backup jobs configured." });
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new { error = "No Usenet provider configured." });
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new { error = "Restore session is locked. Touch your YubiKey to unlock it." })
                : (500, new { error = "Service passphrase is not configured." });
        Task.Run(() =>
        {
            try
            {
                scheduler.DownloadFromNzb(repo, nzbPath, passphrase);
                OperationLog.Append(repo, "manual-nzb-download", $"nzb={Path.GetFileName(nzbPath)} OK");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-nzb-download",
                    $"nzb={Path.GetFileName(nzbPath)} FAILED: {ex.Message}");
            }
            finally
            {
                try { File.Delete(nzbPath); } catch { }
            }
        });
        OperationLog.Append(repo, "manual-nzb-download", $"nzb={Path.GetFileName(nzbPath)} started");
        return (202, new { accepted = true });
    }

    /// <summary>
    /// Starts a disk-image backup in the background.
    /// </summary>
    public static (int Status, object Payload) StartDiskBackup(
        ServiceConfig config, string repo, string device, string imageName)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (string.IsNullOrWhiteSpace(device))
            return (400, new { error = "device is required." });
        string? passphrase = BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = "Service passphrase is not configured." });
        Task.Run(() =>
        {
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                var manifest = r.BackupDiskImage(device, imageName);
                OperationLog.Append(repo, "manual-disk-backup",
                    $"id={manifest.BackupId} device={device} OK");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-disk-backup",
                    $"device={device} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-disk-backup", $"device={device} started");
        return (202, new { accepted = true });
    }

    /// <summary>
    /// Starts a disk-image restore in the background. Destructive: the caller
    /// must pass confirmed=true (the UI requires an explicit checkbox plus a
    /// confirm dialog, and never lists the running system drive as a target).
    /// </summary>
    public static (int Status, object Payload) StartDiskRestore(
        ServiceConfig config, string repo, string backupId, string device, bool confirmed,
        RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new { error = "backupId is required." });
        if (string.IsNullOrWhiteSpace(device))
            return (400, new { error = "device is required." });
        if (!confirmed)
            return (400, new { error = "Restore not confirmed. Restore aborted." });
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new { error = "Restore session is locked. Touch your YubiKey to unlock it." })
                : (500, new { error = "Service passphrase is not configured." });
        Task.Run(() =>
        {
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                r.RestoreDiskImage(backupId, device);
                OperationLog.Append(repo, "manual-disk-restore",
                    $"backupId={backupId} device={device} OK");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-disk-restore",
                    $"backupId={backupId} device={device} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-disk-restore", $"backupId={backupId} device={device} started");
        return (202, new { accepted = true });
    }

    /// <summary>WinRE USB build state.</summary>
    private static string? _winReBuildStatus;
    private static string? _winReBuildError;
    private static string? _winReBuildDrive;

    /// <summary>Handle of the PowerShell build process while a build runs (for cancel/timeout).</summary>
    private static System.Diagnostics.Process? _winReBuildProcess;
    private static volatile bool _winReBuildCancelRequested;

    /// <summary>Per-step progress for the running WinRE USB build.</summary>
    private static readonly List<Dictionary<string, object>> _winReBuildSteps = new();
    private static readonly object _winReBuildStepsLock = new();
    private static DateTime _winReBuildStartedAt;

    private static LanServer? _lanServer;
    private static readonly object _lanServerLock = new();

    /// <summary>
    /// Checks prerequisites for WinRE USB creation: Windows, admin rights,
    /// reagentc, DISM, bcdboot, and WinRE enabled with a locatable WIM.
    /// ADK is NOT required.
    /// </summary>
    public static object CheckWinRePrerequisites()
    {
        var result = new Dictionary<string, object>();
        result["isWindows"] = OperatingSystem.IsWindows();
        if (!OperatingSystem.IsWindows())
        {
            result["ready"] = false;
            result["error"] = "WinRE USB creation requires Windows.";
            return result;
        }
        bool isAdmin = false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }
        result["isAdmin"] = isAdmin;

        bool hasReagentc = FindSystemTool("reagentc.exe") is not null;
        bool hasDism = FindSystemTool("dism.exe") is not null;
        bool hasBcdboot = FindSystemTool("bcdboot.exe") is not null;
        result["reagentc"] = hasReagentc;
        result["dism"] = hasDism;
        result["bcdboot"] = hasBcdboot;

        // Check WinRE status via reagentc /info
        bool winReEnabled = false;
        string winReLocation = "";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "reagentc.exe",
                Arguments = "/info",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            winReEnabled = output.Contains("Enabled", StringComparison.OrdinalIgnoreCase) &&
                           !output.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("Windows RE location:", StringComparison.OrdinalIgnoreCase))
                {
                    winReLocation = line.Split(':', 2)[1].Trim();
                    break;
                }
            }
        }
        catch { }
        result["winReEnabled"] = winReEnabled;
        result["winReLocation"] = winReLocation;

        // Check for the build script (installed layout or source tree)
        string scriptPath = FindWinReScript();
        result["scriptFound"] = !string.IsNullOrEmpty(scriptPath);

        var missing = new List<string>();
        if (!isAdmin) missing.Add("Administrator rights");
        if (!hasReagentc) missing.Add("reagentc.exe");
        if (!hasDism) missing.Add("dism.exe");
        if (!hasBcdboot) missing.Add("bcdboot.exe");
        if (!winReEnabled) missing.Add("WinRE enabled (reagentc /info)");
        if (string.IsNullOrEmpty(scriptPath)) missing.Add("build-winre-usb.ps1");

        result["ready"] = missing.Count == 0;
        result["missing"] = missing;
        return result;
    }

    private static string? FindSystemTool(string name)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = name,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            var first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch { return null; }
    }

    private static string FindWinReScript()
    {
        // Installed layout: <base>/winre/build-winre-usb.ps1
        string installed = Path.Combine(AppContext.BaseDirectory, "winre", "build-winre-usb.ps1");
        if (File.Exists(installed)) return installed;
        // Source tree: walk up from base directory
        string? dir = Path.GetDirectoryName(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "winre", "build-winre-usb.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }

    /// <summary>Current WinRE USB build status for the dashboard, with per-step progress.</summary>
    public static object GetWinReBuildStatus()
    {
        // Snapshot everything under the lock so the status, error, target,
        // and steps are always mutually consistent.
        List<Dictionary<string, object>> steps;
        string? status;
        string? error;
        string? drive;
        DateTime startedAt;
        lock (_winReBuildStepsLock)
        {
            steps = _winReBuildSteps.Select(s => new Dictionary<string, object>(s)).ToList();
            status = _winReBuildStatus;
            error = _winReBuildError;
            drive = _winReBuildDrive;
            startedAt = _winReBuildStartedAt;
        }

        int percent = 0;
        int etaSec = 0;
        string currentStepName = "";
        foreach (var s in steps)
        {
            if (s.TryGetValue("percent", out var pv) && pv is int pi) percent = Math.Max(percent, pi);
            if (s.TryGetValue("state", out var st) && st as string == "started")
            {
                currentStepName = s.TryGetValue("name", out var n) ? n as string ?? "" : "";
                if (s.TryGetValue("estimatedRemainingSec", out var ev) && ev is int ei) etaSec = ei;
            }
        }
        if (status == "complete") percent = 100;

        double elapsedSec = status == "running"
            ? (DateTime.UtcNow - startedAt).TotalSeconds
            : 0;

        return new
        {
            status = status ?? "idle",
            error = error ?? "",
            drive = drive ?? "",
            percent,
            currentStep = currentStepName,
            estimatedRemainingSec = etaSec,
            elapsedSec = (int)elapsedSec,
            steps,
        };
    }

    /// <summary>
    /// Starts a WinRE recovery media build in the background. Returns 202 if accepted.
    /// USB mode (driveLetter or diskNumber): destructive, the target USB drive is formatted.
    /// Image mode (imagePath): builds a bootable .vhdx disk image file instead.
    /// </summary>
    public static (int Status, object Payload) StartWinReBuild(string driveLetter, int diskNumber, string imagePath, string? sourceDir, string bootMode, int imageSizeMB)
    {
        if (!OperatingSystem.IsWindows())
            return (400, new { error = "WinRE recovery media creation requires Windows." });
        bootMode = (bootMode ?? "Both").Trim();
        if (bootMode != "UEFI" && bootMode != "BIOS" && bootMode != "Both")
            return (400, new { error = "bootMode must be UEFI, BIOS, or Both." });

        // Validate everything BEFORE claiming the running flag: every return
        // below must leave _winReBuildStatus untouched, or a rejected request
        // wedges the builder as "running" forever (a retry then reports
        // "A WinRE build is already running." with nothing actually running).
        bool imageMode = !string.IsNullOrWhiteSpace(imagePath);
        if (imageMode && (!string.IsNullOrWhiteSpace(driveLetter) || diskNumber >= 0))
            return (400, new { error = "Specify either a USB target or an image path, not both." });
        if (!imageMode)
        {
            driveLetter = driveLetter.Trim().TrimEnd(':', '\\');
            bool hasLetter = driveLetter.Length == 1 && char.IsLetter(driveLetter[0]);
            bool hasDisk = diskNumber >= 0;
            if (!hasLetter && !hasDisk)
                return (400, new { error = "Select a USB drive." });
            if (hasLetter && hasDisk)
                return (400, new { error = "Specify either a drive letter or a disk number, not both." });
            if (hasDisk)
            {
                // The dashboard only offers USB-bus disks; refuse anything else
                // even if the request was crafted by hand. This is checked here
                // (before the flag) rather than only in the script.
                bool knownUsb;
                try
                {
                    knownUsb = UsenetBackup.Core.Recovery.UsbDrives.List().Any(d => d.Number == diskNumber);
                }
                catch
                {
                    knownUsb = false;
                }
                if (!knownUsb)
                    return (400, new { error = $"Disk {diskNumber} is not a USB-attached drive." });
            }
        }
        else
        {
            var (normalizedPath, pathError) = NormalizeWinReImagePath(imagePath);
            if (pathError is not null)
                return (400, new { error = pathError });
            imagePath = normalizedPath!;
            if (imageSizeMB < 1024)
                return (400, new { error = "Image size must be at least 1024 MB." });
        }

        var prereq = CheckWinRePrerequisites() as Dictionary<string, object>;
        if (prereq is null || !(prereq.TryGetValue("ready", out var readyObj) && readyObj is bool ready && ready))
        {
            var missing = prereq?.TryGetValue("missing", out var m) == true ? m : "prerequisites not met";
            return (400, new { error = $"Prerequisites not met: {missing}" });
        }

        lock (_winReBuildStepsLock)
        {
            if (_winReBuildStatus == "running")
                return (400, new { error = "A WinRE build is already running." });
            _winReBuildStatus = "running";
            _winReBuildSteps.Clear();
            _winReBuildStartedAt = DateTime.UtcNow;
            _winReBuildProcess = null;
            _winReBuildCancelRequested = false;
            _winReBuildError = null;
            _winReBuildDrive = imageMode ? imagePath
                : diskNumber >= 0 ? $"Disk {diskNumber}" : driveLetter.ToUpperInvariant() + ":";
        }

        Task.Run(() =>
        {
            try
            {
                string scriptPath = FindWinReScript();
                if (string.IsNullOrEmpty(scriptPath))
                    throw new FileNotFoundException("build-winre-usb.ps1 not found.");

                // In the installed layout, use binary mode. Detect by the
                // presence of the installed binaries next to the script's
                // grandparent dir (...\FileKeep\winre\ -> ...\FileKeep\),
                // not by AppContext.BaseDirectory (the service exe lives in
                // ...\FileKeep\service\, so the old StartsWith check was
                // always false on installed systems).
                string? scriptGrandparent = Path.GetDirectoryName(Path.GetDirectoryName(scriptPath));
                bool isInstalledLayout = scriptGrandparent is not null &&
                    File.Exists(Path.Combine(scriptGrandparent, "cli", "FileKeep.exe"));

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                // Use ArgumentList, not an Arguments string: user-controlled
                // values (imagePath, sourceDir) must not be able to break out
                // of quoting and inject PowerShell statements.
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(scriptPath);
                if (imageMode)
                {
                    psi.ArgumentList.Add("-ImagePath");
                    psi.ArgumentList.Add(imagePath);
                }
                else if (diskNumber >= 0)
                {
                    psi.ArgumentList.Add("-DiskNumber");
                    psi.ArgumentList.Add(diskNumber.ToString());
                }
                else
                {
                    psi.ArgumentList.Add("-DriveLetter");
                    psi.ArgumentList.Add(driveLetter);
                }
                if (!imageMode)
                {
                    // The dashboard already confirmed the destructive format
                    // with the user, so the script must not prompt (it runs
                    // headless under the service, where a prompt can only fail
                    // or hang).
                    psi.ArgumentList.Add("-Force");
                }
                else
                {
                    psi.ArgumentList.Add("-ImageSizeMB");
                    psi.ArgumentList.Add(imageSizeMB.ToString());
                }
                psi.ArgumentList.Add("-BootMode");
                psi.ArgumentList.Add(bootMode);
                if (isInstalledLayout && scriptGrandparent is not null)
                {
                    psi.ArgumentList.Add("-BinaryDir");
                    psi.ArgumentList.Add(scriptGrandparent);
                }
                if (!string.IsNullOrWhiteSpace(sourceDir))
                {
                    psi.ArgumentList.Add("-SourceDir");
                    psi.ArgumentList.Add(sourceDir);
                }
                using var proc = System.Diagnostics.Process.Start(psi)!;
                lock (_winReBuildStepsLock) { _winReBuildProcess = proc; }
                try
                {
                    // Read stdout line-by-line so FKPROGRESS markers update the
                    // dashboard in real time instead of only at the end.
                    var outputLines = new System.Text.StringBuilder();
                    string? line;
                    while ((line = proc.StandardOutput.ReadLine()) is not null)
                    {
                        outputLines.AppendLine(line);
                        if (line.StartsWith("FKPROGRESS:", StringComparison.Ordinal))
                            ParseWinReProgress(line.Substring("FKPROGRESS:".Length).Trim());
                    }
                    string err = proc.StandardError.ReadToEnd();
                    if (_winReBuildCancelRequested)
                        throw new OperationCanceledException("WinRE build cancelled.");
                    if (!proc.WaitForExit(TimeSpan.FromMinutes(60)))
                    {
                        // Backstop against a hung script: a stuck DISM/diskpart
                        // would otherwise wedge the status as "running" until
                        // the service restarts. The dashboard also offers
                        // Cancel for user-initiated stops.
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        throw new TimeoutException("WinRE build timed out after 60 minutes and was stopped.");
                    }
                    if (proc.ExitCode != 0)
                        throw new InvalidOperationException($"build-winre-usb.ps1 exited {proc.ExitCode}: {err}{outputLines}");
                    _winReBuildStatus = "complete";
                    LogWinReBuild($"target={_winReBuildDrive}: complete");
                }
                finally
                {
                    lock (_winReBuildStepsLock)
                    {
                        if (_winReBuildProcess == proc) _winReBuildProcess = null;
                    }
                }
            }
            catch (Exception ex)
            {
                // Set the terminal state BEFORE logging so a logging failure
                // can never wedge the builder as "running".
                _winReBuildStatus = "failed";
                // Cap the message: a failed build can dump megabytes of
                // DISM/diskpart output; the tail is where the error is.
                _winReBuildError = TruncateTail(ex.Message, 4096);
                LogWinReBuild($"target={_winReBuildDrive}: FAILED: {_winReBuildError}");
            }
        });
        return (202, new { accepted = true });
    }

    /// <summary>
    /// Forgiving normalization for the image-mode output path. Accepts a folder
    /// (an existing directory, or a path with no file name, gets the default
    /// file name) or a path without an extension (".vhdx" is appended); only a
    /// non-.vhdx extension is rejected.
    /// </summary>
    private static (string? Path, string? Error) NormalizeWinReImagePath(string raw)
    {
        string p = raw.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (p.Length == 0)
            return (null, "Choose where to save the recovery image (full file path, e.g. D:\\images\\filekeep-recovery.vhdx).");
        if (Directory.Exists(p) || string.IsNullOrEmpty(Path.GetFileName(p)))
            p = Path.Combine(p, "filekeep-recovery.vhdx");
        else if (string.IsNullOrEmpty(Path.GetExtension(p)))
            p += ".vhdx";
        else if (!p.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            return (null, $"The image path must end with .vhdx (got \"{p}\").");
        return (p, null);
    }

    /// <summary>FileKeep's machine-wide data dir (service config and logs live here).</summary>
    private static string FileKeepDataDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileKeep");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Appends a winre-build line to the data-dir operations log. Never throws.</summary>
    private static void LogWinReBuild(string details)
    {
        try
        {
            string line = $"{DateTime.UtcNow:O} [winre-build] {details}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(FileKeepDataDir(), OperationLog.FileName), line);
        }
        catch
        {
            // Logging must never fail or wedge a build.
        }
    }

    /// <summary>Keeps only the tail of a long message (a failed build can dump MBs of tool output).</summary>
    private static string TruncateTail(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;
        return "[…truncated…]" + s.Substring(s.Length - maxLen);
    }

    /// <summary>
    /// Requests cancellation of the running WinRE build by killing its process
    /// tree. The build task observes the kill and reports "failed: cancelled".
    /// </summary>
    public static (int Status, object Payload) CancelWinReBuild()
    {
        lock (_winReBuildStepsLock)
        {
            if (_winReBuildStatus != "running")
                return (400, new { error = "No WinRE build is running." });
            _winReBuildCancelRequested = true;
            try { _winReBuildProcess?.Kill(entireProcessTree: true); } catch { }
            return (200, new { cancelled = true });
        }
    }

    /// <summary>Parses one FKPROGRESS JSON line from build-winre-usb.ps1.</summary>
    private static void ParseWinReProgress(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            int step = root.GetProperty("step").GetInt32();
            int totalSteps = root.GetProperty("totalSteps").GetInt32();
            string name = root.GetProperty("name").GetString() ?? $"Step {step}";
            string state = root.GetProperty("state").GetString() ?? "";
            int percent = root.TryGetProperty("percent", out var p) ? p.GetInt32() : 0;
            int etaSec = root.TryGetProperty("estimatedRemainingSec", out var e) ? e.GetInt32() : 0;

            lock (_winReBuildStepsLock)
            {
                // Ensure the list covers all steps seen so far.
                while (_winReBuildSteps.Count < totalSteps)
                    _winReBuildSteps.Add(new Dictionary<string, object>
                    {
                        ["step"] = _winReBuildSteps.Count + 1,
                        ["name"] = "",
                        ["state"] = "pending",
                    });
                var entry = _winReBuildSteps[step - 1];
                entry["name"] = name;
                entry["state"] = state;
                entry["percent"] = percent;
                entry["estimatedRemainingSec"] = etaSec;
                if (state == "started" && !entry.ContainsKey("startedAt"))
                    entry["startedAt"] = DateTime.UtcNow.ToString("o");
                if (state == "completed")
                    entry["completedAt"] = DateTime.UtcNow.ToString("o");
            }
        }
        catch
        {
            // A malformed progress line must not break the build.
        }
    }

    /// <summary>Current LAN server state for the dashboard.</summary>
    public static object GetLanServerStatus() =>
        new { running = _lanServer?.IsRunning == true, port = _lanServer?.Port ?? 0 };

    /// <summary>Starts the LAN chunk server for all configured job repos.</summary>
    public static (int Status, object Payload) StartLanServer(ServiceConfig config, int port)
    {
        var repos = AllJobRepos(config);
        if (repos.Count == 0)
            return (400, new { error = "No backup jobs configured." });
        lock (_lanServerLock)
        {
            if (_lanServer?.IsRunning == true)
                return (400, new { error = "LAN server is already running." });
            if (port is < 1 or > 65535)
                return (400, new { error = "Invalid port." });
            try
            {
                _lanServer?.Dispose();
                _lanServer = new LanServer(repos, port, "0.0.0.0");
                _lanServer.Start();
                foreach (string repo in repos)
                    OperationLog.Append(repo, "lan-server", $"started on port {port} ({repos.Count} repos)");
                return (200, new { running = true, port, repos = repos.Count });
            }
            catch (Exception ex)
            {
                return (500, new { error = ex.Message });
            }
        }
    }

    /// <summary>Stops the LAN chunk server.</summary>
    public static void StopLanServer()
    {
        try { _lanServer?.Dispose(); } catch { }
        _lanServer = null;
    }

    /// <summary>
    /// Runs the NNTP connectivity diagnostic synchronously (fast probes).
    /// </summary>
    public static (int Status, object Payload) RunDiagnose(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (400, new { error = "host is required." });
        var results = new List<object>();
        foreach (var (port, ssl, label) in new (int, bool, string)[]
                 { (119, false, "119/plain"), (563, true, "563/TLS"), (443, false, "443/plain") })
        {
            results.Add(new { port = label, result = ProbeNntp(host, port, ssl) });
        }
        bool anyOk = results.Any(r => ((string)r.GetType().GetProperty("result")!.GetValue(r)!)
            .StartsWith("OK", StringComparison.Ordinal));
        object? fallback = null;
        string diagnosis;
        if (anyOk)
        {
            diagnosis = $"{host} is reachable. Use port 563 (TLS) if your ISP interferes with plaintext NNTP.";
        }
        else
        {
            string fb = ProbeNntp("freenews.netfront.net", 119, false);
            fallback = new { host = "freenews.netfront.net:119", result = fb };
            diagnosis = fb.StartsWith("OK", StringComparison.Ordinal)
                ? $"Your network CAN reach NNTP (fallback answered), so the problem is specific to {host}."
                : "No NNTP server is reachable. Your ISP or firewall is likely blocking NNTP.";
        }
        return (200, new { host, probes = results, fallback, diagnosis });
    }

    private static string ProbeNntp(string host, int port, bool ssl)
    {
        try
        {
            using var client = new UsenetBackup.Core.Nntp.NntpClient(host, port, ssl);
            client.Connect();
            string g = client.Greeting ?? "";
            try { client.Quit(); } catch { }
            return $"OK ({(g.Length <= 60 ? g : g[..60] + "…")})";
        }
        catch (Exception ex)
        {
            string msg = ex.Message ?? "";
            if (ex is TimeoutException || msg.Contains("Timed out", StringComparison.OrdinalIgnoreCase))
                return "BLOCKED/TIMEOUT (ISP or firewall may be filtering this port)";
            if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase))
                return "CONNECTION REFUSED (port closed)";
            if (msg.Contains("No such host", StringComparison.OrdinalIgnoreCase))
                return "DNS FAILED";
            return $"FAILED ({(msg.Length <= 80 ? msg : msg[..80] + "…")})";
        }
    }

    /// <summary>
    /// Lists USB-attached physical drives (Windows only).
    /// </summary>
    public static (int Status, object Payload) ListUsbDrives()
    {
        if (!OperatingSystem.IsWindows())
            return (400, new { error = "USB drive listing requires Windows." });
        try
        {
            var drives = UsenetBackup.Core.Recovery.UsbDrives.List()
                .Select(d => new { number = d.Number, devicePath = d.DevicePath, model = d.Model, sizeBytes = d.SizeBytes })
                .ToArray();
            return (200, drives);
        }
        catch (Exception ex)
        {
            return (500, new { error = ex.Message });
        }
    }

    /// <summary>Returns (200, log lines) or (400, error payload).</summary>
    public static (int Status, object Payload) GetLog(ServiceConfig config, string repo, int lines = 100)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        string path = Path.Combine(repo, OperationLog.FileName);
        if (!File.Exists(path))
            return (200, Array.Empty<string>());
        string[] all = File.ReadAllLines(path);
        return (200, all.TakeLast(Math.Clamp(lines, 1, 1000)).ToArray());
    }

    /// <summary>
    /// Adds a new job or updates an existing one (matched by name, case-insensitive).
    /// Validates, saves to disk, and reloads the scheduler. Returns (200, job) or (400, error).
    /// </summary>
    public static (int Status, object Payload) UpsertJob(ServiceConfig config,
        BackupScheduler scheduler, string configPath, BackupJobConfig job,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(job.Name))
            return (400, new { error = "Job name is required." });
        // Default the repo to ProgramData when not specified: the repo is
        // FileKeep's internal storage and users shouldn't have to pick it.
        // ProgramData (not LocalAppData) because the service — not the user —
        // runs the backups; LocalAppData would resolve to the service account's
        // profile instead of the user's.
        if (string.IsNullOrWhiteSpace(job.Repo))
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            // Sanitize job name for use as a directory name.
            string safeName = string.Concat(job.Name.Split(Path.GetInvalidFileNameChars()));
            job.Repo = Path.Combine(programData, "FileKeep", "repos", safeName);
        }
        if (string.IsNullOrWhiteSpace(job.Source))
            return (400, new { error = "Source path is required." });
        // Validate schedule format and mode via a temporary config.
        var test = new ServiceConfig
        {
            DashboardPort = config.DashboardPort,
            DashboardBind = config.DashboardBind,
            Nntp = config.Nntp,
            Locations = config.Locations,
            Jobs = config.Jobs
                .Where(j => !j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { job }).ToList(),
        };
        try { test.Validate(); }
        catch (Exception ex) { return (400, new { error = ex.Message }); }
        // Scheduled jobs must upload to at least one Usenet or network
        // location. Local-only backups are only allowed as manual one-time
        // runs, never on a schedule. This is the server-side enforcement;
        // the dashboard form and the scheduler fail closed as well.
        if (job.EffectiveTargets.Count == 0)
            return (400, new { error = "Scheduled jobs must have at least one upload target (Usenet or network location)." });

        // Apply to the live config under the Jobs lock. Replace the object
        // rather than mutating in place: the scheduler may be running a job
        // with the old config object, and it must see a consistent snapshot.
        lock (config.JobsLock)
        {
            var existing = config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase));
            // Encrypt a supplied SMB password via DPAPI; blank keeps the stored blob.
            // Note: per-job SMB fields (SmbShare/SmbUser/SmbPasswordProtected) are
            // legacy — migrated to BackupLocations on config load. New jobs use
            // location names in Targets; credentials live on the location.
            string smbPwBlob = existing?.SmbPasswordProtected ?? "";
            if (!string.IsNullOrEmpty(job.SmbPasswordPlaintext))
                smbPwBlob = UsenetBackup.Core.Dpapi.Protect(job.SmbPasswordPlaintext) ?? "";
            job.SmbPasswordProtected = smbPwBlob;
            job.SmbPasswordPlaintext = null;
            if (existing is null)
                config.Jobs.Add(job);
            else
                config.Jobs[config.Jobs.IndexOf(existing)] = job;
        }
        try
        {
            config.Save(configPath);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: job '{job.Name}' saved");
            return (200, job);
        }
        catch (Exception ex)
        {
            return (400, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a job by name. Saves to disk and reloads the scheduler.
    /// Returns (200, {}) or (400, error).
    /// </summary>
    public static (int Status, object Payload) DeleteJob(ServiceConfig config,
        BackupScheduler scheduler, string configPath, string name,
        Action<string>? log = null)
    {
        lock (config.JobsLock)
        {
            var existing = config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return (400, new { error = $"Unknown job '{name}'." });
            if (config.Jobs.Count == 1)
                return (400, new { error = "Cannot delete the last backup job. The service requires at least one job." });
            config.Jobs.Remove(existing);
        }
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(config,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                });
            File.WriteAllText(configPath + ".tmp", json);
            File.Move(configPath + ".tmp", configPath, overwrite: true);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: job '{name}' deleted");
            return (200, new { });
        }
        catch (Exception ex)
        {
            return (400, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Adds or updates a backup location (matched by name, case-insensitive).
    /// Encrypts a supplied plaintext password via DPAPI; blank keeps the
    /// existing stored blob. Returns (200, location) or (400, error).
    /// </summary>
    public static (int Status, object Payload) UpsertLocation(ServiceConfig config,
        string configPath, BackupLocation loc, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(loc.Name))
            return (400, new { error = "Location name is required." });
        if (loc.Type != "nntp" && loc.Type != "smb")
            return (400, new { error = "Location type must be 'nntp' or 'smb'." });
        if (loc.Type == "nntp" && string.IsNullOrWhiteSpace(loc.Host))
            return (400, new { error = "Host is required for Usenet locations." });
        if (loc.Type == "smb" && string.IsNullOrWhiteSpace(loc.Share))
            return (400, new { error = "Share path is required for SMB locations." });

        var existing = config.Locations.FirstOrDefault(l =>
            l.Name.Equals(loc.Name, StringComparison.OrdinalIgnoreCase));
        // Encrypt supplied passwords; blank keeps the stored blob.
        string? pwBlob = existing?.PasswordProtected;
        if (!string.IsNullOrEmpty(loc.PasswordPlaintext))
            pwBlob = UsenetBackup.Core.Dpapi.Protect(loc.PasswordPlaintext);
        string? smbPwBlob = existing?.SmbPasswordProtected;
        if (!string.IsNullOrEmpty(loc.SmbPasswordPlaintext))
            smbPwBlob = UsenetBackup.Core.Dpapi.Protect(loc.SmbPasswordPlaintext);
        loc.PasswordProtected = pwBlob;
        loc.SmbPasswordProtected = smbPwBlob;
        loc.PasswordPlaintext = null;
        loc.SmbPasswordPlaintext = null;

        if (existing is null)
            config.Locations.Add(loc);
        else
        {
            existing.Type = loc.Type;
            existing.Host = loc.Host; existing.Port = loc.Port;
            existing.Ssl = loc.Ssl; existing.Username = loc.Username;
            existing.Connections = loc.Connections;
            existing.Newsgroup = loc.Newsgroup;
            existing.RetentionDays = loc.RetentionDays;
            existing.PasswordProtected = pwBlob;
            existing.Share = loc.Share; existing.SmbUser = loc.SmbUser;
            existing.SmbPasswordProtected = smbPwBlob;
        }
        try
        {
            config.Save(configPath);
            log?.Invoke($"dashboard: location '{loc.Name}' saved");
            return (200, loc);
        }
        catch (Exception ex) { return (400, new { error = ex.Message }); }
    }

    /// <summary>
    /// Deletes a location by name. Refuses if any job still targets it.
    /// Returns (200, {}) or (400, error).
    /// </summary>
    public static (int Status, object Payload) DeleteLocation(ServiceConfig config,
        string configPath, string name, Action<string>? log = null)
    {
        var existing = config.Locations.FirstOrDefault(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return (400, new { error = $"Unknown location '{name}'." });
        var usedBy = config.SnapshotJobs()
            .Where(j => j.EffectiveTargets.Any(t =>
                t.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(j => j.Name).ToList();
        if (usedBy.Count > 0)
            return (400, new { error =
                $"Location '{name}' is used by job(s): {string.Join(", ", usedBy)}. " +
                "Remove it from those jobs first." });
        config.Locations.Remove(existing);
        try
        {
            config.Save(configPath);
            log?.Invoke($"dashboard: location '{name}' deleted");
            return (200, new { });
        }
        catch (Exception ex) { return (400, new { error = ex.Message }); }
    }

    /// <summary>
    /// Tests connectivity to a location: NNTP connect+auth, or SMB share
    /// reachability. Returns (200, { ok, message }) or (400, error).
    /// </summary>
    public static (int Status, object Payload) TestLocation(ServiceConfig config, string name)
    {
        var loc = config.Locations.FirstOrDefault(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (loc is null)
            return (400, new { error = $"Unknown location '{name}'." });
        try
        {
            if (loc.Type == "nntp")
            {
                using var client = new UsenetBackup.Core.Nntp.NntpClient(loc.Host, loc.Port, loc.Ssl);
                client.Connect();
                string? pw = Environment.GetEnvironmentVariable("USENETBACKUP_NNTP_PASSWORD");
                if (string.IsNullOrEmpty(pw) && !string.IsNullOrEmpty(loc.PasswordProtected))
                    pw = UsenetBackup.Core.Dpapi.Unprotect(loc.PasswordProtected);
                if (!string.IsNullOrEmpty(loc.Username))
                    client.Authenticate(loc.Username, pw ?? "");
                return (200, new { ok = true, message = $"Connected to {loc.Host}:{loc.Port}." });
            }
            else
            {
                string? pw = Environment.GetEnvironmentVariable("USENETBACKUP_SMB_PASSWORD");
                if (string.IsNullOrEmpty(pw) && !string.IsNullOrEmpty(loc.SmbPasswordProtected))
                    pw = UsenetBackup.Core.Dpapi.Unprotect(loc.SmbPasswordProtected);
                using var share = UsenetBackup.Core.SmbShare.Connect(loc.Share, loc.SmbUser, pw);
                return (200, new { ok = true, message = $"Share {loc.Share} is reachable." });
            }
        }
        catch (Exception ex)
        {
            return (200, new { ok = false, message = ex.Message });
        }
    }

    public static (int Status, object Payload) UpdateNntp(ServiceConfig config,
        BackupScheduler scheduler, string configPath, NntpConfig nntp,
        Action<string>? log = null)
    {
        // Empty host clears the Usenet configuration.
        if (string.IsNullOrWhiteSpace(nntp.Host))
        {
            config.Nntp = null;
        }
        else
        {
            // If a new plaintext password was supplied, encrypt it via DPAPI.
            // If blank, keep the existing stored blob (the UI never receives the password).
            string? blobToKeep = config.Nntp?.PasswordProtected;
            if (!string.IsNullOrEmpty(nntp.PasswordPlaintext))
                blobToKeep = Dpapi.Protect(nntp.PasswordPlaintext);
            nntp.PasswordProtected = blobToKeep;
            nntp.PasswordPlaintext = null; // never persist plaintext
            config.Nntp = nntp;
        }
        try
        {
            // Validate NNTP fields if a host is set; Validate() also checks that
            // no job has auto-upload without a provider.
            config.Validate();
            // Sync the migrated "Usenet" location with the new settings so
            // scheduled uploads (which now use locations) don't go stale.
            var usenetLoc = config.Locations.FirstOrDefault(l =>
                l.Type == "nntp" && l.Name.Equals("Usenet", StringComparison.OrdinalIgnoreCase));
            if (usenetLoc is not null && config.Nntp is not null)
            {
                usenetLoc.Host = config.Nntp.Host;
                usenetLoc.Port = config.Nntp.Port;
                usenetLoc.Ssl = config.Nntp.Ssl;
                usenetLoc.Username = config.Nntp.Username;
                usenetLoc.Connections = config.Nntp.Connections;
                usenetLoc.Newsgroup = config.Nntp.Newsgroup;
                usenetLoc.RetentionDays = config.Nntp.RetentionDays;
                usenetLoc.PasswordProtected = config.Nntp.PasswordProtected;
                usenetLoc.RedundancyMode = config.Nntp.RedundancyMode;
            }
            config.Save(configPath);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: Usenet provider {(config.Nntp is null ? "cleared" : $"set to {config.Nntp.Host}")}");
            // Never return the encrypted blob to the UI; just report whether one is stored.
            object safe = config.Nntp is null
                ? new { hasPassword = false }
                : new
                {
                    host = config.Nntp.Host,
                    port = config.Nntp.Port,
                    username = config.Nntp.Username,
                    ssl = config.Nntp.Ssl,
                    connections = config.Nntp.Connections,
                    hasPassword = config.Nntp.HasPassword,
                };
            return (200, safe);
        }
        catch (Exception ex)
        {
            return (400, new { error = ex.Message });
        }
    }

    public static (int Status, object Payload) UpdateNntpProviders(ServiceConfig config,
        BackupScheduler scheduler, string configPath, List<NntpConfig> providers,
        Action<string>? log = null)
    {
        // Validate each provider
        foreach (var p in providers)
        {
            if (string.IsNullOrWhiteSpace(p.Host))
                return (400, new { error = "Provider host is required." });
            if (p.Port is < 1 or > 65535)
                return (400, new { error = $"Port {p.Port} is out of range." });
            if (p.Connections is < 1 or > 100)
                return (400, new { error = $"Connections must be 1-100 (got {p.Connections})." });
        }
        // Encrypt supplied plaintext passwords via DPAPI; blank keeps the stored blob.
        // Match against existing providers by host+port to preserve blobs.
        foreach (var p in providers)
        {
            var existing = config.NntpProviders.FirstOrDefault(e =>
                e.Host.Equals(p.Host, StringComparison.OrdinalIgnoreCase) && e.Port == p.Port);
            string? blobToKeep = existing?.PasswordProtected;
            if (!string.IsNullOrEmpty(p.PasswordPlaintext))
                blobToKeep = Dpapi.Protect(p.PasswordPlaintext);
            p.PasswordProtected = blobToKeep;
            p.PasswordPlaintext = null;
        }
        config.NntpProviders = providers;
        try
        {
            config.Validate();
            config.Save(configPath);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: {providers.Count} Usenet provider(s) saved");
            return (200, new { count = providers.Count });
        }
        catch (Exception ex)
        {
            return (400, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reports whether a repository passphrase is available (env var or
    /// stored) and how it's protected. Never returns the passphrase itself.
    /// </summary>
    public static (int Status, object Payload) GetPassphraseStatus(ServiceConfig config)
    {
        bool fromEnv = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar));
        bool stored = !string.IsNullOrEmpty(config.PassphraseProtected);
        return (200, new
        {
            configured = fromEnv || stored,
            source = fromEnv ? "environment" : stored ? "stored" : "none",
            yubikey = stored && config.PassphraseYubiKey,
        });
    }

    /// <summary>
    /// Stores (or clears) the repository passphrase.
    /// A null/empty passphrase clears the stored value and disables YubiKey mode.
    /// With <paramref name="useYubiKey"/>, the key's OTP slot is programmed
    /// (two touches: program + wrap) and the passphrase is stored wrapped
    /// under the challenge-response-derived KEK; a DPAPI copy is still kept
    /// so scheduled backups run unattended. Restores then require a touch
    /// to open a session.
    /// </summary>
    public static (int Status, object Payload) SetPassphrase(ServiceConfig config,
        string configPath, string? passphrase, bool useYubiKey,
        IYubiKeyProvider? yubiKeyProvider, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            config.PassphraseProtected = null;
            config.PassphraseYubiKey = false;
            config.YubiKeyChallenge = null;
            config.PassphraseYubiKeyWrapped = null;
        }
        else if (useYubiKey)
        {
            if (yubiKeyProvider is null)
                return (400, new { error = "YubiKey support is unavailable in this build." });
            try
            {
                var (challenge, wrapped) = YubiKeySetup.Setup(
                    yubiKeyProvider, passphrase, config.YubiKeySlot);
                config.PassphraseProtected = Dpapi.Protect(passphrase);
                config.YubiKeyChallenge = challenge;
                config.PassphraseYubiKeyWrapped = wrapped;
                config.PassphraseYubiKey = true;
            }
            catch (InvalidOperationException ex)
            {
                return (400, new { error = ex.Message });
            }
            catch (TimeoutException)
            {
                return (400, new { error = "Timed out waiting for a YubiKey touch. Try again and touch the key when it flashes." });
            }
            catch (Exception ex)
            {
                return (400, new { error = "YubiKey setup failed: " + ex.Message });
            }
        }
        else
        {
            config.PassphraseProtected = Dpapi.Protect(passphrase);
            config.PassphraseYubiKey = false;
            config.YubiKeyChallenge = null;
            config.PassphraseYubiKeyWrapped = null;
        }
        try
        {
            config.Save(configPath);
            log?.Invoke("dashboard: repository passphrase " +
                (string.IsNullOrEmpty(passphrase) ? "cleared" :
                 useYubiKey ? "saved (DPAPI + YubiKey-wrapped)" : "saved (DPAPI-encrypted)"));
            return (200, new { ok = true, yubikey = useYubiKey && !string.IsNullOrEmpty(passphrase) });
        }
        catch (Exception ex)
        {
            return (400, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Lists attached YubiKeys for the setup UI.
    /// </summary>
    public static (int Status, object Payload) GetYubiKeyStatus(IYubiKeyProvider? provider)
    {
        if (provider is null)
            return (200, new { detected = false, devices = Array.Empty<string>() });
        IReadOnlyList<YubiKeyInfo> devices;
        try { devices = provider.FindDevices(); }
        catch { devices = Array.Empty<YubiKeyInfo>(); }
        return (200, new
        {
            detected = devices.Count > 0,
            devices = devices.Select(d => d.Description).ToArray(),
        });
    }

    /// <summary>
    /// Opens a restore session: touches the YubiKey, unwraps the passphrase
    /// and holds it in <paramref name="session"/> memory. Only meaningful
    /// when the config has YubiKey mode enabled.
    /// </summary>
    public static (int Status, object Payload) UnlockRestoreSession(ServiceConfig config,
        RestoreSession session, IYubiKeyProvider? provider, Action<string>? log = null)
    {
        if (!config.PassphraseYubiKey)
            return (400, new { error = "YubiKey mode is not enabled for this passphrase." });
        if (string.IsNullOrEmpty(config.YubiKeyChallenge) ||
            string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped))
            return (400, new { error = "YubiKey was not set up completely. Re-save the passphrase with YubiKey enabled." });
        if (provider is null)
            return (400, new { error = "YubiKey support is unavailable in this build." });
        try
        {
            string passphrase = YubiKeySetup.Unlock(provider,
                config.YubiKeyChallenge, config.PassphraseYubiKeyWrapped, config.YubiKeySlot);
            session.Unlock(passphrase);
            log?.Invoke("dashboard: restore session unlocked via YubiKey");
            return (200, new { ok = true });
        }
        catch (TimeoutException)
        {
            return (400, new { error = "Timed out waiting for a YubiKey touch. Try again and touch the key when it flashes." });
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (400, new { error = "The YubiKey touch succeeded but the passphrase did not decrypt. Wrong key, or the slot was reprogrammed after setup." });
        }
        catch (InvalidOperationException ex)
        {
            return (400, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return (400, new { error = "Unlock failed: " + ex.Message });
        }
    }

    /// <summary>Locks the restore session, wiping the held passphrase.</summary>
    public static (int Status, object Payload) LockRestoreSession(RestoreSession session)
    {
        session.Lock();
        return (200, new { ok = true });
    }

    /// <summary>Reports whether YubiKey mode is on and the session is unlocked.</summary>
    public static (int Status, object Payload) GetRestoreSessionStatus(
        ServiceConfig config, RestoreSession session) =>
        (200, new { yubikey = config.PassphraseYubiKey, unlocked = session.IsUnlocked });

    /// <summary>
    /// Resolves the passphrase for restore-side operations. When YubiKey
    /// mode is on, only the unlocked session passphrase is used (null when
    /// locked); otherwise the environment/DPAPI value. Backup-creation
    /// endpoints use <see cref="BackupScheduler.GetPassphrase"/> directly
    /// and never consult the session.
    /// </summary>
    public static string? GetRestorePassphrase(ServiceConfig config, RestoreSession? session) =>
        config.PassphraseYubiKey ? session?.GetPassphrase() : BackupScheduler.GetPassphrase(config);
}

public static class DashboardHtml
{
    /// <summary>
    /// The {{CSRF_TOKEN}} placeholder is replaced with the per-startup token;
    /// the page is unreadable cross-origin, so only the operator's browser
    /// (and localhost) can learn it.
    /// </summary>
    public static string Page(string csrfToken) =>
        RawPage.Replace("{{CSRF_TOKEN}}", csrfToken, StringComparison.Ordinal);

    private static string RawPage => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="csrf-token" content="{{CSRF_TOKEN}}">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>FileKeep</title>
        <style>
          /* Windows 11 Settings page styling: Segoe UI Variable, cards, accent button. */
          :root {
            --accent: #0067c0;
            --accent-hover: #1972c2;
            --bg: #f3f3f3;
            --card: #ffffff;
            --border: #e6e6e6;
            --text: #1b1b1b;
            --text-2: #616161;
            --ok: #107c10;
            --bad: #c42b1c;
          }
          /* Toast notifications: replace alert() popups with inline feedback. */
          #toast-container { position: fixed; bottom: 24px; right: 24px; z-index: 9999; display: flex; flex-direction: column; gap: 8px; }
          .toast { padding: 12px 16px; border-radius: 6px; background: #323232; color: white; font-size: 14px; box-shadow: 0 4px 12px rgba(0,0,0,0.25); max-width: 380px; animation: toast-in 0.2s ease-out; }
          .toast.ok { background: #107c10; }
          .toast.err { background: #c42b1c; }
          @keyframes toast-in { from { opacity: 0; transform: translateY(8px); } to { opacity: 1; transform: none; } }
          /* Invalid field highlighting. */
          input.invalid, select.invalid { border-color: var(--bad) !important; background: #fdf0ef; }
          #jf-location-list.invalid { outline: 2px solid var(--bad); border-radius: 6px; padding: 6px; }
          /* Folder picker row: text input + Browse button side by side. */
          .folder-row { display: flex; gap: 6px; align-items: center; }
          .folder-row input { flex: 1; min-width: 0; }
          .browse-btn { white-space: nowrap; }
          * { box-sizing: border-box; }
          body {
            font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
            background: var(--bg); color: var(--text);
            margin: 0; padding: 0; font-size: 14px;
          }
          .app { display: flex; min-height: 100vh; }
          /* Left nav, Settings-style (only our own real sections — no fake entries). */
          nav { width: 300px; flex-shrink: 0; padding: 24px 12px 24px 20px; }
          .nav-brand { display: flex; align-items: center; gap: 12px; padding: 4px 12px 20px; }
          .nav-brand .glyph {
            width: 40px; height: 40px; border-radius: 50%;
            background: var(--accent); color: #fff;
            display: flex; align-items: center; justify-content: center;
            font-size: 20px;
          }
          .nav-brand .t1 { font-weight: 600; }
          .nav-brand .t2 { font-size: 12px; color: var(--text-2); }
          .nav-item {
            display: block; padding: 9px 12px; margin: 2px 0; border-radius: 6px;
            color: var(--text); text-decoration: none; position: relative; cursor: pointer;
          }
          .nav-item:hover { background: #fafafa; }
          .nav-item.active { background: #fafafa; font-weight: 600; }
          .nav-item.active::before {
            content: ""; position: absolute; left: -12px; top: 8px; bottom: 8px; width: 3px;
            border-radius: 2px; background: var(--accent);
          }
          main { flex: 1; padding: 32px 40px 60px 12px; max-width: 960px; }
          h1 { font-size: 28px; font-weight: 600; margin: 0 0 20px; }
          h2 { font-size: 16px; font-weight: 600; margin: 28px 0 12px; }
          .card {
            background: var(--card); border: 1px solid var(--border);
            border-radius: 8px; padding: 20px; margin: 0 0 12px;
          }
          /* Modal dialog overlay. */
          .modal {
            position: fixed; inset: 0; z-index: 100;
            background: rgba(0,0,0,0.45);
            display: flex; align-items: center; justify-content: center;
          }
          .modal-box {
            background: var(--card); border: 1px solid var(--border);
            border-radius: 8px; padding: 24px; width: 520px; max-width: 90vw;
            max-height: 85vh; overflow-y: auto;
          }
          /* Status hero, Windows Update style. */
          .hero { display: flex; align-items: center; gap: 20px; }
          .hero-icon {
            width: 56px; height: 56px; border-radius: 50%; flex-shrink: 0;
            display: flex; align-items: center; justify-content: center;
            font-size: 28px; color: #fff; background: var(--ok);
          }
          .hero-icon.warn { background: #ca5010; }
          .hero-icon.bad { background: var(--bad); }
          .hero-title { font-size: 18px; font-weight: 600; }
          .hero-sub { color: var(--text-2); margin-top: 4px; }
          .hero button { margin-left: auto; flex-shrink: 0; }
          .row { display: flex; align-items: center; gap: 12px; padding: 12px 0; border-top: 1px solid var(--border); }
          .row:first-of-type { border-top: none; }
          .row .grow { flex: 1; }
          .row .name { font-weight: 600; }
          .row .meta { color: var(--text-2); font-size: 13px; margin-top: 2px; }
          .pill {
            font-size: 12px; padding: 2px 10px; border-radius: 10px; font-weight: 600;
            background: #e8f5e9; color: var(--ok);
          }
          .pill.fail { background: #fdecea; color: var(--bad); }
          .pill.idle { background: #f0f0f0; color: var(--text-2); }
          button {
            font-family: inherit; font-size: 14px;
            padding: 6px 18px; border-radius: 4px; cursor: pointer;
            border: 1px solid #d1d1d1; background: #fbfbfb; color: var(--text);
          }
          button:hover { background: #f0f0f0; }
          button.accent { background: var(--accent); border-color: var(--accent); color: #fff; }
          button.accent:hover { background: var(--accent-hover); border-color: var(--accent-hover); }
          button.danger { background: #c42b1c; border-color: #c42b1c; color: #fff; }
          button.danger:hover { background: #a92418; border-color: #a92418; }
          button:disabled { opacity: .5; cursor: default; }
          select {
            font-family: inherit; font-size: 14px; padding: 6px 10px;
            border: 1px solid #d1d1d1; border-radius: 4px; background: #fbfbfb;
            max-width: 420px;
          }
          table { border-collapse: collapse; width: 100%; font-size: 13px; }
          th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--border); }
          th { color: var(--text-2); font-weight: 600; }
          code { font-size: 12px; }
          pre {
            background: #fafafa; border: 1px solid var(--border); border-radius: 6px;
            padding: 12px; overflow: auto; font-size: 12px; max-height: 320px; margin: 0;
          }
          .view { display: none; }
          .view.active { display: block; }
          .foot { color: var(--text-2); font-size: 12px; margin-top: 32px; }
          /* Settings form */
          .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px 16px; }
          .form-grid label { display: flex; flex-direction: column; gap: 6px; font-weight: 600; font-size: 13px; }
          .form-grid label.check { flex-direction: row; align-items: center; font-weight: 400; }
          .form-grid input[type=text], .form-grid input[type=time], .form-grid input[type=number] {
            font-family: inherit; font-size: 14px; padding: 6px 10px;
            border: 1px solid #d1d1d1; border-radius: 4px; background: #fbfbfb; color: var(--text);
          }
          .form-grid input[type=checkbox] { width: 16px; height: 16px; }
          /* Dark mode: follow the OS color scheme, Windows 11 dark palette. */
          @media (prefers-color-scheme: dark) {
            :root {
              color-scheme: dark;
              --bg: #202020;
              --card: #2b2b2b;
              --border: #353535;
              --text: #f3f3f3;
              --text-2: #a7a7a7;
              --ok: #6ccb5f;
              --bad: #f1707b;
            }
            .nav-item:hover, .nav-item.active { background: #2d2d2d; }
            button {
              background: #2d2d2d; border-color: #3a3a3a; color: var(--text);
            }
            button:hover { background: #323232; }
            select { background: #2d2d2d; border-color: #3a3a3a; color: var(--text); }
            pre { background: #242424; }
            .pill { background: #1d3325; }
            .pill.fail { background: #3a2320; }
            .pill.idle { background: #2d2d2d; }
            .form-grid input[type=text], .form-grid input[type=time], .form-grid input[type=number] {
              background: #2d2d2d; border-color: #3a3a3a; color: var(--text);
            }
            /* Toasts stay readable on dark surfaces. */
            .toast { background: #3a3a3a; border: 1px solid #4d4d4d; }
            .toast.ok { background: #0f7a0f; }
            .toast.err { background: #a4262c; }
            /* Invalid fields: dark red tint instead of light pink. */
            input.invalid, select.invalid { background: #3a1d1d; }
            .hero-icon.warn { background: #b34a0e; }
            button.danger { background: #a4262c; border-color: #a4262c; }
            button.danger:hover { background: #8f2026; border-color: #8f2026; }
          }
        </style>
        </head>
        <body>
        <div class="app">
          <nav>
            <div class="nav-brand">
              <div class="glyph">⛁</div>
              <div><div class="t1">FileKeep</div><div class="t2">Service settings</div></div>
            </div>
            <a class="nav-item active" data-view="overview">Overview</a>
            <a class="nav-item" data-view="backups">Backups</a>
            <a class="nav-item" data-view="recovery">Recovery</a>
            <a class="nav-item" data-view="tools">Tools</a>
            <a class="nav-item" data-view="log">Operations log</a>
            <a class="nav-item" data-view="settings">Settings</a>
          </nav>
          <main>
            <div class="view active" id="view-overview">
              <h1>Backup (Usenet)</h1>
              <div class="card hero">
                <div class="hero-icon" id="heroIcon">✓</div>
                <div>
                  <div class="hero-title" id="heroTitle">Checking…</div>
                  <div class="hero-sub" id="heroSub"></div>
                </div>
                <button class="accent" id="backupNow" onclick="runAll()">Back up now</button>
                <button id="oneTimeBtn" onclick="showOneTimeForm()" title="Run a single backup to a local folder you choose. The only way to back up locally — one-time backups are never scheduled.">One-time backup…</button>
              </div>
              <div class="card" id="oneTimeCard" style="display:none">
                <div class="name" style="font-size:16px;font-weight:600">One-time backup</div>
                <div class="meta" style="color:var(--text-2);margin:4px 0 8px">Back up once to a local folder you pick. Nothing is scheduled and no backup job is created.</div>
                <div class="form-grid">
                  <label title="The folder to back up right now. Click Browse to pick it.">Source folder<span class="folder-row"><input id="ot-source" type="text" placeholder="Choose a folder to back up"><button type="button" class="browse-btn" onclick="browseFolder('ot-source', 'Select source folder')">Browse…</button></span></label>
                  <label title="Where to save this one-time backup. A new encrypted repository is created here if there isn't one already.">Destination folder<span class="folder-row"><input id="ot-dest" type="text" placeholder="Choose where to save it"><button type="button" class="browse-btn" onclick="browseFolder('ot-dest', 'Select destination folder')">Browse…</button></span></label>
                </div>
                <div id="ot-error" style="color:var(--bad);margin:8px 0;display:none"></div>
                <div style="margin-top:12px;display:flex;gap:8px">
                  <button class="accent" onclick="runOneTimeBackup()">Run backup</button>
                  <button onclick="hideOneTimeForm()">Cancel</button>
                </div>
              </div>
              <div class="card" id="setupCard" style="display:none">
                <div class="name" style="font-size:16px;font-weight:600">Get set up</div>
                <div class="meta" style="color:var(--text-2);margin:4px 0 4px">Three steps to your first backup.</div>
                <div id="setupSteps"></div>
              </div>
              <h2>Backup jobs</h2>
              <div class="card" id="jobs" style="padding-top:8px"><p style="color:var(--text-2)">Loading…</p></div>
              <div class="card" id="uploadProgress" style="display:none; margin-top:12px">
                <div class="row">
                  <div class="grow">
                    <div class="name" id="upTitle">Uploading…</div>
                    <div class="meta" id="upMeta"></div>
                  </div>
                  <div class="name" id="upSpeed" style="white-space:nowrap"></div>
                </div>
                <div style="background:var(--bg-2); border-radius:4px; height:8px; margin-top:8px; overflow:hidden">
                  <div id="upBar" style="background:var(--accent); height:100%; width:0%; transition:width 0.5s"></div>
                </div>
                <div class="meta" id="upEta" style="margin-top:4px"></div>
              </div>
              <h2>Usenet expiration warnings</h2>
              <div class="card" id="expiration" style="padding-top:8px"><p style="color:var(--text-2)">Loading…</p></div>
              <div class="foot">Backups are encrypted locally and posted to Usenet. Your passphrase never leaves this machine's memory.</div>
            </div>
            <div class="view" id="view-backups">
              <h1>Backups</h1>
              <div class="card">
                <div class="row">
                  <div class="grow">
                    <div class="name">Repository</div>
                    <div class="meta">Only repos from configured backup jobs are listed.</div>
                  </div>
                  <select id="repo"></select>
                  <button onclick="loadRepo()">Refresh</button>
                </div>
              </div>
              <div class="card" style="padding:8px 20px">
                <div class="row" id="restore-session-row" style="display:none;margin-bottom:8px">
                  <span id="restore-session-status" style="color:var(--text-2)">Restore session: locked</span>
                  <button onclick="lockRestoreSession()" title="Wipe the session passphrase from memory">Lock</button>
                </div>
                <table id="backups"><thead><tr><th>ID</th><th>Type</th><th>Created (UTC)</th><th>Actions</th></tr></thead><tbody></tbody></table>
              </div>
              <div class="card">
                <div class="name">Back up now</div>
                <div class="meta">Ad-hoc backup into the selected repository, outside the schedule.</div>
                <div class="row" style="margin-top:8px">
                  <label style="display:flex;align-items:center;gap:6px">
                    <input type="radio" name="backupnow-type" value="files" checked onchange="backupNowTypeChanged()"> Files &amp; folders
                  </label>
                  <label style="display:flex;align-items:center;gap:6px">
                    <input type="radio" name="backupnow-type" value="disk" onchange="backupNowTypeChanged()"> Whole disk
                  </label>
                </div>
                <div class="row" style="margin-top:8px" id="backupnow-files-row">
                  <span class="folder-row" style="flex:1"><input id="adhoc-source" placeholder="C:\Users\You\Documents" style="flex:1"><button type="button" class="browse-btn" onclick="browseFolder('adhoc-source', 'Select source folder')">Browse…</button></span>
                  <button onclick="startAdhocBackup()">Back up now</button>
                </div>
                <div id="backupnow-disk-row" style="display:none">
                  <div class="row" style="margin-top:8px">
                    <select id="disk-device" style="flex:1"><option value="">Select a disk…</option></select>
                    <button type="button" onclick="refreshDiskList()" title="Rescan disks">↻</button>
                    <button onclick="startDiskBackup()">Back up disk</button>
                  </div>
                  <div class="meta">Images the entire disk bit-for-bit. The image name is generated automatically. Use the Restore button on the disk-image backup above to write it back.</div>
                </div>
              </div>
              <div id="restoreModal" class="modal" style="display:none">
                <div class="modal-box">
                  <h2 style="margin-top:0" id="restoreModalTitle">Restore backup</h2>
                  <div class="meta" id="restoreModalSub"></div>
                  <div id="restoreFilesForm">
                    <div class="row" style="margin-top:8px">
                      <span class="folder-row" style="flex:1"><input id="m-restore-dest" placeholder="C:\Restore" style="flex:1"><button type="button" class="browse-btn" onclick="browseFolder('m-restore-dest', 'Select restore destination')">Browse…</button></span>
                    </div>
                    <div class="row" style="margin-top:8px">
                      <label>If a file already exists:
                        <select id="m-restore-policy" title="What to do when the destination already has the file">
                          <option value="skip" selected>Skip files that differ (recommended)</option>
                          <option value="overwrite">Overwrite with the backup's copies</option>
                          <option value="newer">Keep whichever file is newer</option>
                        </select>
                      </label>
                    </div>
                    <div class="row" style="margin-top:8px">
                      <input id="m-restore-smb" placeholder="SMB share (optional, e.g. \\NAS\backups)" style="flex:1">
                      <input id="m-restore-smb-user" placeholder="Username (optional)" style="width:160px">
                    </div>
                    <div class="meta">If chunks are missing locally, enter the SMB share to pull them from (uses the job's saved SMB password).</div>
                  </div>
                  <div id="restoreDiskForm" style="display:none">
                    <div class="row" style="margin-top:8px">
                      <select id="m-disk-target" style="flex:1"><option value="">Select target disk…</option></select>
                      <button type="button" onclick="refreshDiskList()" title="Rescan disks">↻</button>
                    </div>
                    <div class="row" style="margin-top:8px">
                      <label class="check" title="Required: acknowledges the destructive restore"><input id="m-disk-ack" type="checkbox"> I understand this DESTROYS all data on the selected disk.</label>
                    </div>
                    <div class="meta" style="color:var(--bad)">Destructive — all data on the target device will be destroyed. The disk running Windows is never listed here.</div>
                  </div>
                  <div style="margin-top:12px;display:flex;gap:8px;justify-content:flex-end">
                    <button onclick="closeRestoreModal()">Cancel</button>
                    <button class="accent" id="restoreModalGo" onclick="submitRestore()">Restore</button>
                  </div>
                </div>
              </div>
              <div class="card">
                <h2 style="margin-top:0">Backup destinations</h2>
                <p style="color:var(--text-2);margin-top:0">Everywhere backup data is kept — the local repository plus all configured Usenet providers and network shares.</p>
                <div id="destinations-list"><p style="color:var(--text-2)">Loading…</p></div>
              </div>
              <div class="card">
                <div class="row">
                  <div class="grow">
                    <div class="name">Discover from Usenet</div>
                    <div class="meta">Find backup manifests posted to Usenet that aren't in this repo yet.</div>
                  </div>
                  <button onclick="discoverRemote()">Discover</button>
                </div>
                <div id="remoteBackups" style="margin-top:8px"></div>
              </div>
            </div>
            <div class="view" id="view-tools">
              <h1>Tools</h1>
              <div class="card">
                <div class="name">Retention check</div>
                <div class="meta">STAT-sample articles against provider retention; repost aging ones with fresh IDs. Runs across every configured repository.</div>
                <div class="row" style="margin-top:8px">
                  <label class="check"><input id="op-dryrun" type="checkbox" checked> Dry run</label>
                  <button onclick="runRetentionCheck()">Run retention check</button>
                </div>
              </div>
              <div class="card">
                <div class="name">Download from NZB</div>
                <div class="meta">Fetch chunks referenced by an NZB file from Usenet into the first configured repository.</div>
                <div class="row" style="margin-top:8px">
                  <input id="nzb-file" type="file" accept=".nzb">
                  <button onclick="uploadNzb()">Download chunks</button>
                </div>
              </div>
              <div class="card">
                <div class="name">Sync to SMB share</div>
                <div class="meta">Copy every repository's chunks and manifests to a network share (e.g. a NAS). Idempotent — files already on the share are skipped.</div>
                <div class="row" style="margin-top:8px">
                  <input id="smb-share" placeholder="\\\\NAS\\backups\\filekeep" style="flex:1">
                  <input id="smb-user" placeholder="Username (optional)" style="width:160px">
                  <button onclick="startSmbSync()">Sync now</button>
                </div>
                <div class="meta">Password: uses the job's saved SMB password, or set USENETBACKUP_SMB_PASSWORD.</div>
              </div>
              <div class="card">
                <div class="name">LAN server</div>
                <div class="meta">Serve every configured repository's chunks over HTTP for LAN restores (e.g. from WinRE recovery).</div>
                <div class="row" style="margin-top:8px">
                  <input id="lan-port" type="number" value="8477" min="1" max="65535" style="width:100px" title="Port for the LAN server">
                  <button onclick="startLanServer()">Start server</button>
                  <button onclick="stopLanServer()">Stop</button>
                  <span id="lan-status" style="color:var(--text-2)"></span>
                </div>
              </div>
            </div>
                        <div class="view" id="view-recovery">
              <h1>Recovery</h1>
              <div class="card">
                <div class="name">Build WinRE recovery media (ADK-free)</div>
                <div class="meta">Create bootable FileKeep recovery media directly from the host's WinRE image. No ADK required. Requires Administrator rights.</div>
                <div class="row" style="margin-top:8px">
                  <button onclick="checkWinRePrereqs()">Check prerequisites</button>
                  <span id="winre-prereq" style="margin-left:8px"></span>
                </div>
                <div class="row" style="margin-top:8px">
                  <label style="display:flex;align-items:center;gap:6px">
                    <input type="radio" name="winre-mode" value="usb" checked onchange="winreModeChanged()"> Write to USB
                  </label>
                  <label style="display:flex;align-items:center;gap:6px">
                    <input type="radio" name="winre-mode" value="image" onchange="winreModeChanged()"> Save disk image (.vhdx)
                  </label>
                </div>
                <div class="row" style="margin-top:8px" id="winre-usb-row">
                  <select id="winre-drive" style="width:280px"><option value="">Select USB drive…</option></select>
                  <button type="button" onclick="refreshWinReDrives()" title="Rescan for USB drives">↻</button>
                  <span class="folder-row" style="flex:1"><input id="winre-src" placeholder="Source dir (optional)" style="flex:1"><button type="button" class="browse-btn" onclick="browseFolder('winre-src', 'Select source folder')">Browse…</button></span>
                  <button class="danger" onclick="startWinReBuild()">Build USB</button>
                </div>
                <div class="row" style="margin-top:8px;display:none" id="winre-image-row">
                  <span class="folder-row" style="flex:1"><input id="winre-image" placeholder="Full path to the .vhdx file, e.g. D:\images\filekeep-recovery.vhdx" title="Type the full file path including the file name, or pick one with Browse… A folder alone also works — the file is saved there as filekeep-recovery.vhdx." style="flex:1"><button type="button" class="browse-btn" onclick="browseSaveFile('winre-image', 'Save recovery image as', 'VHDX image (*.vhdx)|*.vhdx', 'filekeep-recovery.vhdx')">Browse…</button></span>
                  <label title="Maximum size of the expandable .vhdx image, in megabytes.">Size (MB): <input id="winre-imgsize" type="number" value="4096" min="1024" step="1024" style="width:90px"></label>
                  <span class="folder-row" style="flex:1"><input id="winre-src-img" placeholder="Source dir (optional)" style="flex:1"><button type="button" class="browse-btn" onclick="browseFolder('winre-src-img', 'Select source folder')">Browse…</button></span>
                  <button onclick="startWinReBuild()">Build image</button>
                </div>
                <div class="row" style="margin-top:8px">
                  <label>Boot mode:
                    <select id="winre-bootmode">
                      <option value="Both" selected>Both (UEFI + legacy BIOS)</option>
                      <option value="UEFI">UEFI only</option>
                      <option value="BIOS">Legacy BIOS only</option>
                    </select>
                  </label>
                </div>
                <div class="meta" id="winre-mode-hint" style="margin-top:4px">Destructive — the USB drive is formatted. All data on it is destroyed.</div>
                <div class="row" style="margin-top:8px">
                  <button onclick="checkWinReStatus()">Check build status</button>
                  <button id="winre-cancel" onclick="cancelWinReBuild()" style="display:none;margin-left:8px">Cancel build</button>
                  <span id="winre-status" style="margin-left:8px"></span>
                </div>
              </div>
            </div>
            <div class="view" id="view-log">
              <h1>Operations log</h1>
              <div class="card">
                <div class="name" style="font-weight:600;margin-bottom:4px">Recent activity</div>
                <div class="meta" style="color:var(--text-2);margin-bottom:4px">A plain-language summary of the latest operations. Newest first.</div>
                <div id="opSummary"><p style="color:var(--text-2)">Loading…</p></div>
              </div>
              <details class="card" open>
                <summary style="cursor:pointer;font-weight:600" title="The complete unfiltered log, for troubleshooting.">Raw log <span style="font-weight:400;font-size:13px;color:var(--text-2)">(for diagnostics)</span></summary>
                <pre id="log" style="margin-top:8px">(loading…)</pre>
              </details>
            </div>
            <div class="view" id="view-settings">
              <h1>Settings</h1>
              <div class="card">
                <h2 style="margin-top:0">Backup jobs</h2>
                <div id="settingsJobs"><p style="color:var(--text-2)">Loading…</p></div>
                <div style="margin-top:16px">
                  <button class="accent" onclick="showJobForm()">Add job</button>
                </div>
              </div>
              <div class="card" id="jobFormCard" style="display:none">
                <h2 style="margin-top:0" id="jobFormTitle">Add backup job</h2>
                <div class="row" id="jf-type-row" style="margin-bottom:12px">
                  <label style="display:flex;align-items:center;gap:6px" title="Back up files and folders on a schedule."><input type="radio" name="jf-type" value="files" checked onchange="jobTypeChanged()"> Files &amp; folders</label>
                  <label style="display:flex;align-items:center;gap:6px" title="Image an entire disk bit-for-bit on a schedule. Always a full capture."><input type="radio" name="jf-type" value="disk" onchange="jobTypeChanged()"> Whole disk image</label>
                </div>
                <div id="jf-disk-row" style="display:none;margin-bottom:12px">
                  <div class="meta" style="margin-bottom:6px">Disk to image on schedule:</div>
                  <div class="row"><select id="jf-disk" style="flex:1"><option value="">Select a disk…</option></select>
                  <button type="button" onclick="refreshDiskList()" title="Rescan disks">↻</button></div>
                </div>
                <!-- Quick pick from Windows user folders -->
                <div id="jf-quickpick" style="margin-bottom:12px">
                  <div class="meta" style="margin-bottom:6px">Quick pick a folder to back up:</div>
                  <div id="jf-userfolders" style="display:flex;flex-wrap:wrap;gap:8px"><p style="color:var(--text-2)">Loading…</p></div>
                </div>
                <div class="form-grid">
                  <label title="A name for this backup job, e.g. 'Documents' or 'Photos'. Must be unique.">Name<input id="jf-name" type="text" placeholder="Documents"></label>
                  <label id="jf-source-label" title="The folder to back up. Click Browse to pick it, or choose from your user folders above.">Source folder<span class="folder-row"><input id="jf-source" type="text" placeholder="Choose a folder above or browse"><button type="button" class="browse-btn" onclick="browseFolder('jf-source', 'Select source folder')">Browse…</button></span></label>
                  <label title="How often to run this backup. Daily runs once per day at the specified time; Interval runs every N hours.">Schedule<select id="jf-schedType">
                    <option value="daily">Daily at…</option>
                    <option value="interval">Every N minutes</option>
                  </select></label>
                  <label id="jf-dailyWrap" title="The time of day to run the backup, in 24-hour format.">Time (HH:mm)<input id="jf-daily" type="time" value="02:00"></label>
                  <label id="jf-intervalWrap" style="display:none" title="How often to run the backup, in minutes. Minimum 5.">Minutes<input id="jf-interval" type="number" min="5" value="60"></label>
                </div>
                <div style="margin:12px 0">
                  <button type="button" onclick="toggleAdvancedJob()" id="jf-advToggle" title="Show settings most people can leave alone (repository location, upload targets, verification).">Show advanced settings ▸</button>
                </div>
                <div class="form-grid" id="jf-advanced" style="display:none">
                  <label title="Where FileKeep stores its internal backup data (encrypted). Leave empty to use the default in ProgramData. You don't need to change this.">Repository folder (optional, defaults to ProgramData)<span class="folder-row"><input id="jf-repo" type="text" placeholder="Leave empty for default"><button type="button" class="browse-btn" onclick="browseFolder('jf-repo', 'Select repository folder')">Browse…</button></span></label>
                  <label id="jf-mode-label" title="Full backup copies everything; Incremental only copies what changed since the last backup (faster).">Mode<select id="jf-mode">
                    <option value="incremental">Incremental</option>
                    <option value="full">Full</option>
                  </select></label>
                  <label id="jf-priv-label" class="check" title="Lets the service read files that are open or locked by other programs. Requires the service to run as Administrator."><input id="jf-priv" type="checkbox"> Use backup privilege (bypass file locks, admin required)</label>
                  <label id="jf-vss-label" class="check" title="Backs up from a point-in-time shadow copy, so files that change during the backup stay consistent. Requires Administrator."><input id="jf-vss" type="checkbox"> Use VSS shadow copy (point-in-time snapshot, admin required)</label>
                  <div id="jf-targets" style="margin:4px 0" title="Scheduled backups must upload to Usenet or a network location. Local-only backups can only be run manually, one at a time.">
                    <div class="meta" style="margin-bottom:4px">Upload to <b style="color:var(--bad)">*</b> <span style="color:var(--text-2)">(required — scheduled backups must leave this machine; manage locations in Backup locations below)</span></div>
                    <div id="jf-location-list"><p style="color:var(--text-2)">Loading locations…</p></div>
                  </div>
                  <label class="check" title="Checks the backup's integrity automatically after it finishes. Recommended — catches problems early."><input id="jf-autoverify" type="checkbox" checked> Automatically verify backup after it completes</label>
                  <label title="Extra recovery data posted alongside uploads, in case some Usenet articles are lost before you download.">Usenet redundancy:
                    <select id="jf-redundancy">
                      <option value="none">None</option>
                      <option value="xor">XOR parity (recovers 1 missing chunk)</option>
                      <option value="par2">PAR2 (recovers up to 3 missing)</option>
                    </select>
                  </label>
                  <label title="How thoroughly files that look unchanged are re-checked on incremental backups. 'Fast' trusts size and date; 'Paranoid' re-hashes everything.">Incremental verification:
                    <select id="jf-verification">
                      <option value="fast">Fast (size + mtime only)</option>
                      <option value="verify">Verify (hash files that look unchanged)</option>
                      <option value="paranoid">Paranoid (hash every file)</option>
                    </select>
                  </label>
                </div>
                <div id="jf-error" style="color:var(--bad);margin:8px 0;display:none"></div>
                <div style="margin-top:12px;display:flex;gap:8px">
                  <button class="accent" onclick="saveJob()">Save</button>
                  <button onclick="hideJobForm()">Cancel</button>
                </div>
              </div>
              <div class="card">
                <h2 style="margin-top:0">Backup locations</h2>
                <p style="color:var(--text-2);margin-top:0">Everywhere your backups can go — Usenet providers and network shares, in one place. Jobs pick from this list. Credentials are encrypted with Windows DPAPI; leave a password blank to keep the saved one.</p>
                <div id="locations-list"><p style="color:var(--text-2)">Loading…</p></div>
                <div style="margin-top:12px;display:flex;gap:8px">
                  <button onclick="showLocationForm('nntp')">＋ Add Usenet provider</button>
                  <button onclick="showLocationForm('smb')">＋ Add network share</button>
                </div>
              </div>
              <div class="card">
                <div class="name">NNTP connectivity diagnostic</div>
                <div class="meta">Probe 119/563/443 with classified failures; falls back to a free server to distinguish ISP blocking.</div>
                <div class="row" style="margin-top:8px">
                  <input id="op-host" placeholder="news.example.com" style="flex:1">
                  <button onclick="runDiagnose()">Diagnose</button>
                </div>
                <div id="diagnoseResult" style="margin-top:8px"></div>
              </div>
              <div class="card">
                <h2 style="margin-top:0">Security</h2>
                <p style="color:var(--text-2);margin-top:0">The repository passphrase encrypts everything you back up. Store it here (Windows DPAPI-encrypted, never in plaintext) so scheduled jobs run without typing it, or keep it in the USENETBACKUP_PASSPHRASE environment variable.</p>
                <div class="row" style="margin-top:8px">
                  <span id="passphrase-status" style="color:var(--text-2)">Checking…</span>
                </div>
                <div class="form-grid" style="margin-top:8px">
                  <label>New passphrase<input id="pp-pass" type="password" placeholder="Enter passphrase" autocomplete="new-password"></label>
                  <label>Confirm passphrase<input id="pp-confirm" type="password" placeholder="Repeat passphrase" autocomplete="new-password"></label>
                </div>
                <div class="row" style="margin-top:8px">
                  <label class="check" title="When enabled, opening a restore session requires touching your YubiKey. Scheduled backups still run unattended."><input id="pp-yubikey" type="checkbox"> Unlock with YubiKey <span style="color:var(--text-2);font-size:12px">(touch to open restore sessions; backups stay automatic)</span></label>
                </div>
                <div id="pp-yubikey-status" style="color:var(--text-2);font-size:12px;margin-top:4px"></div>
                <div id="pp-touch" style="display:none;margin-top:8px;padding:10px;border:1px solid var(--accent);border-radius:6px;background:#eef5fc">
                  <strong>Touch your YubiKey</strong><span id="pp-touch-detail" style="color:var(--text-2)"> — press the button when it flashes.</span>
                </div>
                <div id="pp-error" style="color:var(--bad);margin:8px 0;display:none"></div>
                <div style="margin-top:12px;display:flex;gap:8px">
                  <button class="accent" onclick="savePassphrase()">Save passphrase</button>
                  <button class="danger" onclick="clearPassphrase()">Clear stored passphrase</button>
                </div>
              </div>
              <div class="card" id="locationFormCard" style="display:none">
                <h2 style="margin-top:0" id="locationFormTitle">Add location</h2>
                <div class="form-grid">
                  <label title="A friendly name for this backup destination, e.g. 'Frugal Usenet' or 'NAS backups'. Must be unique.">Name<input id="lf-name" type="text" placeholder="e.g. Frugal Usenet, NAS backups"></label>
                  <input id="lf-type" type="hidden">
                </div>
                <div class="form-grid" id="lf-nntp-fields">
                  <label title="Choose your Usenet provider to auto-fill the host, port, and SSL settings below. Select 'Custom' to enter them manually.">Provider preset<select id="lf-preset" onchange="applyNntpPreset()">
                    <option value="">Custom…</option>
                    <option value="frugal">Frugal Usenet</option>
                    <option value="eweka">Eweka</option>
                    <option value="newshosting">Newshosting</option>
                    <option value="usenetserver">UsenetServer</option>
                    <option value="giganews">Giganews</option>
                    <option value="easynews">Easynews</option>
                  </select></label>
                  <label title="Your Usenet provider's server address, e.g. news.frugalusenet.com. Use the preset dropdown above to fill this automatically.">Host<input id="lf-host" type="text" placeholder="news.example.com"></label>
                  <label title="119 for standard connections, 563 for SSL-encrypted connections. The preset dropdown sets this automatically.">Port<input id="lf-port" type="number" min="1" max="65535" value="119"></label>
                  <label title="Your Usenet account username. Leave blank for providers that don't require authentication.">Username<input id="lf-user" type="text" placeholder="(optional)"></label>
                  <label title="Your Usenet account password. Stored encrypted on this machine. Leave blank to keep the existing saved password.">Password<input id="lf-pass" type="password" placeholder="(unchanged)" autocomplete="new-password"></label>
                  <label title="How many simultaneous connections to use. More is faster but some providers limit this. 10 is a safe default.">Connections<input id="lf-conn" type="number" min="1" max="100" value="10"></label>
                  <label class="check"><input id="lf-ssl" type="checkbox"> Use SSL (port 563)</label>
                </div>
                <div class="form-grid" id="lf-smb-fields" style="display:none">
                  <label title="Network path to the shared folder, e.g. \\NAS\backups\filekeep. Click Browse to pick it from the network.">Share (UNC path)<span class="folder-row"><input id="lf-share" type="text" placeholder="\\NAS\backups\filekeep"><button type="button" class="browse-btn" onclick="browseFolder('lf-share', 'Select network share')">Browse…</button></span></label>
                  <label title="Username for the network share, if it requires authentication. Leave blank for guest/anonymous shares.">Username<input id="lf-smbuser" type="text" placeholder="(optional)"></label>
                  <label title="Password for the network share. Stored encrypted on this machine. Leave blank to keep the existing saved password.">Password<input id="lf-smbpass" type="password" placeholder="(unchanged)" autocomplete="new-password"></label>
                </div>
                <div id="lf-error" style="color:var(--bad);margin:8px 0;display:none"></div>
                <div style="margin-top:12px;display:flex;gap:8px">
                  <button class="accent" onclick="saveLocation()">Save</button>
                  <button onclick="hideLocationForm()">Cancel</button>
                </div>
              </div>
                <div id="provider-status" style="color:var(--text-2);margin:8px 0;font-size:13px"></div>
              </div>
              <div class="foot">Changes are saved to service.json and take effect immediately. The service does not need to restart.</div>
            </div>
          </main>
        </div>
        <script>
        // Native folder picker: uses the FileKeep GUI's native dialog when available,
        // otherwise does nothing (user types the path manually in a plain browser).
        async function browseFolder(inputId, title) {
          if (typeof window.fileKeepPickFolder === 'function') {
            try {
              const path = await window.fileKeepPickFolder(title || 'Select a folder');
              if (path) document.getElementById(inputId).value = path;
            } catch (e) { /* dialog unavailable; leave the typed value */ }
          }
        }
        async function browseSaveFile(inputId, title, filter, defaultName) {
          if (typeof window.fileKeepPickSaveFile === 'function') {
            try {
              const path = await window.fileKeepPickSaveFile(title || 'Save as', filter || '', defaultName || '');
              if (path) document.getElementById(inputId).value = path;
            } catch (e) { /* dialog unavailable; leave the typed value */ }
          }
        }
        // NNTP provider presets: fill host/port/SSL from a known provider.
        const nntpPresets = {
          frugal: { host: 'news.frugalusenet.com', port: 563, ssl: true },
          eweka: { host: 'news.eweka.nl', port: 563, ssl: true },
          newshosting: { host: 'news.newshosting.com', port: 563, ssl: true },
          usenetserver: { host: 'news.usenetserver.com', port: 563, ssl: true },
          giganews: { host: 'news.giganews.com', port: 563, ssl: true },
          easynews: { host: 'news.easynews.com', port: 563, ssl: true },
        };
        function applyNntpPreset() {
          const key = document.getElementById('lf-preset').value;
          const p = nntpPresets[key];
          if (!p) return;
          document.getElementById('lf-host').value = p.host;
          document.getElementById('lf-port').value = p.port;
          document.getElementById('lf-ssl').checked = p.ssl;
          const nameEl = document.getElementById('lf-name');
          if (!nameEl.value) {
            const label = document.querySelector('#lf-preset option:checked').textContent;
            nameEl.value = label;
          }
        }
        // Whole-disk image jobs: pick a disk from the dropdown; folder
        // quick-pick, file-only mode and snapshot options do not apply.
        function jobTypeChanged() {
          const isDisk = document.querySelector('input[name="jf-type"]:checked').value === 'disk';
          document.getElementById('jf-quickpick').style.display = isDisk ? 'none' : '';
          document.getElementById('jf-source-label').style.display = isDisk ? 'none' : '';
          document.getElementById('jf-disk-row').style.display = isDisk ? '' : 'none';
          document.getElementById('jf-mode-label').style.display = isDisk ? 'none' : '';
          document.getElementById('jf-priv-label').style.display = isDisk ? 'none' : '';
          document.getElementById('jf-vss-label').style.display = isDisk ? 'none' : '';
          if (isDisk) refreshDiskList();
        }
        // Toggle advanced job settings.
        function toggleAdvancedJob() {
          const adv = document.getElementById('jf-advanced');
          const btn = document.getElementById('jf-advToggle');
          const show = adv.style.display === 'none';
          adv.style.display = show ? '' : 'none';
          btn.textContent = show ? 'Hide advanced settings ▾' : 'Show advanced settings ▸';
        }
        // Load Windows user folders for quick-pick.
        async function loadUserFolders() {
          const box = document.getElementById('jf-userfolders');
          if (!box) return;
          try {
            const folders = await api('/api/user-folders');
            if (!folders.length) {
              box.innerHTML = '<p style="color:var(--text-2)">No user folders found.</p>';
              return;
            }
            box.innerHTML = folders.map(f =>
              `<button type="button" onclick="pickUserFolder('${esc(f.path)}', '${esc(f.name)}')">📁 ${esc(f.name)}</button>`
            ).join('');
          } catch (e) {
            box.innerHTML = '<p style="color:var(--text-2)">Could not load user folders.</p>';
          }
        }
        function pickUserFolder(path, name) {
          document.getElementById('jf-source').value = path;
          const nameInput = document.getElementById('jf-name');
          if (!nameInput.value) nameInput.value = name;
        }
        const csrfToken = document.querySelector('meta[name="csrf-token"]')?.content || '';
        async function api(path, opts) {
          opts = opts || {};
          const method = (opts.method || 'GET').toUpperCase();
          if (method !== 'GET' && method !== 'HEAD') {
            opts.headers = Object.assign({}, opts.headers, {
              'Content-Type': 'application/json',
              'X-CSRF-Token': csrfToken
            });
          }
          const r = await fetch(path, opts);
          if (!r.ok) {
            const t = await r.text();
            let msg = t;
            try { const j = JSON.parse(t); msg = j.error || j.message || t; } catch { /* not JSON, use raw text */ }
            throw new Error(msg);
          }
          return r.status === 202 ? null : r.json();
        }
        function esc(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
        // UNC path check for network shares: \\server\share (optionally deeper).
        function isValidUnc(p) { return /^\\\\[^\\]+\\[^\\]+/.test(p || ''); }
        // Toast notifications: non-blocking inline feedback replacing alert().
        function toast(msg, kind) {
          let box = document.getElementById('toast-container');
          if (!box) {
            box = document.createElement('div');
            box.id = 'toast-container';
            document.body.appendChild(box);
          }
          const el = document.createElement('div');
          el.className = 'toast' + (kind ? ' ' + kind : '');
          el.textContent = msg;
          box.appendChild(el);
          setTimeout(() => { el.style.opacity = '0'; el.style.transition = 'opacity 0.3s'; setTimeout(() => el.remove(), 350); }, 4000);
        }
        let jobNames = [];
        document.querySelectorAll('.nav-item').forEach(a => a.addEventListener('click', () => {
          document.querySelectorAll('.nav-item').forEach(x => x.classList.remove('active'));
          a.classList.add('active');
          document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
          document.getElementById('view-' + a.dataset.view).classList.add('active');
        }));
        function fmtLast(j) {
          if (!j.lastResult) return '<span class="pill idle">never run</span>';
          return j.lastResult.success
            ? '<span class="pill">backed up</span>'
            : '<span class="pill fail">failed</span>';
        }
        // First-run setup card: visible on Overview until at least one backup
        // location and one job exist. Buttons link into the existing forms.
        let lastSetupSig = '';
        function gotoSettingsLocation() {
          document.querySelector('[data-view="settings"]').click();
          showLocationForm('nntp');
          setTimeout(() => document.getElementById('locationFormCard').scrollIntoView({ behavior: 'smooth', block: 'nearest' }), 60);
        }
        function gotoSettingsJob() {
          document.querySelector('[data-view="settings"]').click();
          showJobForm();
        }
        function renderSetupCard(jobs, locations) {
          const sig = jobs.length + ':' + locations.length + ':' + jobs.some(j => j.lastResult);
          if (sig === lastSetupSig) return;
          lastSetupSig = sig;
          const card = document.getElementById('setupCard');
          if (locations.length && jobs.length) { card.style.display = 'none'; return; }
          const hasRun = jobs.some(j => j.lastResult);
          const step = (done, n, title, meta, btnHtml) => `
            <div class="row">
              <div style="font-size:18px;width:28px;text-align:center">${done ? '✓' : n}</div>
              <div class="grow">
                <div class="name">${title}</div>
                <div class="meta">${meta}</div>
              </div>
              ${done ? '<span class="pill">done</span>' : btnHtml}
            </div>`;
          document.getElementById('setupSteps').innerHTML =
            step(locations.length > 0, 1, 'Add a backup location',
              'Where backups go — a Usenet provider or a network share.',
              '<button class="accent" onclick="gotoSettingsLocation()" title="Open the add-location form in Settings">Add location</button>') +
            step(jobs.length > 0, 2, 'Create a backup job',
              'Pick a folder and a schedule.',
              '<button class="accent" onclick="gotoSettingsJob()" title="Open the add-job form in Settings">Add job</button>') +
            step(hasRun, 3, 'Run your first backup',
              'Back up now, or wait for the schedule.',
              `<button onclick="runAll()" ${jobs.length ? '' : 'disabled'} title="Run all backup jobs now">Back up now</button>`);
          card.style.display = '';
        }
        async function load() {
          const s = await api('/api/status');
          jobNames = s.jobs.map(j => j.name);
          const icon = document.getElementById('heroIcon');
          const title = document.getElementById('heroTitle');
          const sub = document.getElementById('heroSub');
          const anyFail = s.jobs.some(j => j.lastResult && !j.lastResult.success);
          const anyNever = s.jobs.some(j => !j.lastResult);
          const lastOk = s.jobs.flatMap(j => j.lastResult && j.lastResult.success ? [j.lastResult.startedLocal] : []).sort().pop();
          if (!s.jobs.length) {
            icon.textContent = '⛁'; icon.className = 'hero-icon warn';
            title.textContent = 'No backup jobs configured';
            sub.textContent = 'Add jobs to service.json and restart the service.';
          } else if (anyFail) {
            icon.textContent = '!'; icon.className = 'hero-icon bad';
            title.textContent = 'A backup needs attention';
            sub.textContent = 'One or more jobs failed. See Backup jobs below.';
          } else if (anyNever) {
            icon.textContent = '⛁'; icon.className = 'hero-icon warn';
            title.textContent = 'Backup is set up';
            sub.textContent = 'No backup has run yet. Press "Back up now" or wait for the schedule.';
          } else {
            icon.textContent = '✓'; icon.className = 'hero-icon';
            title.textContent = "You're backed up";
            sub.textContent = lastOk ? 'Last backup: ' + lastOk + ' (local time)' : '';
          }
          document.getElementById('jobs').innerHTML = s.jobs.map(j => `
            <div class="row">
              <div class="grow">
                <div class="name">${esc(j.name)} ${fmtLast(j)}</div>
                <div class="meta">${esc(j.schedule)} · ${esc(j.mode)}${j.backupPrivilege ? ' · backup-privilege' : ''}${j.vss ? ' · vss' : ''}<br>
                ${esc(j.source)} → ${esc(j.repo)}<br>
                Next run: ${esc(j.nextRunLocal)} (local)${j.consecutiveFailures ? ` · <span style="color:var(--bad)">${j.consecutiveFailures} consecutive failures</span>` : ''}</div>
              </div>
              <button onclick="runJob('${esc(j.name)}')">Run now</button>
            </div>`).join('') || '<p style="color:var(--text-2)">No jobs.</p>';
          // Load expiration warnings for each job's repo
          loadExpiration(s.jobs);
          // First-run setup card (Overview).
          try {
            const cfg = await api('/api/config');
            renderSetupCard(s.jobs, cfg.locations || []);
          } catch (e) { /* setup card stays hidden on error */ }
          // Start upload progress polling
          startUploadPolling();
          const sel = document.getElementById('repo');
          if (!sel.options.length)
            s.jobs.forEach(j => sel.add(new Option(j.name + ' — ' + j.repo, j.repo)));
          loadRepo();
          loadLog();
        }
        async function loadExpiration(jobs) {
          const box = document.getElementById('expiration');
          try {
            // Fetch all jobs in parallel, not sequentially
            const results = await Promise.all(jobs.map(j =>
              api('/api/expiration?repo=' + encodeURIComponent(j.repo) + '&warnDays=90')
                .then(items => items.map(x => ({ ...x, jobName: j.name })))
                .catch(() => []) // Skip failed jobs, don't break the whole list
            ));
            const all = results.flat();
            if (!all.length) {
              box.innerHTML = '<p style="color:var(--text-2)">No uploads expiring within 90 days.</p>';
              return;
            }
            all.sort((a, b) => a.daysLeft - b.daysLeft);
            box.innerHTML = all.map(x => `
              <div class="row">
                <div class="grow">
                  <div class="name">${esc(x.backupId.substring(0, 8))}… <span style="color:${x.daysLeft < 0 ? 'var(--bad)' : 'var(--warn)'}">${x.daysLeft < 0 ? 'EXPIRED' : x.daysLeft + 'd left'}</span></div>
                  <div class="meta">${esc(x.jobName)} · ${esc(x.providerHost)} · retention ${x.retentionDays}d<br>
                  Uploaded: ${esc(x.uploadedUtc)} · Expires: ${esc(x.expiresUtc)}</div>
                </div>
              </div>`).join('');
          } catch (e) {
            box.innerHTML = '<p style="color:var(--bad)">Could not load expiration data: ' + esc(e.message) + '</p>';
          }
        }
        let uploadPollTimer = null;
        function startUploadPolling() {
          if (uploadPollTimer) return;
          const poll = async () => {
            try {
              const res = await api('/api/upload-progress');
              const card = document.getElementById('uploadProgress');
              if (!res.active) {
                card.style.display = 'none';
                return;
              }
              const p = res.progress;
              card.style.display = 'block';
              document.getElementById('upTitle').textContent = 'Uploading ' + p.phase + ' to ' + p.host;
              document.getElementById('upMeta').textContent = p.jobName + ' — ' + p.doneChunks + '/' + p.totalChunks + ' items';
              document.getElementById('upBar').style.width = p.percent + '%';
              const bps = p.bytesPerSec;
              const speed = bps >= 1024*1024 ? (bps/(1024*1024)).toFixed(1) + ' MB/s' : Math.round(bps/1024) + ' KB/s';
              document.getElementById('upSpeed').textContent = speed;
              document.getElementById('upEta').textContent = p.measuring
                ? 'Measuring throughput...'
                : (p.eta ? '~' + p.eta + ' remaining' : 'Almost done');
            } catch (e) { /* ignore poll errors */ }
          };
          poll();
          uploadPollTimer = setInterval(poll, 5000); // Poll every 5 seconds
        }
        async function runJob(name) {
          const csrf = document.querySelector('meta[name=csrf-token]').content;
          await api('/api/jobs/' + encodeURIComponent(name) + '/run',
            { method: 'POST', headers: { 'X-CSRF-Token': csrf } });
          setTimeout(load, 2000);
        }
        async function runAll() {
          const btn = document.getElementById('backupNow');
          btn.disabled = true;
          try { for (const n of jobNames) await runJob(n); }
          finally { btn.disabled = false; setTimeout(load, 2000); }
        }
        function showOneTimeForm() {
          document.getElementById('ot-source').value = '';
          document.getElementById('ot-dest').value = '';
          document.getElementById('ot-error').style.display = 'none';
          document.querySelectorAll('#oneTimeCard .invalid').forEach(el => el.classList.remove('invalid'));
          document.getElementById('oneTimeCard').style.display = '';
          document.getElementById('oneTimeCard').scrollIntoView({behavior:'smooth',block:'nearest'});
        }
        function hideOneTimeForm() {
          document.getElementById('oneTimeCard').style.display = 'none';
        }
        async function runOneTimeBackup() {
          const errBox = document.getElementById('ot-error');
          errBox.style.display = 'none';
          document.querySelectorAll('#oneTimeCard .invalid').forEach(el => el.classList.remove('invalid'));
          const source = document.getElementById('ot-source').value.trim();
          const dest = document.getElementById('ot-dest').value.trim();
          let valid = true;
          if (!source) { document.getElementById('ot-source').classList.add('invalid'); valid = false; }
          if (!dest) { document.getElementById('ot-dest').classList.add('invalid'); valid = false; }
          if (!valid) {
            errBox.textContent = 'Choose a source folder and a destination folder.';
            errBox.style.display = '';
            return;
          }
          const csrf = document.querySelector('meta[name=csrf-token]').content;
          try {
            await api('/api/backup/once', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
              body: JSON.stringify({ sourceDir: source, repoPath: dest }),
            });
            hideOneTimeForm();
            toast('One-time backup started. Watch the Operations log for progress.', 'ok');
          } catch (e) {
            errBox.textContent = e.message;
            errBox.style.display = '';
          }
        }
        async function loadRepo() {
          const repo = document.getElementById('repo').value;
          if (!repo) return;
          refreshRestoreSession();
          const q = '?repo=' + encodeURIComponent(repo);
          let bs = [];
          try {
            bs = await api('/api/backups' + q);
            document.querySelector('#backups tbody').innerHTML = bs.map(b =>
              `<tr><td><code>${esc(b.backupId)}</code></td><td>${esc(b.type)}</td><td>${esc(b.createdUtc)}</td>` +
              `<td><button onclick="uploadBackup('${esc(b.backupId)}')">Upload</button> ` +
              `<button onclick="verifyBackup('${esc(b.backupId)}')">Verify</button> ` +
              `<button onclick="downloadNzb('${esc(b.backupId)}')">NZB</button> ` +
              `<button onclick="openRestoreModal('${esc(b.backupId)}','${esc(b.type)}')">Restore</button></td></tr>`).join('')
              || '<tr><td colspan="4" style="color:var(--text-2)">No backups yet.</td></tr>';
          } catch (e) { document.querySelector('#backups tbody').innerHTML = `<tr><td colspan="4">${esc(e.message)}</td></tr>`; }
          document.getElementById('remoteBackups').innerHTML = '';
          // Refresh the destinations card with the current repo path.
          if (typeof renderDestinations === 'function') renderDestinations(cachedLocations);
        }
        async function uploadBackup(backupId) {
          const repo = document.getElementById('repo').value;
          if (!confirm(`Upload backup ${backupId} to Usenet?`)) return;
          await api('/api/operations/upload', { method: 'POST', body: JSON.stringify({ repo, backupId }) });
          toast('Upload started in the background. Watch the Operations log.');
        }
        async function verifyBackup(backupId) {
          const repo = document.getElementById('repo').value;
          await api('/api/operations/verify', { method: 'POST', body: JSON.stringify({ repo, backupId }) });
          toast('Verification started in the background. Watch the Operations log.');
        }
        function downloadNzb(backupId) {
          const repo = document.getElementById('repo').value;
          window.location = '/api/operations/nzb?repo=' + encodeURIComponent(repo) + '&backupId=' + encodeURIComponent(backupId);
        }
        async function startAdhocBackup() {
          const repo = document.getElementById('repo').value;
          const sourceDir = document.getElementById('adhoc-source').value.trim();
          if (!sourceDir) { toast('Enter a source folder.', 'err'); return; }
          await api('/api/operations/backup-now', { method: 'POST', body: JSON.stringify({ repo, sourceDir }) });
          toast('Backup started in the background. Watch the Operations log.');
        }
        async function startSmbSync() {
          const share = document.getElementById('smb-share').value.trim();
          const user = document.getElementById('smb-user').value.trim();
          if (!share) { toast('Enter the SMB share path.', 'err'); return; }
          await api('/api/operations/smb-sync', { method: 'POST', body: JSON.stringify({ share, user }) });
          toast('SMB sync started in the background. Watch the Operations log.');
        }
        async function uploadNzb() {
          const input = document.getElementById('nzb-file');
          if (!input.files.length) { toast('Choose an NZB file.', 'err'); return; }
          const form = new FormData();
          form.append('nzb', input.files[0]);
          const res = await fetch('/api/operations/download-nzb', {
            method: 'POST', headers: { 'X-CSRF-Token': csrfToken }, body: form
          });
          if (!res.ok) { toast('Failed: ' + (await res.text()), 'err'); return; }
          toast('NZB download started in the background. Watch the Operations log.');
        }
        async function startDiskBackup() {
          const repo = document.getElementById('repo').value;
          const device = document.getElementById('disk-device').value;
          if (!device) { toast('Select a disk.', 'err'); return; }
          // Image name is generated: disk<N>-<timestamp>.img — no typing needed.
          const n = device.match(/(\d+)\s*$/)?.[1] ?? 'x';
          const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 12);
          const imageName = `disk${n}-${stamp}.img`;
          await api('/api/operations/disk-backup', { method: 'POST', body: JSON.stringify({ repo, device, imageName }) });
          toast('Disk backup started in the background. Watch the Operations log.');
        }
        function backupNowTypeChanged() {
          const isDisk = document.querySelector('input[name="backupnow-type"]:checked').value === 'disk';
          document.getElementById('backupnow-files-row').style.display = isDisk ? 'none' : '';
          document.getElementById('backupnow-disk-row').style.display = isDisk ? '' : 'none';
        }
        // Populate every disk picker from /api/operations/drives (no typing).
        // The restore-target picker never lists the running system disk.
        async function refreshDiskList() {
          let drives = [];
          try { drives = await api('/api/operations/drives'); }
          catch (e) { toast('Could not list disks: ' + e.message, 'err'); return; }
          const fmtSize = b => b >= 1073741824 ? (b / 1073741824).toFixed(1) + ' GB' : Math.max(1, Math.round(b / 1048576)) + ' MB';
          const fill = (id, excludeSystem) => {
            const sel = document.getElementById(id);
            if (!sel) return;
            const cur = sel.value;
            sel.innerHTML = '<option value="">Select a disk…</option>';
            drives.filter(d => !excludeSystem || !d.isSystemDrive).forEach(d => {
              const label = d.number + ': ' + d.model + ' (' + fmtSize(d.sizeBytes) + ')' +
                (d.isSystemDrive ? ' — \u26a0 SYSTEM DISK' : '');
              sel.add(new Option(label, d.devicePath));
            });
            if ([...sel.options].some(o => o.value === cur && cur)) sel.value = cur;
          };
          fill('disk-device', false);
          fill('m-disk-target', true);
          fill('jf-disk', false);
        }
        // Restore is an action on a backup: the Restore button on each row opens
        // this modal, pre-set for file or disk-image restores.
        let restoreTarget = null;
        async function lockRestoreSession() {
          try { await api('/api/restore/lock', { method: 'POST' }); } catch (e) { /* ignore */ }
          refreshRestoreSession();
        }
        async function refreshRestoreSession() {
          const row = document.getElementById('restore-session-row');
          const st = document.getElementById('restore-session-status');
          if (!row || !st) return;
          try {
            const s = await api('/api/restore/session');
            if (!s.yubikey) { row.style.display = 'none'; return; }
            row.style.display = '';
            st.textContent = s.unlocked ? 'Restore session: unlocked ✓' : 'Restore session: locked — touch your YubiKey to restore';
            st.style.color = s.unlocked ? 'var(--good)' : 'var(--text-2)';
          } catch (e) { row.style.display = 'none'; }
        }
        async function ensureRestoreSession() {
          // Returns true when restores may proceed. In YubiKey mode this
          // touches the key to unlock the session first.
          let s;
          try { s = await api('/api/restore/session'); }
          catch (e) { toast('Could not check restore session: ' + e.message, 'err'); return false; }
          if (!s.yubikey || s.unlocked) return true;
          if (!confirm('Restores require your YubiKey. Touch the key when it flashes. Continue?')) return false;
          toast('Touch your YubiKey…');
          try {
            await api('/api/restore/unlock', { method: 'POST' });
            toast('Restore session unlocked.', 'ok');
            return true;
          } catch (e) { toast('Unlock failed: ' + e.message, 'err'); return false; }
        }
        async function openRestoreModal(backupId, type) {
          if (!await ensureRestoreSession()) return;
          restoreTarget = { backupId, type };
          const isDisk = type === 'disk-image';
          document.getElementById('restoreModalTitle').textContent = isDisk ? 'Restore disk image' : 'Restore backup';
          document.getElementById('restoreModalSub').textContent = 'Backup ' + backupId.substring(0, 8) + '\u2026 (' + type + ')';
          document.getElementById('restoreFilesForm').style.display = isDisk ? 'none' : '';
          document.getElementById('restoreDiskForm').style.display = isDisk ? '' : 'none';
          const go = document.getElementById('restoreModalGo');
          go.className = isDisk ? 'danger' : 'accent';
          go.textContent = isDisk ? 'Restore image to disk' : 'Restore';
          document.getElementById('restoreModal').style.display = '';
          if (isDisk) refreshDiskList();
        }
        function closeRestoreModal() {
          document.getElementById('restoreModal').style.display = 'none';
          restoreTarget = null;
        }
        async function submitRestore() {
          const repo = document.getElementById('repo').value;
          if (!restoreTarget) return;
          if (restoreTarget.type === 'disk-image') {
            const device = document.getElementById('m-disk-target').value;
            const ack = document.getElementById('m-disk-ack').checked;
            if (!device) { toast('Select the target disk.', 'err'); return; }
            if (!ack) { toast('Check the box to acknowledge the destructive restore.', 'err'); return; }
            const label = document.querySelector('#m-disk-target option:checked')?.textContent || device;
            if (!window.confirm('This will DESTROY all data on ' + label + '. Continue?')) return;
            await api('/api/operations/disk-restore', { method: 'POST', body: JSON.stringify({ repo, backupId: restoreTarget.backupId, device, confirmed: true }) });
            toast('Disk restore started in the background. Watch the Operations log.');
          } else {
            const destDir = document.getElementById('m-restore-dest').value.trim();
            const smbShare = document.getElementById('m-restore-smb').value.trim();
            const smbUser = document.getElementById('m-restore-smb-user').value.trim();
            const mergePolicy = document.getElementById('m-restore-policy').value;
            if (!destDir) { toast('Enter a destination folder.', 'err'); return; }
            await api('/api/operations/restore', { method: 'POST', body: JSON.stringify({ repo, backupId: restoreTarget.backupId, destDir, smbShare, smbUser, mergePolicy }) });
            toast('Restore started in the background. Watch the Operations log.');
          }
          closeRestoreModal();
        }
        async function refreshLanStatus() {
          try {
            const s = await api('/api/operations/lan-server');
            document.getElementById('lan-status').textContent =
              s.running ? `Running on port ${s.port}` : 'Stopped';
          } catch (e) { /* ignore */ }
        }
        async function startLanServer() {
          const port = parseInt(document.getElementById('lan-port').value, 10) || 8477;
          if (!confirm(`Start the LAN server on port ${port}? Anyone on your network can read these repos' (encrypted) chunks.`)) return;
          await api('/api/operations/lan-server/start', { method: 'POST', body: JSON.stringify({ port }) });
          refreshLanStatus();
        }
        async function stopLanServer() {
          await api('/api/operations/lan-server/stop', { method: 'POST' });
          refreshLanStatus();
        }
        async function discoverRemote() {
          const repo = document.getElementById('repo').value;
          const box = document.getElementById('remoteBackups');
          box.innerHTML = '<p style="color:var(--text-2)">Discovering…</p>';
          try {
            const rs = await api('/api/backups/remote?repo=' + encodeURIComponent(repo));
            if (!rs.length) { box.innerHTML = '<p style="color:var(--text-2)">No remote-only backups found.</p>'; return; }
            box.innerHTML = '<table><thead><tr><th></th><th>ID</th><th>Type</th><th>Created (UTC)</th><th>Files</th></tr></thead><tbody>' +
              rs.map(r => `<tr><td><input type="checkbox" class="remote-check" value="${esc(r.backupId)}"></td>` +
                `<td><code>${esc(r.backupId)}</code></td><td>${esc(r.type)}</td><td>${esc(r.createdUtc)}</td><td>${r.fileCount}</td></tr>`).join('') +
              '</tbody></table><button onclick="importRemote()">Import selected</button>';
          } catch (e) { box.innerHTML = `<p>${esc(e.message)}</p>`; }
        }
        async function importRemote() {
          const repo = document.getElementById('repo').value;
          const ids = [...document.querySelectorAll('.remote-check:checked')].map(c => c.value);
          if (!ids.length) { toast('Select at least one backup.', 'err'); return; }
          await api('/api/backups/remote/import?repo=' + encodeURIComponent(repo),
            { method: 'POST', body: JSON.stringify({ backupIds: ids }) });
          toast('Imported ' + ids.length + ' backup' + (ids.length === 1 ? '' : 's') + '.', 'ok');
          loadRepo();
        }
        // ---- Operations ----
        async function runRetentionCheck() {
          const dryRun = document.getElementById('op-dryrun').checked;
          if (!dryRun && !confirm('Run a live retention check (may repost articles)?')) return;
          await api('/api/operations/retention-check', { method: 'POST', body: JSON.stringify({ dryRun }) });
          toast('Retention check started in the background. Watch the Operations log.', 'ok');
        }
        async function runDiagnose() {
          const host = document.getElementById('op-host').value.trim();
          if (!host) { toast('Enter a hostname.', 'err'); return; }
          const box = document.getElementById('diagnoseResult');
          box.innerHTML = '<p style="color:var(--text-2)">Probing…</p>';
          try {
            const r = await api('/api/operations/diagnose', { method: 'POST', body: JSON.stringify({ host }) });
            box.innerHTML = '<table><tbody>' +
              r.probes.map(p => `<tr><td><code>${esc(p.port)}</code></td><td>${esc(p.result)}</td></tr>`).join('') +
              (r.fallback ? `<tr><td><code>${esc(r.fallback.host)}</code></td><td>${esc(r.fallback.result)}</td></tr>` : '') +
              '</tbody></table><p>' + esc(r.diagnosis) + '</p>';
          } catch (e) { box.innerHTML = `<p>${esc(e.message)}</p>`; }
        }
        async function checkWinRePrereqs() {
          const el = document.getElementById('winre-prereq');
          el.textContent = 'Checking…';
          try {
            const p = await api('/api/operations/winre-prerequisites');
            if (p.ready) {
              el.innerHTML = '<b style="color:green">Ready</b> — WinRE enabled, no ADK needed.';
            } else {
              const missing = (p.missing || []).map(esc).join(', ');
              el.innerHTML = '<b style="color:red">Not ready:</b> ' + missing;
            }
          } catch (e) { el.textContent = e.message; }
        }
        function winreModeChanged() {
          const mode = document.querySelector('input[name="winre-mode"]:checked').value;
          document.getElementById('winre-usb-row').style.display = mode === 'usb' ? '' : 'none';
          document.getElementById('winre-image-row').style.display = mode === 'image' ? '' : 'none';
          document.getElementById('winre-mode-hint').textContent = mode === 'usb'
            ? 'Destructive — the USB drive is formatted. All data on it is destroyed.'
            : 'Builds a bootable .vhdx file you can flash later with Rufus/BalenaEtcher or boot in a VM. Not destructive.';
        }
        async function startWinReBuild() {
          const mode = document.querySelector('input[name="winre-mode"]:checked').value;
          const sourceDir = (document.getElementById(mode === 'usb' ? 'winre-src' : 'winre-src-img').value || '').trim();
          const bootMode = document.getElementById('winre-bootmode').value;
          let body, confirmText;
          if (mode === 'usb') {
            const diskNum = document.getElementById('winre-drive').value.trim();
            if (!diskNum) { toast('Select a USB drive first.', 'err'); return; }
            const diskLabel = document.querySelector('#winre-drive option:checked')?.textContent || ('Disk ' + diskNum);
            confirmText = 'Build WinRE USB on ' + diskLabel + '? This FORMATS the drive — all data is destroyed.';
            body = { diskNumber: parseInt(diskNum, 10), sourceDir: sourceDir || null, bootMode };
          } else {
            const imagePath = document.getElementById('winre-image').value.trim();
            if (!imagePath) { toast('Choose where to save the recovery image (Browse…).', 'err'); return; }
            const imageSizeMB = Math.max(1024, parseInt(document.getElementById('winre-imgsize').value, 10) || 4096);
            confirmText = 'Build WinRE disk image at ' + imagePath + '? This takes several minutes.'
              + (/\.[^\\/]+$/.test(imagePath) ? '' : ' (A folder saves as filekeep-recovery.vhdx inside it; otherwise .vhdx is appended.)');
            body = { imagePath, sourceDir: sourceDir || null, bootMode, imageSizeMB };
          }
          if (!window.confirm(confirmText)) return;
          try {
            await api('/api/operations/winre-build', { method: 'POST', body: JSON.stringify(body) });
            checkWinReStatus();
          } catch (e) { toast('Failed: ' + e.message, 'err'); }
        }
        function fmtDuration(sec) {
          sec = Math.max(0, Math.round(sec));
          if (sec < 60) return sec + 's';
          const m = Math.floor(sec / 60), s = sec % 60;
          return m + 'm ' + s + 's';
        }
        let winrePollTimer = null;
        async function checkWinReStatus() {
          const el = document.getElementById('winre-status');
          try {
            const s = await api('/api/operations/winre-build-status');
            let html = 'Status: <b>' + esc(s.status) + '</b>';
            if (s.drive) html += ' — target ' + esc(s.drive);
            const cancelBtn = document.getElementById('winre-cancel');
            if (cancelBtn) cancelBtn.style.display = s.status === 'running' ? '' : 'none';
            if (s.error) html += ' <span style="color:red">' + esc(s.error) + '</span>';
            if (s.status === 'running') {
              const pct = s.percent || 0;
              html += '<div style="margin-top:8px;max-width:420px">'
                + '<div style="background:var(--bg-2,#e0e0e0);border-radius:4px;height:14px;overflow:hidden">'
                + '<div style="background:var(--accent,#0078d4);height:100%;width:' + pct + '%;transition:width .5s"></div>'
                + '</div>'
                + '<div style="margin-top:4px;color:var(--text-2)">'
                + pct + '% — ' + esc(s.currentStep || 'working…');
              if (s.elapsedSec) html += ' (elapsed ' + fmtDuration(s.elapsedSec);
              if (s.estimatedRemainingSec) html += ', ~' + fmtDuration(s.estimatedRemainingSec) + ' left';
              if (s.elapsedSec) html += ')';
              html += '</div></div>';
              if (s.steps && s.steps.length) {
                html += '<ol style="margin-top:8px;padding-left:20px">';
                for (const st of s.steps) {
                  const icon = st.state === 'completed' ? '✅' : st.state === 'started' ? '⏳' : '⬜';
                  html += '<li>' + icon + ' ' + esc(st.name || ('Step ' + st.step)) + '</li>';
                }
                html += '</ol>';
              }
              // Keep polling while running.
              if (!winrePollTimer) winrePollTimer = setInterval(checkWinReStatus, 3000);
            } else {
              if (winrePollTimer) { clearInterval(winrePollTimer); winrePollTimer = null; }
              if (s.status === 'complete') html += ' <b style="color:green">Done.</b>';
            }
            el.innerHTML = html;
          } catch (e) { el.textContent = e.message; }
        }
        async function cancelWinReBuild() {
          if (!window.confirm('Cancel the running WinRE build? The target may be left partially written; you can start a new build afterwards.')) return;
          try {
            await api('/api/operations/winre-build/cancel', { method: 'POST' });
            checkWinReStatus();
          } catch (e) { toast('Failed: ' + e.message, 'err'); }
        }
        async function refreshWinReDrives() {
          const sel = document.getElementById('winre-drive');
          if (!sel) return;
          sel.innerHTML = '<option value="">Scanning…</option>';
          try {
            const ds = await api('/api/operations/usb-drives');
            sel.innerHTML = '<option value="">Select USB drive…</option>' + ds.map(d => {
              const gb = (d.sizeBytes / 1073741824).toFixed(1);
              const label = `${esc(d.model)} (${gb} GB)`;
              return `<option value="${esc(d.number)}">Disk ${d.number}: ${label}</option>`;
            }).join('');
            if (!ds.length)
              sel.innerHTML = '<option value="">No USB drives found</option>';
          } catch (e) {
            sel.innerHTML = '<option value="">Scan failed</option>';
          }
        }
        // Populate the USB dropdown when the Recovery view is opened.
        document.querySelector('[data-view="recovery"]')?.addEventListener('click', refreshWinReDrives);
        document.querySelector('[data-view="backups"]')?.addEventListener('click', refreshDiskList);
        // Human-readable summary of the operations log.
        // Raw lines look like: 2026-10-13T12:34:56.789Z [backup] type=full id=abc files=5 ...
        const opLabels = {
          'backup': 'Backup', 'scheduled-backup': 'Scheduled backup',
          'restore': 'Restore', 'restore-disk': 'Disk restore',
          'verify': 'Verify', 'auto-verify': 'Auto-verify',
          'nntp-upload': 'Upload to Usenet', 'manual-upload': 'Manual upload',
          'auto-upload': 'Auto-upload', 'manifest-upload': 'Manifest upload',
          'manifest-index-upload': 'Manifest index upload',
          'nzb-generate': 'NZB generated', 'download': 'Download from Usenet',
          'import-remote': 'Import from Usenet', 'init': 'Repository initialized'
        };
        function opFailed(op, kv, rest) {
          if (op.endsWith('-failed')) return true;
          if ((kv.ok || '').toLowerCase() === 'false') return true;
          const em = rest.match(/errors=(\d+)/);
          if (em && parseInt(em[1], 10) > 0) return true;
          return /FAILED/.test(rest);
        }
        function fmtOpTime(iso) {
          const d = new Date(iso);
          if (isNaN(d.getTime())) return iso;
          const t = d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
          return d.toDateString() === new Date().toDateString()
            ? 'today ' + t
            : d.toLocaleDateString([], { month: 'short', day: 'numeric' }) + ', ' + t;
        }
        function summarizeOpLine(line) {
          const m = line.match(/^(\S+)\s+\[([^\]]+)\]\s*(.*)$/);
          if (!m) return null;
          const ts = m[1], op = m[2], rest = m[3] || '';
          const kv = {};
          const re = /([A-Za-z_]\w*)=("[^"]*"|\S+)/g;
          let km;
          while ((km = re.exec(rest)) !== null) kv[km[1]] = km[2].replace(/^"|"$/g, '');
          // Error text may contain spaces, so capture it to end-of-line.
          const errText = (rest.match(/error=([\s\S]*)$/) || [])[1];
          const failed = opFailed(op, kv, rest);
          const base = op.replace(/-failed$/, '');
          const title = (opLabels[base] || base.replace(/-/g, ' ')) + (failed ? ' failed' : '');
          const shortId = id => id && id.length > 10 ? id.substring(0, 8) + '…' : (id || '');
          let detail = '';
          switch (base) {
            case 'backup':
              detail = `${kv.type || 'backup'} — ${kv.files || '?'} files` + (kv.source ? ` from ${kv.source}` : '');
              break;
            case 'scheduled-backup':
              detail = `job '${kv.job || '?'}' — ${kv.type || ''}${kv.type ? ', ' : ''}${kv.files || '?'} files`;
              if (failed && errText) detail += ` — ${errText.trim().substring(0, 120)}`;
              break;
            case 'restore':
              detail = `${kv.restored || kv.files || '?'} of ${kv.files || '?'} files → ${kv.dest || ''}`;
              if (failed) detail += ` (${kv.errors || '?'} errors)`;
              break;
            case 'restore-disk':
              detail = `image ${shortId(kv.id)} → ${kv.device || ''}`;
              break;
            case 'verify':
            case 'auto-verify':
              detail = `backup ${shortId(kv.id)} — ${kv.files || '?'} files, ${kv.errors || 0} errors`;
              break;
            case 'nntp-upload':
            case 'auto-upload':
              detail = failed && errText
                ? errText.trim().substring(0, 120)
                : `${kv.uploaded || 0} posted, ${kv.skipped || 0} already present` + (kv.host ? ` (${kv.host})` : '');
              break;
            case 'manual-upload': {
              const reason = (rest.match(/FAILED:\s*([\s\S]*)$/) || [])[1];
              detail = `backup ${shortId(kv.backupId)}` + (reason ? ` — ${reason.trim().substring(0, 120)}` : (/started/.test(rest) ? ' — started' : ''));
              break;
            }
            case 'manifest-upload':
              detail = `backup ${shortId(kv.id)}`;
              break;
            case 'manifest-index-upload':
              detail = `${kv.count || '?'} backups indexed`;
              break;
            case 'nzb-generate':
              detail = `backup ${shortId(kv.id)} → ${kv.output || 'NZB file'}`;
              break;
            case 'download':
              detail = `${kv.downloaded || 0} fetched, ${kv.already_present || 0} already present` + (kv.host ? ` (${kv.host})` : '');
              break;
            case 'import-remote':
              detail = `${kv.imported || '?'} backups imported` + (kv.host ? ` from ${kv.host}` : '');
              break;
            case 'init':
              detail = 'encrypted repository created';
              break;
            default:
              detail = rest.substring(0, 120);
          }
          return { title, detail, failed, ts };
        }
        function renderOpSummary(lines) {
          const box = document.getElementById('opSummary');
          const items = lines.map(summarizeOpLine).filter(Boolean).slice(-10).reverse();
          if (!items.length) {
            box.innerHTML = '<p style="color:var(--text-2)">No operations recorded yet.</p>';
            return;
          }
          box.innerHTML = items.map(it => `
            <div class="row">
              <div style="font-size:16px;width:24px;text-align:center" title="${it.failed ? 'Failed' : 'Succeeded'}">${it.failed ? '✗' : '✓'}</div>
              <div class="grow">
                <div class="name">${esc(it.title)} <span class="pill${it.failed ? ' fail' : ''}">${it.failed ? 'failed' : 'ok'}</span></div>
                <div class="meta">${esc(it.detail)}<br>${esc(fmtOpTime(it.ts))} (local)</div>
              </div>
            </div>`).join('');
        }
        async function loadLog() {
          const repo = document.getElementById('repo').value;
          if (!repo) return;
          try {
            const lines = await api('/api/log?repo=' + encodeURIComponent(repo) + '&lines=60');
            document.getElementById('log').textContent = lines.join('\n') || '(empty)';
            renderOpSummary(lines);
          } catch (e) {
            document.getElementById('log').textContent = e.message;
            document.getElementById('opSummary').innerHTML = '<p style="color:var(--bad)">' + esc(e.message) + '</p>';
          }
        }
        // ---- Settings ----
        let editingJob = null;
        document.getElementById('jf-schedType').addEventListener('change', e => {
          const daily = e.target.value === 'daily';
          document.getElementById('jf-dailyWrap').style.display = daily ? '' : 'none';
          document.getElementById('jf-intervalWrap').style.display = daily ? 'none' : '';
        });
        async function loadSettings() {
          const cfg = await api('/api/config');
          const box = document.getElementById('settingsJobs');
          box.innerHTML = cfg.jobs.map(j => `
            <div class="row">
              <div class="grow">
                <div class="name">${esc(j.name)}</div>
                <div class="meta">${esc(j.schedule)} · ${j.jobType === 'disk' ? 'whole-disk image' : esc(j.mode)}${j.backupPrivilege ? ' · backup-privilege' : ''}${j.vss ? ' · vss' : ''}${(j.targets && j.targets.length ? ' → ' + j.targets.join(', ') : (j.autoUpload ? ' · auto-upload' : ''))}<br>
                ${esc(j.source)} → ${esc(j.repo)}</div>
              </div>
              <button onclick='editJob(${JSON.stringify(j.name)})'>Edit</button>
              <button onclick='deleteJob(${JSON.stringify(j.name)})'>Delete</button>
            </div>`).join('') || '<p style="color:var(--text-2)">No jobs configured.</p>';
          // Load backup locations
          renderLocations(cfg.locations || []);
          renderDestinations(cfg.locations || []);
          loadPassphraseStatus();
        }
        async function loadPassphraseStatus() {
          const el = document.getElementById('passphrase-status');
          if (!el) return;
          try {
            const s = await api('/api/config/passphrase-status');
            el.textContent = s.configured
              ? '✓ Passphrase ' + (s.source === 'environment' ? 'from environment variable' : 'stored (DPAPI-encrypted)') + (s.yubikey ? ' · YubiKey' : '')
              : '✗ No passphrase configured — scheduled jobs will fail.';
            el.style.color = s.configured ? 'var(--good)' : 'var(--bad)';
          } catch (e) { el.textContent = 'Could not check passphrase status.'; }
          loadYubiKeyStatus();
        }
        async function loadYubiKeyStatus() {
          const el = document.getElementById('pp-yubikey-status');
          if (!el) return;
          try {
            const s = await api('/api/yubikey/status');
            el.textContent = s.detected ? '✓ YubiKey detected: ' + s.devices.join(', ') : 'No YubiKey detected.';
          } catch (e) { el.textContent = ''; }
        }
        async function savePassphrase() {
          const err = document.getElementById('pp-error');
          err.style.display = 'none';
          const p1 = document.getElementById('pp-pass').value;
          const p2 = document.getElementById('pp-confirm').value;
          const yk = document.getElementById('pp-yubikey').checked;
          if (!p1) { err.textContent = 'Enter a passphrase.'; err.style.display = ''; return; }
          if (p1 !== p2) { err.textContent = 'Passphrases do not match.'; err.style.display = ''; return; }
          if (yk && !confirm('This will program YubiKey slot 2 for FileKeep challenge-response (overwriting anything in that slot). You will touch the key twice: once to program it, once to wrap the passphrase. Continue?')) return;
          const touch = document.getElementById('pp-touch');
          try {
            if (yk) { touch.style.display = ''; document.getElementById('pp-touch-detail').textContent = ' — programming slot 2, touch the key when it flashes.'; }
            await api('/api/config/passphrase', { method: 'POST', body: JSON.stringify({ passphrase: p1, yubikey: yk }) });
            document.getElementById('pp-pass').value = '';
            document.getElementById('pp-confirm').value = '';
            document.getElementById('pp-yubikey').checked = false;
            toast(yk ? 'Passphrase saved — YubiKey setup complete. Restores will ask for a touch.' : 'Passphrase saved.', 'ok');
            loadPassphraseStatus();
          } catch (e) { err.textContent = e.message; err.style.display = ''; }
          finally { touch.style.display = 'none'; }
        }
        async function clearPassphrase() {
          if (!confirm('Clear the stored passphrase? Scheduled jobs will need USENETBACKUP_PASSPHRASE set.')) return;
          try {
            await api('/api/config/passphrase', { method: 'POST', body: JSON.stringify({ passphrase: '' }) });
            loadPassphraseStatus();
          } catch (e) { toast(e.message, 'err'); }
        }
        // ---- Backup destinations: everywhere backup data is kept ----
        let cachedLocations = [];
        function renderDestinations(locations) {
          cachedLocations = locations;
          const box = document.getElementById('destinations-list');
          if (!box) return;
          const repoSel = document.getElementById('repo');
          const repoPath = repoSel && repoSel.value ? repoSel.value : '';
          const nntp = locations.filter(l => l.type === 'nntp');
          const smb = locations.filter(l => l.type === 'smb');
          let html = '';
          // Local repository
          html += `<div class="row">
            <div style="font-size:20px">🖥️</div>
            <div class="grow">
              <div class="name">Local repository <span style="font-size:11px;color:var(--text-2);text-transform:uppercase">local</span></div>
              <div class="meta" style="font-family:monospace">${repoPath ? esc(repoPath) : 'Select a repository above'}</div>
            </div>
          </div>`;
          // Usenet providers
          nntp.forEach(l => {
            html += `<div class="row">
              <div style="font-size:20px">📡</div>
              <div class="grow">
                <div class="name">${esc(l.name)} <span style="font-size:11px;color:var(--text-2);text-transform:uppercase">usenet</span></div>
                <div class="meta" style="font-family:monospace">${esc(l.host)}:${l.port}${l.ssl ? ' · SSL' : ''} · ${esc(l.newsgroup)}</div>
              </div>
            </div>`;
          });
          // Network shares
          smb.forEach(l => {
            html += `<div class="row">
              <div style="font-size:20px">💾</div>
              <div class="grow">
                <div class="name">${esc(l.name)} <span style="font-size:11px;color:var(--text-2);text-transform:uppercase">network</span></div>
                <div class="meta" style="font-family:monospace">${esc(l.share)}</div>
              </div>
            </div>`;
          });
          if (!nntp.length && !smb.length) {
            html += '<p style="color:var(--text-2)">No Usenet providers or network shares configured. Add them in <b>Backup locations</b> below.</p>';
          }
          box.innerHTML = html;
        }
        // ---- Backup locations ----
        let editingLocation = null;
        function renderLocations(locations) {
          const box = document.getElementById('locations-list');
          if (!locations.length) {
            box.innerHTML = '<p style="color:var(--text-2)">No locations yet. Add a Usenet provider or network share.</p>';
            return;
          }
          box.innerHTML = locations.map(l => {
            const icon = l.type === 'nntp' ? '📡' : '💾';
            const detail = l.type === 'nntp'
              ? `${esc(l.host)}:${l.port}${l.ssl ? ' · SSL' : ''}`
              : esc(l.share);
            const usedBy = l.usedBy && l.usedBy.length
              ? `Used by ${l.usedBy.length} job${l.usedBy.length > 1 ? 's' : ''} · `
              : 'Not used by any job · ';
            const pw = l.hasPassword ? 'credentials saved ✓' : 'no credentials saved';
            return `<div class="row">
              <div style="font-size:20px">${icon}</div>
              <div class="grow">
                <div class="name">${esc(l.name)} <span style="font-size:11px;color:var(--text-2);text-transform:uppercase">${l.type}</span></div>
                <div class="meta" style="font-family:monospace">${detail}</div>
                <div class="meta">${usedBy}${pw}</div>
              </div>
              <button onclick='editLocation(${JSON.stringify(l.name)})'>Edit</button>
              <button onclick='testLocation(${JSON.stringify(l.name)})'>Test</button>
              <button onclick='deleteLocation(${JSON.stringify(l.name)})'>Remove</button>
            </div>`;
          }).join('');
        }
        function showLocationForm(type, loc) {
          editingLocation = loc ? loc.name : null;
          document.getElementById('locationFormTitle').textContent =
            (loc ? 'Edit' : 'Add') + (type === 'nntp' ? ' Usenet provider' : ' network share');
          document.getElementById('locationFormCard').style.display = '';
          document.getElementById('lf-type').value = type;
          document.getElementById('lf-name').value = loc ? loc.name : '';
          document.getElementById('lf-name').disabled = !!loc;
          document.getElementById('lf-nntp-fields').style.display = type === 'nntp' ? '' : 'none';
          document.getElementById('lf-smb-fields').style.display = type === 'smb' ? '' : 'none';
          if (type === 'nntp') {
            document.getElementById('lf-host').value = loc ? loc.host : '';
            document.getElementById('lf-port').value = loc ? loc.port : 119;
            document.getElementById('lf-user').value = loc ? loc.username : '';
            document.getElementById('lf-pass').value = '';
            document.getElementById('lf-conn').value = loc ? loc.connections : 10;
            document.getElementById('lf-ssl').checked = loc ? !!loc.ssl : false;
          } else {
            document.getElementById('lf-share').value = loc ? loc.share : '';
            document.getElementById('lf-smbuser').value = loc ? loc.smbUser : '';
            document.getElementById('lf-smbpass').value = '';
          }
          document.getElementById('lf-error').style.display = 'none';
        }
        function hideLocationForm() {
          document.getElementById('locationFormCard').style.display = 'none';
          editingLocation = null;
        }
        async function editLocation(name) {
          const cfg = await api('/api/config');
          const loc = (cfg.locations || []).find(l => l.name === name);
          if (loc) showLocationForm(loc.type, loc);
        }
        async function saveLocation() {
          const type = document.getElementById('lf-type').value;
          const err = document.getElementById('lf-error');
          // Clear previous validation highlights.
          document.querySelectorAll('#locationFormCard .invalid').forEach(el => el.classList.remove('invalid'));
          const loc = {
            name: document.getElementById('lf-name').value.trim(),
            type,
          };
          let valid = true;
          let errMsg = 'Please fill in the highlighted fields.';
          const markInvalid = id => { document.getElementById(id).classList.add('invalid'); valid = false; };
          if (!loc.name) markInvalid('lf-name');
          if (type === 'nntp') {
            loc.host = document.getElementById('lf-host').value.trim();
            loc.port = parseInt(document.getElementById('lf-port').value) || 119;
            loc.username = document.getElementById('lf-user').value.trim();
            loc.password = document.getElementById('lf-pass').value;
            loc.connections = parseInt(document.getElementById('lf-conn').value) || 10;
            loc.ssl = document.getElementById('lf-ssl').checked;
            if (!loc.host) markInvalid('lf-host');
          } else {
            loc.share = document.getElementById('lf-share').value.trim();
            loc.smbUser = document.getElementById('lf-smbuser').value.trim();
            loc.smbPassword = document.getElementById('lf-smbpass').value;
            if (!loc.share) markInvalid('lf-share');
            else if (!isValidUnc(loc.share)) {
              markInvalid('lf-share');
              errMsg = 'Enter a valid network path like \\\\NAS\\backups (a server and a share name).';
            }
          }
          if (!valid) {
            err.textContent = errMsg;
            err.style.display = '';
            return;
          }
          try {
            await api('/api/config/locations', { method: 'POST', body: JSON.stringify(loc) });
            hideLocationForm();
            loadSettings();
            toast('Location saved.', 'ok');
          } catch (e) {
            err.textContent = e.message;
            err.style.display = '';
          }
        }
        async function deleteLocation(name) {
          if (!confirm(`Remove location '${name}'?`)) return;
          try {
            await api('/api/config/locations/' + encodeURIComponent(name), { method: 'DELETE' });
            loadSettings();
            toast('Location removed.', 'ok');
          } catch (e) { toast(e.message, 'err'); }
        }
        async function testLocation(name) {
          try {
            const r = await api('/api/locations/' + encodeURIComponent(name) + '/test', { method: 'POST' });
            toast(r.ok ? '✓ ' + r.message : '✗ ' + r.message, r.ok ? 'ok' : 'err');
          } catch (e) { toast(e.message, 'err'); }
        }
        // Renders the location checklist in the job editor.
        async function renderJobLocationChecklist(selected) {
          const box = document.getElementById('jf-location-list');
          try {
            const cfg = await api('/api/config');
            const locations = cfg.locations || [];
            if (!locations.length) {
              box.innerHTML = '<p style="color:var(--text-2)">No locations configured. Add one in <b>Backup locations</b> below.</p>';
              return;
            }
            box.innerHTML = locations.map(l => {
              const checked = selected.some(s => s.toLowerCase() === l.name.toLowerCase()) ? 'checked' : '';
              const icon = l.type === 'nntp' ? '📡' : '💾';
              return `<label class="check" style="display:block"><input type="checkbox" data-location="${esc(l.name)}" ${checked}> ${icon} ${esc(l.name)} <span style="color:var(--text-2);font-size:12px">(${l.type})</span></label>`;
            }).join('');
          } catch (e) {
            box.innerHTML = '<p style="color:var(--bad)">Could not load locations.</p>';
          }
        }
        async function showJobForm(job) {
          editingJob = job ? job.name : null;
          document.getElementById('jobFormTitle').textContent = job ? 'Edit backup job' : 'Add backup job';
          // Reset advanced section to collapsed for a clean simple view.
          document.getElementById('jf-advanced').style.display = 'none';
          document.getElementById('jf-advToggle').textContent = 'Show advanced settings ▸';
          // Load Windows user folders for quick-pick.
          loadUserFolders();
          document.getElementById('jf-name').value = job ? job.name : '';
          document.getElementById('jf-name').disabled = !!job;
          // Backup type: files & folders, or whole disk image.
          const jobType = job && job.jobType ? job.jobType : 'files';
          document.querySelector('input[name="jf-type"][value="' + jobType + '"]').checked = true;
          jobTypeChanged();
          if (jobType === 'disk') {
            await refreshDiskList();
            document.getElementById('jf-disk').value = job ? job.source : '';
          } else {
            document.getElementById('jf-source').value = job ? job.source : '';
          }
          document.getElementById('jf-repo').value = job ? job.repo : '';
          document.getElementById('jf-mode').value = job ? job.mode : 'incremental';
          document.getElementById('jf-priv').checked = job ? !!job.backupPrivilege : false;
          document.getElementById('jf-vss').checked = job ? !!job.vss : false;
          // Upload targets: location names, always required. Rendered as a checklist.
          const targets = job && job.targets && job.targets.length ? job.targets : [];
          renderJobLocationChecklist(targets);
          document.getElementById('jf-autoverify').checked = job ? !!job.autoVerify : true;
          document.getElementById('jf-redundancy').value = job && job.redundancyMode ? job.redundancyMode : 'none';
          document.getElementById('jf-verification').value = job && job.verificationMode ? job.verificationMode : 'fast';
          // Parse schedule
          const sched = job ? job.schedule : 'daily 02:00';
          if (sched.startsWith('daily ')) {
            document.getElementById('jf-schedType').value = 'daily';
            document.getElementById('jf-daily').value = sched.slice(6);
            document.getElementById('jf-dailyWrap').style.display = '';
            document.getElementById('jf-intervalWrap').style.display = 'none';
          } else if (sched.startsWith('interval ')) {
            document.getElementById('jf-schedType').value = 'interval';
            document.getElementById('jf-interval').value = sched.slice(9);
            document.getElementById('jf-dailyWrap').style.display = 'none';
            document.getElementById('jf-intervalWrap').style.display = '';
          }
          document.getElementById('jf-error').style.display = 'none';
          document.querySelectorAll('#jobFormCard .invalid').forEach(el => el.classList.remove('invalid'));
          document.getElementById('jobFormCard').style.display = '';
          document.getElementById('jobFormCard').scrollIntoView({behavior:'smooth',block:'nearest'});
        }
        function hideJobForm() {
          document.getElementById('jobFormCard').style.display = 'none';
          editingJob = null;
        }
        function editJob(name) {
          api('/api/config').then(cfg => {
            const job = cfg.jobs.find(j => j.name === name);
            if (job) showJobForm(job);
          });
        }
        async function saveJob() {
          const errBox = document.getElementById('jf-error');
          errBox.style.display = 'none';
          // Clear previous validation highlights.
          document.querySelectorAll('#jobFormCard .invalid').forEach(el => el.classList.remove('invalid'));
          const schedType = document.getElementById('jf-schedType').value;
          const dailyTime = document.getElementById('jf-daily').value;
          const intervalMin = parseInt(document.getElementById('jf-interval').value, 10);
          const checkedLocations = [...document.querySelectorAll('#jf-location-list input[data-location]:checked')]
            .map(cb => cb.getAttribute('data-location'));
          let valid = true;
          const markInvalid = id => { document.getElementById(id).classList.add('invalid'); valid = false; };
          const name = document.getElementById('jf-name').value.trim();
          const isDiskJob = document.querySelector('input[name="jf-type"]:checked').value === 'disk';
          const source = isDiskJob
            ? document.getElementById('jf-disk').value
            : document.getElementById('jf-source').value.trim();
          if (!name) markInvalid('jf-name');
          if (!source) markInvalid(isDiskJob ? 'jf-disk' : 'jf-source');
          if (schedType === 'daily') {
            if (!dailyTime) markInvalid('jf-daily');
          } else if (!intervalMin || intervalMin < 5) {
            markInvalid('jf-interval');
          }
          // Scheduled backups must upload to Usenet or a network location —
          // local-only jobs cannot be scheduled.
          if (!checkedLocations.length) {
            document.getElementById('jf-location-list').classList.add('invalid');
            valid = false;
            errBox.textContent = 'Scheduled backups must upload to Usenet or a network location \u2014 choose at least one. (Local-only backups can only be run manually, one at a time.)';
            errBox.style.display = '';
            return;
          }
          if (!valid) {
            errBox.textContent = 'Please fill in the highlighted fields.';
            errBox.style.display = '';
            return;
          }
          const schedule = schedType === 'daily' ? 'daily ' + dailyTime : 'interval ' + intervalMin;
          const job = {
            name,
            source,
            jobType: isDiskJob ? 'disk' : 'files',
            repo: document.getElementById('jf-repo').value.trim(),
            schedule,
            mode: isDiskJob ? 'full' : document.getElementById('jf-mode').value,
            backupPrivilege: document.getElementById('jf-priv').checked,
            vss: document.getElementById('jf-vss').checked,
            targets: checkedLocations,
            autoVerify: document.getElementById('jf-autoverify').checked,
            redundancyMode: document.getElementById('jf-redundancy').value,
            verificationMode: document.getElementById('jf-verification').value,
          };
          const csrf = document.querySelector('meta[name=csrf-token]').content;
          try {
            await api('/api/config/jobs', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
              body: JSON.stringify(job),
            });
            hideJobForm();
            loadSettings();
            load(); // refresh overview
            toast('Backup job saved.', 'ok');
          } catch (e) {
            errBox.textContent = e.message;
            errBox.style.display = '';
          }
        }
        async function deleteJob(name) {
          if (!confirm(`Delete backup job "${name}"?`)) return;
          const csrf = document.querySelector('meta[name=csrf-token]').content;
          try {
            await api('/api/config/jobs/' + encodeURIComponent(name), {
              method: 'DELETE',
              headers: { 'X-CSRF-Token': csrf },
            });
            loadSettings();
            load();
            toast('Backup job deleted.', 'ok');
          } catch (e) { toast(e.message, 'err'); }
        }
        async function saveNntp() {
          const errBox = document.getElementById('nntp-error');
          const nntp = {
            host: document.getElementById('nntp-host').value.trim(),
            port: parseInt(document.getElementById('nntp-port').value, 10) || 119,
            username: document.getElementById('nntp-user').value.trim(),
            password: document.getElementById('nntp-pass').value,
            ssl: document.getElementById('nntp-ssl').checked,
            connections: parseInt(document.getElementById('nntp-conn').value, 10) || 10,
          };
          const csrf = document.querySelector('meta[name=csrf-token]').content;
          try {
            await api('/api/config/nntp', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
              body: JSON.stringify(nntp),
            });
            errBox.style.display = 'none';
            loadSettings();
          } catch (e) {
            errBox.textContent = e.message;
            errBox.style.display = '';
          }
        }
        // Multi-provider management
        function renderProviders(providers) {
          const list = document.getElementById('provider-list');
          list.innerHTML = '';
          (providers || []).forEach((p, idx) => {
            const div = document.createElement('div');
            div.className = 'form-grid';
            div.style.cssText = 'border:1px solid var(--border);padding:12px;margin-bottom:8px;border-radius:6px';
            div.innerHTML = `
              <label>Host<input data-p="${idx}" data-f="host" type="text" value="${esc(p.host||'')}" placeholder="news.example.com"></label>
              <label>Port<input data-p="${idx}" data-f="port" type="number" min="1" max="65535" value="${p.port||119}"></label>
              <label>Username<input data-p="${idx}" data-f="username" type="text" value="${esc(p.username||'')}"></label>
              <label>Connections<input data-p="${idx}" data-f="connections" type="number" min="1" max="100" value="${p.connections||10}"></label>
              <label>Retention (days)<input data-p="${idx}" data-f="retentionDays" type="number" min="1" value="${p.retentionDays||1095}"></label>
              <label>Redundancy<select data-p="${idx}" data-f="redundancyMode">
                <option value="" ${!p.redundancyMode?'selected':''}>Use job default</option>
                <option value="none" ${p.redundancyMode==='none'?'selected':''}>None</option>
                <option value="xor" ${p.redundancyMode==='xor'?'selected':''}>XOR parity</option>
                <option value="par2" ${p.redundancyMode==='par2'?'selected':''}>PAR2</option>
              </select></label>
              <label class="check"><input data-p="${idx}" data-f="ssl" type="checkbox" ${p.ssl?'checked':''}> SSL</label>
              <button onclick="removeProvider(${idx})" style="grid-column:1/-1">Remove</button>
            `;
            list.appendChild(div);
          });
        }
        function addProvider() {
          const list = document.getElementById('provider-list');
          const idx = list.children.length;
          const div = document.createElement('div');
          div.className = 'form-grid';
          div.style.cssText = 'border:1px solid var(--border);padding:12px;margin-bottom:8px;border-radius:6px';
          div.innerHTML = `
            <label>Host<input data-p="${idx}" data-f="host" type="text" placeholder="news.example.com"></label>
            <label>Port<input data-p="${idx}" data-f="port" type="number" value="119"></label>
            <label>Username<input data-p="${idx}" data-f="username" type="text"></label>
            <label>Connections<input data-p="${idx}" data-f="connections" type="number" min="1" max="100" value="10"></label>
            <label>Retention (days)<input data-p="${idx}" data-f="retentionDays" type="number" value="1095"></label>
            <label>Redundancy<select data-p="${idx}" data-f="redundancyMode">
              <option value="">Use job default</option><option value="none">None</option>
              <option value="xor">XOR parity</option><option value="par2">PAR2</option>
            </select></label>
            <label class="check"><input data-p="${idx}" data-f="ssl" type="checkbox"> SSL</label>
            <button onclick="removeProvider(${idx})" style="grid-column:1/-1">Remove</button>
          `;
          list.appendChild(div);
        }
        function removeProvider(idx) {
          const list = document.getElementById('provider-list');
          if (list.children[idx]) list.children[idx].remove();
          // Re-index remaining
          Array.from(list.children).forEach((div, newIdx) => {
            div.querySelectorAll('[data-p]').forEach(el => el.setAttribute('data-p', newIdx));
            const btn = div.querySelector('button');
            if (btn) btn.setAttribute('onclick', `removeProvider(${newIdx})`);
          });
        }
        async function saveProviders() {
          const list = document.getElementById('provider-list');
          const providers = [];
          Array.from(list.children).forEach(div => {
            const p = {};
            div.querySelectorAll('[data-f]').forEach(el => {
              const f = el.getAttribute('data-f');
              if (el.type === 'checkbox') p[f] = el.checked;
              else if (el.type === 'number') p[f] = parseInt(el.value, 10) || 0;
              else p[f] = el.value.trim();
            });
            if (p.host) providers.push(p);
          });
          const csrf = document.querySelector('meta[name=csrf-token]').content;
          const status = document.getElementById('provider-status');
          try {
            await api('/api/config/nntp-providers', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
              body: JSON.stringify(providers),
            });
            status.textContent = `Saved ${providers.length} provider(s).`;
            loadSettings();
          } catch (e) {
            status.textContent = 'Error: ' + e.message;
          }
        }
        // Load settings when the tab is opened
        document.querySelector('[data-view="settings"]').addEventListener('click', loadSettings);
        load();
        setInterval(load, 30000);
        </script>
        </body>
        </html>

        """;}
