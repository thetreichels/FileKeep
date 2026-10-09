using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
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
    public static async Task RunAsync(ServiceConfig config, BackupScheduler scheduler,
        string configPath, CancellationToken ct, Action<string>? log = null)
    {
        // Per-startup CSRF token for state-changing endpoints. The dashboard
        // has no login, so without this any website you visit could trigger
        // backups via cross-origin POSTs to loopback. The token is embedded
        // in the served HTML (unreadable cross-origin) and never logged.
        string csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        log?.Invoke("dashboard CSRF token generated (embedded in served pages)");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{config.DashboardBind}:{config.DashboardPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        app.MapGet("/", () => Results.Content(DashboardHtml.Page(csrfToken), "text/html; charset=utf-8"));

        app.MapGet("/api/status", () => Results.Json(DashboardApi.GetStatus(scheduler)));

        app.MapPost("/api/jobs/{name}/run", (string name, HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!DashboardApi.IsKnownJob(scheduler, name))
                return Results.NotFound(new { error = $"Unknown job '{name}'." });
            // Run in the background; the scheduler serializes runs.
            Task.Run(() => scheduler.RunJobNow(name));
            return Results.Accepted();
        });

        // Repo-scoped endpoints only accept repos belonging to configured
        // jobs, so the dashboard cannot be pointed at arbitrary paths.
        app.MapGet("/api/backups", (string repo) =>
        {
            var (status, payload) = DashboardApi.GetBackups(config, repo);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapGet("/api/backups/remote", (string repo) =>
        {
            var (status, payload) = DashboardApi.GetRemoteBackups(config, repo);
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
            var (status, payload) = DashboardApi.ImportRemoteBackups(config, repo, ids);
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
            string repo = body.GetProperty("repo").GetString() ?? "";
            string backupId = body.GetProperty("backupId").GetString() ?? "";
            var (status, payload) = DashboardApi.StartUpload(config, scheduler, repo, backupId);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/verify", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = body.GetProperty("repo").GetString() ?? "";
            string backupId = body.GetProperty("backupId").GetString() ?? "";
            var (status, payload) = DashboardApi.StartVerify(config, repo, backupId);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/retention-check", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = body.GetProperty("repo").GetString() ?? "";
            bool dryRun = body.TryGetProperty("dryRun", out var d) && d.GetBoolean();
            var (status, payload) = DashboardApi.StartRetentionCheck(config, scheduler, repo, dryRun);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/diagnose", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string host = body.GetProperty("host").GetString() ?? "";
            var (status, payload) = DashboardApi.RunDiagnose(host);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapGet("/api/operations/usb-drives", () =>
        {
            var (status, payload) = DashboardApi.ListUsbDrives();
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/restore", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = body.GetProperty("repo").GetString() ?? "";
            string backupId = body.GetProperty("backupId").GetString() ?? "";
            string destDir = body.GetProperty("destDir").GetString() ?? "";
            var (status, payload) = DashboardApi.StartRestore(config, repo, backupId, destDir);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/backup-now", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = body.GetProperty("repo").GetString() ?? "";
            string sourceDir = body.GetProperty("sourceDir").GetString() ?? "";
            var (status, payload) = DashboardApi.StartAdhocBackup(config, repo, sourceDir);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapGet("/api/operations/nzb", (string repo, string backupId) =>
        {
            var (status, payload, fileName) = DashboardApi.GenerateNzb(config, repo, backupId);
            if (status != 200) return Results.BadRequest(payload);
            return Results.File((byte[])payload, "application/x-nzb", fileName);
        });

        app.MapPost("/api/operations/download-nzb", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart form." });
            var form = await request.ReadFormAsync();
            string repo = form["repo"].ToString();
            var file = form.Files["nzb"];
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No NZB file uploaded." });
            string tmp = Path.Combine(Path.GetTempPath(), "filekeep-" + Guid.NewGuid().ToString("N") + ".nzb");
            await using (var fs = File.Create(tmp)) await file.CopyToAsync(fs);
            var (status, payload) = DashboardApi.StartNzbDownload(config, scheduler, repo, tmp);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/disk-backup", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = body.GetProperty("repo").GetString() ?? "";
            string device = body.GetProperty("device").GetString() ?? "";
            string imageName = body.TryGetProperty("imageName", out var n) ? n.GetString() ?? "disk.img" : "disk.img";
            var (status, payload) = DashboardApi.StartDiskBackup(config, repo, device, imageName);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/disk-restore", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = body.GetProperty("repo").GetString() ?? "";
            string backupId = body.GetProperty("backupId").GetString() ?? "";
            string device = body.GetProperty("device").GetString() ?? "";
            string confirm = body.TryGetProperty("confirm", out var c) ? c.GetString() ?? "" : "";
            var (status, payload) = DashboardApi.StartDiskRestore(config, repo, backupId, device, confirm);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/init-repo", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string path = body.GetProperty("path").GetString() ?? "";
            var (status, payload) = DashboardApi.InitRepo(path);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/recovery-usb-write", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string isoPath = body.GetProperty("isoPath").GetString() ?? "";
            int driveNumber = body.TryGetProperty("driveNumber", out var d) ? d.GetInt32() : -1;
            string confirm = body.TryGetProperty("confirm", out var c) ? c.GetString() ?? "" : "";
            var (status, payload) = DashboardApi.StartUsbWrite(isoPath, driveNumber, confirm);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapGet("/api/operations/lan-server", () =>
            Results.Json(DashboardApi.GetLanServerStatus()));

        app.MapGet("/api/operations/winpe-prerequisites", () =>
            Results.Json(DashboardApi.CheckWinPePrerequisites()));

        app.MapGet("/api/operations/winpe-build-status", () =>
            Results.Json(DashboardApi.GetWinPeBuildStatus()));

        app.MapPost("/api/operations/winpe-build", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string isoPath = body.GetProperty("isoPath").GetString() ?? "";
            string? sourceDir = body.TryGetProperty("sourceDir", out var sd) ? sd.GetString() : null;
            var (status, payload) = DashboardApi.StartWinPeBuild(isoPath, sourceDir);
            return status == 202 ? Results.Json(payload, statusCode: 202) : Results.BadRequest(payload);
        });

        app.MapPost("/api/operations/lan-server/start", async (HttpRequest request) =>
        {
            if (!DashboardApi.ValidateCsrfToken(csrfToken, request.Headers["X-CSRF-Token"]))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await request.ReadFromJsonAsync<JsonElement>();
            string repo = body.GetProperty("repo").GetString() ?? "";
            int port = body.TryGetProperty("port", out var p) ? p.GetInt32() : 8477;
            var (status, payload) = DashboardApi.StartLanServer(config, repo, port);
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
                jobs = config.Jobs,
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
        config.Jobs.Any(j => string.Equals(
            Path.GetFullPath(j.Repo), Path.GetFullPath(repo), StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns (200, backup list) or (400/500, error payload).</summary>
    public static (int Status, object Payload) GetBackups(ServiceConfig config, string repo)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = $"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})." });
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
    public static (int Status, object Payload) GetRemoteBackups(ServiceConfig config, string repo)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new { error = "No Usenet provider configured." });
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = $"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})." });
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
    public static (int Status, object Payload) ImportRemoteBackups(ServiceConfig config, string repo, string[] backupIds)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new { error = "No Usenet provider configured." });
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = $"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})." });
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
    /// Starts a manual backup verification in the background.
    /// </summary>
    public static (int Status, object Payload) StartVerify(
        ServiceConfig config, string repo, string backupId)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new { error = "backupId is required." });
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = "Service passphrase is not configured." });
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
        ServiceConfig config, BackupScheduler scheduler, string repo, bool dryRun)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        Task.Run(() =>
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
        });
        OperationLog.Append(repo, "manual-retention", $"dryRun={dryRun} started");
        return (202, new { accepted = true });
    }

    /// <summary>
    /// Starts a restore of a backup to a destination directory in the background.
    /// </summary>
    public static (int Status, object Payload) StartRestore(
        ServiceConfig config, string repo, string backupId, string destDir)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new { error = "backupId is required." });
        if (string.IsNullOrWhiteSpace(destDir))
            return (400, new { error = "destDir is required." });
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = "Service passphrase is not configured." });
        Task.Run(() =>
        {
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                r.Restore(backupId, destDir);
                OperationLog.Append(repo, "manual-restore", $"backupId={backupId} dest={destDir} OK");
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
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
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
    /// Generates an NZB for a backup and returns the XML bytes.
    /// </summary>
    public static (int Status, object Payload, string FileName) GenerateNzb(
        ServiceConfig config, string repo, string backupId)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." }, "");
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new { error = "backupId is required." }, "");
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = "Service passphrase is not configured." }, "");
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
        ServiceConfig config, BackupScheduler scheduler, string repo, string nzbPath)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new { error = "No Usenet provider configured." });
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = "Service passphrase is not configured." });
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
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
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
    /// Starts a disk-image restore in the background. Destructive: requires
    /// the caller to type the device path as confirmation.
    /// </summary>
    public static (int Status, object Payload) StartDiskRestore(
        ServiceConfig config, string repo, string backupId, string device, string confirm)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new { error = "backupId is required." });
        if (string.IsNullOrWhiteSpace(device))
            return (400, new { error = "device is required." });
        if (!string.Equals(confirm?.Trim(), device, StringComparison.Ordinal))
            return (400, new { error = "Confirmation did not match the device path. Restore aborted." });
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = "Service passphrase is not configured." });
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

    /// <summary>
    /// Initializes a new repository at the given path.
    /// </summary>
    public static (int Status, object Payload) InitRepo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (400, new { error = "path is required." });
        string? passphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new { error = "Service passphrase is not configured." });
        try
        {
            using var repo = BackupRepository.Init(path, passphrase, BackupRepository.DefaultChunkSize);
            return (200, new { path = Path.GetFullPath(path) });
        }
        catch (Exception ex)
        {
            return (500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Writes a WinPE ISO to a USB drive in the background. Destructive:
    /// requires the caller to type the drive number as confirmation.
    /// Windows only; the service must run elevated.
    /// </summary>
    public static (int Status, object Payload) StartUsbWrite(
        string isoPath, int driveNumber, string confirm)
    {
        if (!OperatingSystem.IsWindows())
            return (400, new { error = "Recovery USB writing requires Windows." });
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
            return (400, new { error = "isoPath must be an existing WinPE ISO file." });
        if (driveNumber < 0)
            return (400, new { error = "driveNumber is required." });
        if (confirm?.Trim() != driveNumber.ToString())
            return (400, new { error = "Confirmation did not match the drive number. Write aborted." });
        var drives = UsenetBackup.Core.Recovery.UsbDrives.List();
        var target = drives.FirstOrDefault(d => d.Number == driveNumber);
        if (target is null)
            return (400, new { error = $"Drive {driveNumber} is not a USB drive (or not present)." });
        long isoSize = new FileInfo(isoPath).Length;
        if (isoSize > target.SizeBytes)
            return (400, new { error = "ISO does not fit on the target drive." });
        Task.Run(() =>
        {
            try
            {
                UsenetBackup.Core.Recovery.RawDiskWriter.WriteIso(target.DevicePath, isoPath);
            }
            catch (Exception ex)
            {
                OperationLog.Append("", "manual-usb-write",
                    $"drive={driveNumber} FAILED: {ex.Message}");
            }
        });
        return (202, new { accepted = true });
    }

    private static LanServer? _lanServer;

    /// <summary>WinPE ISO build state.</summary>
    private static string? _winPeBuildStatus;
    private static string? _winPeBuildError;
    private static string? _winPeBuildIsoPath;

    /// <summary>
    /// Checks prerequisites for WinPE ISO creation: Windows, admin rights,
    /// ADK Deployment Tools, and WinPE add-on.
    /// </summary>
    public static object CheckWinPePrerequisites()
    {
        var result = new Dictionary<string, object>();
        result["isWindows"] = OperatingSystem.IsWindows();
        if (!OperatingSystem.IsWindows())
        {
            result["ready"] = false;
            result["error"] = "WinPE ISO creation requires Windows.";
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

        // Probe for ADK tools
        string? copype = FindAdkTool("copype.cmd");
        string? makeWinPe = FindAdkTool("MakeWinPEMedia.cmd");
        string? oscdimg = FindAdkTool("oscdimg.exe");
        result["copype"] = copype ?? "";
        result["makeWinPeMedia"] = makeWinPe ?? "";
        result["oscdimg"] = oscdimg ?? "";
        result["adkFound"] = copype is not null && makeWinPe is not null;

        // Check for WinPE add-on (winpe.wim)
        string? winpeWim = FindWinPeWim();
        result["winpeWim"] = winpeWim ?? "";
        result["winpeAddonFound"] = winpeWim is not null;

        bool ready = isAdmin && copype is not null && makeWinPe is not null && winpeWim is not null;
        result["ready"] = ready;
        if (!ready)
        {
            var missing = new List<string>();
            if (!isAdmin) missing.Add("Administrator rights (run service as admin or elevate)");
            if (copype is null || makeWinPe is null) missing.Add("ADK Deployment Tools");
            if (winpeWim is null) missing.Add("WinPE add-on");
            result["error"] = "Missing: " + string.Join(", ", missing);
        }
        return result;
    }

    private static string? FindAdkTool(string name)
    {
        string[] kitRoots =
        {
            @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit",
            @"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit",
        };
        foreach (var root in kitRoots)
        {
            string probe = Path.Combine(root, "Deployment Tools", name);
            if (File.Exists(probe))
                return probe;
            // Also check architecture subfolders
            foreach (var arch in new[] { "amd64", "x86" })
            {
                string archProbe = Path.Combine(root, "Deployment Tools", arch, name);
                if (File.Exists(archProbe))
                    return archProbe;
            }
        }
        return null;
    }

    private static string? FindWinPeWim()
    {
        string[] kitRoots =
        {
            @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit",
            @"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit",
        };
        foreach (var root in kitRoots)
        {
            foreach (var arch in new[] { "amd64", "x86" })
            {
                string probe = Path.Combine(root, "Windows Preinstallation Environment", arch, "en-us", "winpe.wim");
                if (File.Exists(probe))
                    return probe;
            }
        }
        return null;
    }

    /// <summary>Current WinPE build status for the dashboard.</summary>
    public static object GetWinPeBuildStatus() =>
        new { status = _winPeBuildStatus ?? "idle", error = _winPeBuildError ?? "", isoPath = _winPeBuildIsoPath ?? "" };

    /// <summary>
    /// Starts a WinPE ISO build in the background. Returns 202 if accepted.
    /// </summary>
    public static (int Status, object Payload) StartWinPeBuild(string isoPath, string? sourceDir)
    {
        if (!OperatingSystem.IsWindows())
            return (400, new { error = "WinPE ISO creation requires Windows." });
        if (_winPeBuildStatus == "running")
            return (400, new { error = "A WinPE build is already running." });
        if (string.IsNullOrWhiteSpace(isoPath))
            return (400, new { error = "ISO output path is required." });

        var prereq = CheckWinPePrerequisites() as Dictionary<string, object>;
        if (prereq is null || !(prereq.TryGetValue("ready", out var ready) && ready is true))
            return (400, new { error = "Prerequisites not met: " + (prereq?["error"] ?? "unknown") });

        _winPeBuildStatus = "running";
        _winPeBuildError = null;
        _winPeBuildIsoPath = isoPath;

        Task.Run(() =>
        {
            try
            {
                // Invoke the build-winpe.ps1 script
                string scriptPath = Path.Combine(AppContext.BaseDirectory, "winpe", "build-winpe.ps1");
                // Fall back to source-relative path during development
                if (!File.Exists(scriptPath))
                {
                    // Try to find it relative to the service executable
                    string? dir = Path.GetDirectoryName(AppContext.BaseDirectory);
                    while (dir is not null && !File.Exists(Path.Combine(dir, "winpe", "build-winpe.ps1")))
                        dir = Path.GetDirectoryName(dir);
                    if (dir is not null)
                        scriptPath = Path.Combine(dir, "winpe", "build-winpe.ps1");
                }
                if (!File.Exists(scriptPath))
                    throw new FileNotFoundException("build-winpe.ps1 not found.");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -IsoPath \"{isoPath}\"" +
                        (string.IsNullOrWhiteSpace(sourceDir) ? "" : $" -SourceDir \"{sourceDir}\""),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                // Elevation: the service should already be running as admin.
                // If not, the script will fail with a clear error.
                using var proc = System.Diagnostics.Process.Start(psi)!;
                string output = proc.StandardOutput.ReadToEnd();
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException($"build-winpe.ps1 exited {proc.ExitCode}: {err}{output}");
                if (!File.Exists(isoPath))
                    throw new FileNotFoundException($"ISO not created at {isoPath}");
                _winPeBuildStatus = "complete";
                OperationLog.Append("", "winpe-build", $"iso={isoPath} complete");
            }
            catch (Exception ex)
            {
                _winPeBuildStatus = "failed";
                _winPeBuildError = ex.Message;
                OperationLog.Append("", "winpe-build", $"iso={isoPath} FAILED: {ex.Message}");
            }
        });
        return (202, new { accepted = true });
    }

    /// <summary>Current LAN server state for the dashboard.</summary>
    public static object GetLanServerStatus() =>
        new { running = _lanServer?.IsRunning == true, port = _lanServer?.Port ?? 0 };

    /// <summary>Starts the LAN chunk server for a repo.</summary>
    public static (int Status, object Payload) StartLanServer(ServiceConfig config, string repo, int port)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new { error = "Unknown repo (not a configured job)." });
        if (_lanServer?.IsRunning == true)
            return (400, new { error = "LAN server is already running." });
        if (port is < 1 or > 65535)
            return (400, new { error = "Invalid port." });
        try
        {
            _lanServer?.Dispose();
            _lanServer = new LanServer(repo, port, "0.0.0.0");
            _lanServer.Start();
            OperationLog.Append(repo, "lan-server", $"started on port {port}");
            return (200, new { running = true, port });
        }
        catch (Exception ex)
        {
            return (500, new { error = ex.Message });
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
        if (string.IsNullOrWhiteSpace(job.Repo))
            return (400, new { error = "Repo path is required." });
        if (string.IsNullOrWhiteSpace(job.Source))
            return (400, new { error = "Source path is required." });
        // Validate schedule format and mode via a temporary config.
        var test = new ServiceConfig
        {
            DashboardPort = config.DashboardPort,
            DashboardBind = config.DashboardBind,
            Nntp = config.Nntp,
            Jobs = config.Jobs
                .Where(j => !j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { job }).ToList(),
        };
        try { test.Validate(); }
        catch (Exception ex) { return (400, new { error = ex.Message }); }

        // Apply to the live config.
        var existing = config.Jobs.FirstOrDefault(j =>
            j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            config.Jobs.Add(job);
        else
        {
            existing.Repo = job.Repo;
            existing.Source = job.Source;
            existing.Schedule = job.Schedule;
            existing.Mode = job.Mode;
            existing.BackupPrivilege = job.BackupPrivilege;
            existing.Vss = job.Vss;
            existing.AutoUpload = job.AutoUpload;
            existing.AutoVerify = job.AutoVerify;
            existing.RedundancyMode = job.RedundancyMode;
            existing.VerificationMode = job.VerificationMode;
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
        var existing = config.Jobs.FirstOrDefault(j =>
            j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return (400, new { error = $"Unknown job '{name}'." });
        if (config.Jobs.Count == 1)
            return (400, new { error = "Cannot delete the last backup job. The service requires at least one job." });
        config.Jobs.Remove(existing);
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
            <a class="nav-item" data-view="operations">Operations</a>
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
                <table id="backups"><thead><tr><th>ID</th><th>Type</th><th>Created (UTC)</th><th>Actions</th></tr></thead><tbody></tbody></table>
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
            <div class="view" id="view-operations">
              <h1>Operations</h1>
              <div class="card">
                <div class="name">Retention check</div>
                <div class="meta">STAT-sample articles against provider retention; repost aging ones with fresh IDs.</div>
                <div class="row" style="margin-top:8px">
                  <select id="op-repo"></select>
                  <label class="check"><input id="op-dryrun" type="checkbox" checked> Dry run</label>
                  <button onclick="runRetentionCheck()">Run retention check</button>
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
                <div class="name">Build WinPE ISO</div>
                <div class="meta">Create a bootable WinPE recovery ISO. Requires ADK + WinPE add-on and Administrator rights.</div>
                <div class="row" style="margin-top:8px">
                  <button onclick="checkWinPePrereqs()">Check prerequisites</button>
                  <span id="winpe-prereq" style="margin-left:8px"></span>
                </div>
                <div class="row" style="margin-top:8px">
                  <input id="winpe-iso" placeholder="C:\winpe\filekeep-winpe.iso" style="flex:1">
                  <input id="winpe-src" placeholder="Source dir (optional)" style="flex:1">
                  <button onclick="startWinPeBuild()">Build ISO</button>
                </div>
                <div class="row" style="margin-top:8px">
                  <button onclick="checkWinPeStatus()">Check build status</button>
                  <span id="winpe-status" style="margin-left:8px"></span>
                </div>
              </div>
              <div class="card">
                <div class="name">Recovery USB</div>
                <div class="meta">Write a WinPE ISO to a USB drive. Destructive — all data on the drive is destroyed.</div>
                <div class="row" style="margin-top:8px">
                  <button onclick="listUsbDrives()">List USB drives</button>
                </div>
                <div id="usbDrives" style="margin-top:8px"></div>
                <div class="row" style="margin-top:8px">
                  <input id="usb-iso" placeholder="C:\winpe\filekeep-winpe.iso" style="flex:1">
                  <input id="usb-drive" placeholder="Drive #" style="width:80px">
                  <input id="usb-confirm" placeholder="Type drive # to confirm" style="width:180px">
                  <button class="danger" onclick="writeUsb()">Write ISO to USB</button>
                </div>
              </div>
              <div class="card">
                <div class="name">Restore a backup</div>
                <div class="meta">Restore a backup's files to a destination folder.</div>
                <div class="row" style="margin-top:8px">
                  <select id="restore-backup"><option value="">Select backup…</option></select>
                  <input id="restore-dest" placeholder="C:\Restore" style="flex:1">
                  <button onclick="startRestore()">Restore</button>
                </div>
              </div>
              <div class="card">
                <div class="name">Back up a folder now</div>
                <div class="meta">Ad-hoc backup of any folder into the selected repository (outside the schedule).</div>
                <div class="row" style="margin-top:8px">
                  <input id="adhoc-source" placeholder="C:\Users\You\Documents" style="flex:1">
                  <button onclick="startAdhocBackup()">Back up now</button>
                </div>
              </div>
              <div class="card">
                <div class="name">Download from NZB</div>
                <div class="meta">Fetch chunks referenced by an NZB file from Usenet into the repository.</div>
                <div class="row" style="margin-top:8px">
                  <input id="nzb-file" type="file" accept=".nzb">
                  <button onclick="uploadNzb()">Download chunks</button>
                </div>
              </div>
              <div class="card">
                <div class="name">Disk imaging</div>
                <div class="meta">Back up a whole disk to an image, or restore an image back to a disk (destructive).</div>
                <div class="row" style="margin-top:8px">
                  <input id="disk-device" placeholder="\\.\PhysicalDrive2 or /dev/sdb" style="flex:1">
                  <input id="disk-imagename" placeholder="disk.img" style="width:120px">
                  <button onclick="startDiskBackup()">Back up disk</button>
                </div>
                <div class="row" style="margin-top:8px">
                  <select id="disk-backup"><option value="">Select disk-image backup…</option></select>
                  <input id="disk-target" placeholder="Target device" style="flex:1">
                  <input id="disk-confirm" placeholder="Type device to confirm" style="width:200px">
                  <button class="danger" onclick="startDiskRestore()">Restore image to disk</button>
                </div>
              </div>
              <div class="card">
                <div class="name">New repository</div>
                <div class="meta">Initialize a fresh encrypted backup repository at a path.</div>
                <div class="row" style="margin-top:8px">
                  <input id="init-path" placeholder="D:\Backups\NewRepo" style="flex:1">
                  <button onclick="initRepo()">Initialize</button>
                </div>
              </div>
              <div class="card">
                <div class="name">LAN server</div>
                <div class="meta">Serve this repo's chunks over HTTP for LAN restores (e.g. from WinPE recovery).</div>
                <div class="row" style="margin-top:8px">
                  <input id="lan-port" placeholder="8477" style="width:100px">
                  <button onclick="startLanServer()">Start server</button>
                  <button onclick="stopLanServer()">Stop</button>
                  <span id="lan-status" style="color:var(--text-2)"></span>
                </div>
              </div>
            </div>
            <div class="view" id="view-log">
              <h1>Operations log</h1>
              <div class="card"><pre id="log">(loading…)</pre></div>
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
                <div class="form-grid">
                  <label>Name<input id="jf-name" type="text" placeholder="Documents"></label>
                  <label>Source folder<input id="jf-source" type="text" placeholder="C:\Users\You\Documents"></label>
                  <label>Repository folder<input id="jf-repo" type="text" placeholder="D:\Backups\Documents"></label>
                  <label>Schedule<select id="jf-schedType">
                    <option value="daily">Daily at…</option>
                    <option value="interval">Every N minutes</option>
                  </select></label>
                  <label id="jf-dailyWrap">Time (HH:mm)<input id="jf-daily" type="time" value="02:00"></label>
                  <label id="jf-intervalWrap" style="display:none">Minutes<input id="jf-interval" type="number" min="5" value="60"></label>
                  <label>Mode<select id="jf-mode">
                    <option value="incremental">Incremental</option>
                    <option value="full">Full</option>
                  </select></label>
                  <label class="check"><input id="jf-priv" type="checkbox"> Use backup privilege (bypass file locks, admin required)</label>
                  <label class="check"><input id="jf-vss" type="checkbox"> Use VSS shadow copy (point-in-time snapshot, admin required)</label>
                  <label class="check"><input id="jf-autoupload" type="checkbox"> Automatically upload to Usenet after backup</label>
                  <label class="check"><input id="jf-autoverify" type="checkbox" checked> Automatically verify backup after it completes</label>
                  <label>Usenet redundancy:
                    <select id="jf-redundancy">
                      <option value="none">None</option>
                      <option value="xor">XOR parity (recovers 1 missing chunk)</option>
                      <option value="par2">PAR2 (recovers up to 3 missing)</option>
                    </select>
                  </label>
                  <label>Incremental verification:
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
                <h2 style="margin-top:0">Usenet provider</h2>
                <p style="color:var(--text-2);margin-top:0">Used for automatic uploads. The password is encrypted with Windows DPAPI and stored in the config file — only this machine's service account can decrypt it. Leave blank to keep the existing saved password.</p>
                <div class="form-grid">
                  <label>Host<input id="nntp-host" type="text" placeholder="news.example.com"></label>
                  <label>Port<input id="nntp-port" type="number" min="1" max="65535" value="119"></label>
                  <label>Username<input id="nntp-user" type="text" placeholder="(optional)"></label>
                  <label>Password<input id="nntp-pass" type="password" placeholder="(unchanged)" autocomplete="new-password"></label>
                  <label>Connections<input id="nntp-conn" type="number" min="1" max="100" value="10"></label>
                  <label class="check"><input id="nntp-ssl" type="checkbox"> Use SSL (port 563)</label>
                </div>
                <div id="nntp-status" style="color:var(--text-2);margin:8px 0;font-size:13px"></div>
                <div id="nntp-error" style="color:var(--bad);margin:8px 0;display:none"></div>
                <div style="margin-top:12px;display:flex;gap:8px">
                  <button class="accent" onclick="saveNntp()">Save Usenet settings</button>
                </div>
              </div>
              <div class="card" style="margin-top:16px">
                <h3>Additional Usenet Providers (for redundancy)</h3>
                <p style="color:var(--text-2);margin-top:0">Add backup providers. Uploads go to all providers; downloads try each in order. Each provider has its own connections, retention, and redundancy settings.</p>
                <div id="provider-list"></div>
                <div style="margin-top:12px;display:flex;gap:8px">
                  <button onclick="addProvider()">Add provider</button>
                  <button class="accent" onclick="saveProviders()">Save providers</button>
                </div>
                <div id="provider-status" style="color:var(--text-2);margin:8px 0;font-size:13px"></div>
              </div>
              <div class="foot">Changes are saved to service.json and take effect immediately. The service does not need to restart.</div>
            </div>
          </main>
        </div>
        <script>
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
          // Start upload progress polling
          startUploadPolling();
          const sel = document.getElementById('repo');
          if (!sel.options.length)
            s.jobs.forEach(j => sel.add(new Option(j.name + ' — ' + j.repo, j.repo)));
          const opSel = document.getElementById('op-repo');
          if (!opSel.options.length)
            s.jobs.forEach(j => opSel.add(new Option(j.name + ' — ' + j.repo, j.repo)));
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
        async function loadRepo() {
          const repo = document.getElementById('repo').value;
          if (!repo) return;
          const q = '?repo=' + encodeURIComponent(repo);
          let bs = [];
          try {
            bs = await api('/api/backups' + q);
            document.querySelector('#backups tbody').innerHTML = bs.map(b =>
              `<tr><td><code>${esc(b.backupId)}</code></td><td>${esc(b.type)}</td><td>${esc(b.createdUtc)}</td>` +
              `<td><button onclick="uploadBackup('${esc(b.backupId)}')">Upload</button> ` +
              `<button onclick="verifyBackup('${esc(b.backupId)}')">Verify</button> ` +
              `<button onclick="downloadNzb('${esc(b.backupId)}')">NZB</button></td></tr>`).join('')
              || '<tr><td colspan="4" style="color:var(--text-2)">No backups yet.</td></tr>';
          } catch (e) { document.querySelector('#backups tbody').innerHTML = `<tr><td colspan="4">${esc(e.message)}</td></tr>`; }
          document.getElementById('remoteBackups').innerHTML = '';
          // Populate the Operations-view backup selectors.
          const rb = document.getElementById('restore-backup');
          rb.innerHTML = '<option value="">Select backup…</option>' +
            bs.map(b => `<option value="${esc(b.backupId)}">${esc(b.backupId)} (${esc(b.type)}, ${esc(b.createdUtc)})</option>`).join('');
          const db = document.getElementById('disk-backup');
          db.innerHTML = '<option value="">Select disk-image backup…</option>' +
            bs.filter(b => b.type === 'disk-image').map(b => `<option value="${esc(b.backupId)}">${esc(b.backupId)} (${esc(b.createdUtc)})</option>`).join('');
        }
        async function uploadBackup(backupId) {
          const repo = document.getElementById('repo').value;
          if (!confirm(`Upload backup ${backupId} to Usenet?`)) return;
          await api('/api/operations/upload', { method: 'POST', body: JSON.stringify({ repo, backupId }) });
          alert('Upload started in the background. Watch the Operations log.');
        }
        async function verifyBackup(backupId) {
          const repo = document.getElementById('repo').value;
          await api('/api/operations/verify', { method: 'POST', body: JSON.stringify({ repo, backupId }) });
          alert('Verification started in the background. Watch the Operations log.');
        }
        function downloadNzb(backupId) {
          const repo = document.getElementById('repo').value;
          window.location = '/api/operations/nzb?repo=' + encodeURIComponent(repo) + '&backupId=' + encodeURIComponent(backupId);
        }
        async function startRestore() {
          const repo = document.getElementById('op-repo').value;
          const backupId = document.getElementById('restore-backup').value;
          const destDir = document.getElementById('restore-dest').value.trim();
          if (!backupId) { alert('Select a backup.'); return; }
          if (!destDir) { alert('Enter a destination folder.'); return; }
          await api('/api/operations/restore', { method: 'POST', body: JSON.stringify({ repo, backupId, destDir }) });
          alert('Restore started in the background. Watch the Operations log.');
        }
        async function startAdhocBackup() {
          const repo = document.getElementById('op-repo').value;
          const sourceDir = document.getElementById('adhoc-source').value.trim();
          if (!sourceDir) { alert('Enter a source folder.'); return; }
          await api('/api/operations/backup-now', { method: 'POST', body: JSON.stringify({ repo, sourceDir }) });
          alert('Backup started in the background. Watch the Operations log.');
        }
        async function uploadNzb() {
          const repo = document.getElementById('op-repo').value;
          const input = document.getElementById('nzb-file');
          if (!input.files.length) { alert('Choose an NZB file.'); return; }
          const form = new FormData();
          form.append('repo', repo);
          form.append('nzb', input.files[0]);
          const res = await fetch('/api/operations/download-nzb', {
            method: 'POST', headers: { 'X-CSRF-Token': csrfToken }, body: form
          });
          if (!res.ok) { alert('Failed: ' + (await res.text())); return; }
          alert('NZB download started in the background. Watch the Operations log.');
        }
        async function startDiskBackup() {
          const repo = document.getElementById('op-repo').value;
          const device = document.getElementById('disk-device').value.trim();
          const imageName = document.getElementById('disk-imagename').value.trim() || 'disk.img';
          if (!device) { alert('Enter a device path.'); return; }
          await api('/api/operations/disk-backup', { method: 'POST', body: JSON.stringify({ repo, device, imageName }) });
          alert('Disk backup started in the background. Watch the Operations log.');
        }
        async function startDiskRestore() {
          const repo = document.getElementById('op-repo').value;
          const backupId = document.getElementById('disk-backup').value;
          const device = document.getElementById('disk-target').value.trim();
          const confirmText = document.getElementById('disk-confirm').value;
          if (!backupId) { alert('Select a disk-image backup.'); return; }
          if (!device) { alert('Enter the target device.'); return; }
          if (confirmText.trim() !== device) { alert('Confirmation does not match the device. Aborted.'); return; }
          if (!window.confirm('This will DESTROY all data on ' + device + '. Continue?')) return;
          await api('/api/operations/disk-restore', { method: 'POST', body: JSON.stringify({ repo, backupId, device, confirm: confirmText }) });
          alert('Disk restore started in the background. Watch the Operations log.');
        }
        async function initRepo() {
          const path = document.getElementById('init-path').value.trim();
          if (!path) { alert('Enter a path.'); return; }
          const r = await api('/api/operations/init-repo', { method: 'POST', body: JSON.stringify({ path }) });
          alert('Repository initialized at ' + r.path + '. Add it as a job in Settings to schedule backups.');
        }
        async function writeUsb() {
          const isoPath = document.getElementById('usb-iso').value.trim();
          const driveNumber = parseInt(document.getElementById('usb-drive').value, 10);
          const confirmText = document.getElementById('usb-confirm').value;
          if (!isoPath) { alert('Enter the WinPE ISO path.'); return; }
          if (isNaN(driveNumber)) { alert('Enter a drive number.'); return; }
          if (confirmText.trim() !== String(driveNumber)) { alert('Confirmation does not match the drive number. Aborted.'); return; }
          if (!window.confirm('This will DESTROY all data on drive ' + driveNumber + '. Continue?')) return;
          await api('/api/operations/recovery-usb-write', { method: 'POST', body: JSON.stringify({ isoPath, driveNumber, confirm: confirmText }) });
          alert('USB write started in the background. Watch the Operations log.');
        }
        async function refreshLanStatus() {
          try {
            const s = await api('/api/operations/lan-server');
            document.getElementById('lan-status').textContent =
              s.running ? `Running on port ${s.port}` : 'Stopped';
          } catch (e) { /* ignore */ }
        }
        async function startLanServer() {
          const repo = document.getElementById('op-repo').value;
          const port = parseInt(document.getElementById('lan-port').value, 10) || 8477;
          if (!confirm(`Start the LAN server on port ${port}? Anyone on your network can read this repo's (encrypted) chunks.`)) return;
          await api('/api/operations/lan-server/start', { method: 'POST', body: JSON.stringify({ repo, port }) });
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
          if (!ids.length) { alert('Select at least one backup.'); return; }
          await api('/api/backups/remote/import?repo=' + encodeURIComponent(repo),
            { method: 'POST', body: JSON.stringify({ backupIds: ids }) });
          alert('Imported. Refreshing…');
          loadRepo();
        }
        // ---- Operations ----
        async function runRetentionCheck() {
          const repo = document.getElementById('op-repo').value;
          const dryRun = document.getElementById('op-dryrun').checked;
          if (!repo) { alert('Select a repository.'); return; }
          if (!dryRun && !confirm('Run a live retention check (may repost articles)?')) return;
          await api('/api/operations/retention-check', { method: 'POST', body: JSON.stringify({ repo, dryRun }) });
          alert('Retention check started in the background. Watch the Operations log.');
        }
        async function runDiagnose() {
          const host = document.getElementById('op-host').value.trim();
          if (!host) { alert('Enter a hostname.'); return; }
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
        async function checkWinPePrereqs() {
          const el = document.getElementById('winpe-prereq');
          el.textContent = 'Checking…';
          try {
            const p = await api('/api/operations/winpe-prerequisites');
            if (p.ready) {
              el.innerHTML = '<span style="color:green">Ready</span>';
            } else {
              el.innerHTML = '<span style="color:red">Not ready: ' + esc(p.error || 'unknown') + '</span>';
            }
          } catch (e) { el.innerHTML = '<span style="color:red">' + esc(e.message) + '</span>'; }
        }
        async function startWinPeBuild() {
          const isoPath = document.getElementById('winpe-iso').value.trim();
          const sourceDir = document.getElementById('winpe-src').value.trim();
          if (!isoPath) { alert('Enter an ISO output path.'); return; }
          if (!confirm('Build WinPE ISO at ' + isoPath + '? This takes several minutes.')) return;
          try {
            await api('/api/operations/winpe-build', { method: 'POST', body: JSON.stringify({ isoPath, sourceDir: sourceDir || null }) });
            alert('WinPE build started. Check status for progress.');
            checkWinPeStatus();
          } catch (e) { alert('Build failed to start: ' + e.message); }
        }
        async function checkWinPeStatus() {
          const el = document.getElementById('winpe-status');
          try {
            const s = await api('/api/operations/winpe-build-status');
            let html = 'Status: <b>' + esc(s.status) + '</b>';
            if (s.isoPath) html += ' — ' + esc(s.isoPath);
            if (s.error) html += ' <span style="color:red">' + esc(s.error) + '</span>';
            el.innerHTML = html;
          } catch (e) { el.textContent = e.message; }
        }
        async function listUsbDrives() {
          const box = document.getElementById('usbDrives');
          box.innerHTML = '<p style="color:var(--text-2)">Listing…</p>';
          try {
            const ds = await api('/api/operations/usb-drives');
            box.innerHTML = ds.length
              ? '<ul>' + ds.map(d => `<li>[${d.number}] ${esc(d.model)} (${Math.round(d.sizeBytes/1048576)} MB)</li>`).join('') + '</ul>' +
                '<p style="color:var(--text-2)">Write the ISO from an admin prompt: <code>FileKeep.exe recovery-usb --iso &lt;winpe.iso&gt; --drive N</code></p>'
              : '<p style="color:var(--text-2)">No USB drives found.</p>';
          } catch (e) { box.innerHTML = `<p>${esc(e.message)}</p>`; }
        }
        async function loadLog() {
          const repo = document.getElementById('repo').value;
          if (!repo) return;
          try {
            const lines = await api('/api/log?repo=' + encodeURIComponent(repo) + '&lines=60');
            document.getElementById('log').textContent = lines.join('\n') || '(empty)';
          } catch (e) { document.getElementById('log').textContent = e.message; }
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
                <div class="meta">${esc(j.schedule)} · ${esc(j.mode)}${j.backupPrivilege ? ' · backup-privilege' : ''}${j.vss ? ' · vss' : ''}${j.autoUpload ? ' · auto-upload' : ''}<br>
                ${esc(j.source)} → ${esc(j.repo)}</div>
              </div>
              <button onclick='editJob(${JSON.stringify(j.name)})'>Edit</button>
              <button onclick='deleteJob(${JSON.stringify(j.name)})'>Delete</button>
            </div>`).join('') || '<p style="color:var(--text-2)">No jobs configured.</p>';
          // Load Usenet provider settings
          const nntp = cfg.nntp || {};
          document.getElementById('nntp-host').value = nntp.host || '';
          document.getElementById('nntp-port').value = nntp.port || 119;
          document.getElementById('nntp-user').value = nntp.username || '';
          document.getElementById('nntp-pass').value = '';
          document.getElementById('nntp-conn').value = nntp.connections || 10;
          document.getElementById('nntp-ssl').checked = !!nntp.ssl;
          document.getElementById('nntp-status').textContent =
            nntp.host ? (nntp.hasPassword ? 'Password: saved ✓' : 'Password: not set') : '';
          // Load additional providers
          renderProviders(cfg.nntpProviders || []);
        }
        function showJobForm(job) {
          editingJob = job ? job.name : null;
          document.getElementById('jobFormTitle').textContent = job ? 'Edit backup job' : 'Add backup job';
          document.getElementById('jf-name').value = job ? job.name : '';
          document.getElementById('jf-name').disabled = !!job;
          document.getElementById('jf-source').value = job ? job.source : '';
          document.getElementById('jf-repo').value = job ? job.repo : '';
          document.getElementById('jf-mode').value = job ? job.mode : 'incremental';
          document.getElementById('jf-priv').checked = job ? !!job.backupPrivilege : false;
          document.getElementById('jf-vss').checked = job ? !!job.vss : false;
          document.getElementById('jf-autoupload').checked = job ? !!job.autoUpload : false;
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
          const schedType = document.getElementById('jf-schedType').value;
          const schedule = schedType === 'daily'
            ? 'daily ' + document.getElementById('jf-daily').value
            : 'interval ' + document.getElementById('jf-interval').value;
          const job = {
            name: document.getElementById('jf-name').value.trim(),
            source: document.getElementById('jf-source').value.trim(),
            repo: document.getElementById('jf-repo').value.trim(),
            schedule,
            mode: document.getElementById('jf-mode').value,
            backupPrivilege: document.getElementById('jf-priv').checked,
            vss: document.getElementById('jf-vss').checked,
            autoUpload: document.getElementById('jf-autoupload').checked,
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
          } catch (e) { alert(e.message); }
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
