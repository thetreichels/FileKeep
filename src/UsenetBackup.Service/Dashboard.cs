using System.Security.Cryptography;
using System.Text;
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

        app.MapGet("/api/log", (string repo, int lines = 100) =>
        {
            var (status, payload) = DashboardApi.GetLog(config, repo, lines);
            return status == 200 ? Results.Json(payload) : Results.BadRequest(payload);
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
                <table id="backups"><thead><tr><th>ID</th><th>Type</th><th>Created (UTC)</th></tr></thead><tbody></tbody></table>
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
        async function api(path, opts) {
          const r = await fetch(path, opts);
          if (!r.ok) throw new Error(await r.text());
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
                <div class="meta">${esc(j.schedule)} · ${esc(j.mode)}${j.backupPrivilege ? ' · backup-privilege' : ''}<br>
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
          try {
            const bs = await api('/api/backups' + q);
            document.querySelector('#backups tbody').innerHTML = bs.map(b =>
              `<tr><td><code>${esc(b.backupId)}</code></td><td>${esc(b.type)}</td><td>${esc(b.createdUtc)}</td></tr>`).join('')
              || '<tr><td colspan="3" style="color:var(--text-2)">No backups yet.</td></tr>';
          } catch (e) { document.querySelector('#backups tbody').innerHTML = `<tr><td colspan="3">${esc(e.message)}</td></tr>`; }
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
                <div class="meta">${esc(j.schedule)} · ${esc(j.mode)}${j.backupPrivilege ? ' · backup-privilege' : ''}${j.autoUpload ? ' · auto-upload' : ''}<br>
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
