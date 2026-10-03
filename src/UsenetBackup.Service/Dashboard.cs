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
        </style>
        </head>
        <body>
        <div class="app">
          <nav>
            <div class="nav-brand">
              <div class="glyph">⛁</div>
              <div><div class="t1">Usenet Backup</div><div class="t2">Service settings</div></div>
            </div>
            <a class="nav-item active" data-view="overview">Overview</a>
            <a class="nav-item" data-view="backups">Backups</a>
            <a class="nav-item" data-view="log">Operations log</a>
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
          </main>
        </div>
        <script>
        async function api(path, opts) {
          const r = await fetch(path, opts);
          if (!r.ok) throw new Error(await r.text());
          return r.status === 202 ? null : r.json();
        }
        function esc(s) { return String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }
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
          const sel = document.getElementById('repo');
          if (!sel.options.length)
            s.jobs.forEach(j => sel.add(new Option(j.name + ' — ' + j.repo, j.repo)));
          loadRepo();
          loadLog();
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
        load();
        setInterval(load, 30000);
        </script>
        </body>
        </html>

        """;}
