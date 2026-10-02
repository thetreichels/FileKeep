using System.Security.Cryptography;
using System.Text;
using UsenetBackup.Core;
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
        CancellationToken ct, Action<string>? log = null)
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
        <title>Usenet Backup</title>
        <style>
          body { font-family: system-ui, sans-serif; max-width: 900px; margin: 2rem auto; padding: 0 1rem; color: #222; }
          h1 { font-size: 1.4rem; }
          .job { border: 1px solid #ddd; border-radius: 8px; padding: 1rem; margin: 1rem 0; }
          .job h2 { margin: 0 0 .5rem; font-size: 1.1rem; }
          .meta { color: #555; font-size: .9rem; }
          .ok { color: #0a7a2f; font-weight: bold; }
          .fail { color: #b00020; font-weight: bold; }
          button { padding: .4rem .8rem; border-radius: 6px; border: 1px solid #999; background: #f5f5f5; cursor: pointer; }
          button:hover { background: #e8e8e8; }
          pre { background: #f6f6f6; padding: .8rem; border-radius: 6px; overflow-x: auto; font-size: .8rem; max-height: 300px; overflow-y: auto; }
          table { border-collapse: collapse; width: 100%; font-size: .9rem; }
          th, td { text-align: left; padding: .3rem .5rem; border-bottom: 1px solid #eee; }
          select { padding: .3rem; }
        </style>
        </head>
        <body>
        <h1>Usenet Backup — service dashboard</h1>
        <div id="jobs"><p>Loading…</p></div>
        <h2>Backups &amp; log</h2>
        <p><label>Repo: <select id="repo"></select></label>
        <button onclick="loadRepo()">Refresh</button></p>
        <h3>Backups</h3>
        <table id="backups"><thead><tr><th>ID</th><th>Type</th><th>Created (UTC)</th></tr></thead><tbody></tbody></table>
        <h3>Operations log</h3>
        <pre id="log"></pre>
        <script>
        async function api(path, opts) {
          const r = await fetch(path, opts);
          if (!r.ok) throw new Error(await r.text());
          return r.status === 202 ? null : r.json();
        }
        function esc(s) { return String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }
        async function load() {
          const s = await api('/api/status');
          document.getElementById('jobs').innerHTML = s.jobs.map(j => `
            <div class="job">
              <h2>${esc(j.name)} <span class="meta">(${esc(j.schedule)}, ${esc(j.mode)}${j.vss ? ', vss' : ''})</span></h2>
              <div class="meta">source: ${esc(j.source)}<br>repo: ${esc(j.repo)}</div>
              <p>Next run: ${esc(j.nextRunLocal)} (local)</p>
              <p>Last: ${j.lastResult
                ? (j.lastResult.success
                    ? `<span class="ok">OK</span> ${esc(j.lastResult.backupId)} — ${j.lastResult.files} files`
                    : `<span class="fail">FAILED</span> ${esc(j.lastResult.error)}`)
                : 'never'}${j.consecutiveFailures ? ` <span class="fail">(${j.consecutiveFailures} consecutive failures)</span>` : ''}</p>
              <button onclick="runJob('${esc(j.name)}')">Run now</button>
            </div>`).join('');
          const sel = document.getElementById('repo');
          if (!sel.options.length)
            s.jobs.forEach(j => sel.add(new Option(j.name + ' — ' + j.repo, j.repo)));
          loadRepo();
        }
        async function runJob(name) {
          const csrf = document.querySelector('meta[name=csrf-token]').content;
          await api('/api/jobs/' + encodeURIComponent(name) + '/run',
            { method: 'POST', headers: { 'X-CSRF-Token': csrf } });
          setTimeout(load, 2000);
        }
        async function loadRepo() {
          const repo = document.getElementById('repo').value;
          if (!repo) return;
          const q = '?repo=' + encodeURIComponent(repo);
          try {
            const bs = await api('/api/backups' + q);
            document.querySelector('#backups tbody').innerHTML = bs.map(b =>
              `<tr><td><code>${esc(b.backupId)}</code></td><td>${esc(b.type)}</td><td>${esc(b.createdUtc)}</td></tr>`).join('');
          } catch (e) { document.querySelector('#backups tbody').innerHTML = `<tr><td colspan="3">${esc(e.message)}</td></tr>`; }
          try {
            const lines = await api('/api/log' + q + '&lines=60');
            document.getElementById('log').textContent = lines.join('\n') || '(empty)';
          } catch (e) { document.getElementById('log').textContent = e.message; }
        }
        load();
        setInterval(load, 30000);
        </script>
        </body>
        </html>
        """;
}
