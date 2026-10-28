using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service;

static string DefaultConfigPath()
{
    if (OperatingSystem.IsWindows())
    {
        // Windows: writable machine-wide location. Program Files is read-only
        // for standard users and services shouldn't write there.
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "FileKeep");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "service.json");
    }
    // Non-Windows: keep config next to the binary (console/dev usage).
    return Path.Combine(AppContext.BaseDirectory, "service.json");
}

// NS-7: a Windows service's stderr is discarded, so a startup failure
// that only writes there is an invisible total outage (no dashboard, no
// scheduler, nothing in service.log). Every fatal startup error ALSO
// goes to service.log beside the config (falling back to the temp dir
// when even the config directory is unreachable).
void WriteStartupError(string? cfgPath, string what, Exception ex, string? auditLine = null)
{
    // Security review rec 3: exception text is sanitized before it
    // reaches any log — a subsystem that interpolates a blob or
    // token into its message must not leak it into service.log or
    // stderr. Paths and failure kinds survive sanitization (see
    // StartupErrorSanitizer); only secret-shaped content is removed.
    // Note: this startup sink keeps StartupErrorSanitizer (landed,
    // tested); the operational Log sink uses SecretScrubber, whose
    // rules differ (base32 secret shapes, hex chunk IDs preserved).
    // Both are backstops, not licenses to log secrets.
    string safeMessage = StartupErrorSanitizer.Sanitize(ex.Message);
    string line = $"[{DateTime.Now:O}] FATAL startup: {what}: {safeMessage}{Environment.NewLine}";
    Console.Error.WriteLine($"error: {what}: {safeMessage}");
    string[] candidates =
    {
        cfgPath is not null
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfgPath)) ?? ".", "service.log")
            : "",
        Path.Combine(Path.GetTempPath(), "FileKeep-service-startup-error.log"),
    };
    foreach (string candidate in candidates)
    {
        if (candidate.Length == 0) continue;
        try
        {
            File.AppendAllText(candidate, line);
            if (auditLine is not null)
                File.AppendAllText(candidate, auditLine + Environment.NewLine);
            LogFileSecurity.RestrictLogFileBestEffort(candidate);
            return;
        }
        catch { /* try next */ }
    }
}

string? configPath = null;
try
{
    // Security review rec 3: canonicalize the user-supplied config
    // path before any use, so a relative or oddly-spelled --config
    // cannot make the service read, write, or log beside a different
    // file than the operator intended. Invalid spellings throw here
    // and take the sanitized startup-error path below.
    configPath = Path.GetFullPath(ArgValue(args, "--config") ?? DefaultConfigPath());

// First-run: if the config doesn't exist, seed it from the example file
// installed in the docs subdirectory (Program Files\FileKeep\docs).
// This preserves the old workflow where users copied service.example.json to service.json.
if (!File.Exists(configPath))
{
    // AppContext.BaseDirectory ends with a separator (e.g. "C:\...\service\"),
    // and Path.GetDirectoryName on such a path returns the path itself, not
    // its parent. Trim first so we get the real parent (the install root).
    string? parentDir = Path.GetDirectoryName(
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    string examplePath = parentDir != null
        ? Path.Combine(parentDir, "docs", "service.example.json")
        : Path.Combine("docs", "service.example.json");
    if (File.Exists(examplePath))
    {
        File.Copy(examplePath, configPath);
        Console.WriteLine($"Created default config at {configPath} from example.");
    }
}
}
catch (Exception ex)
{
    WriteStartupError(configPath, "could not prepare the config file", ex);
    return 2;
}
bool consoleMode = args.Contains("--console") || !OperatingSystem.IsWindows();

ServiceConfig config;
try
{
    config = ServiceConfig.Load(configPath);
}
catch (Exception ex)
{
    WriteStartupError(configPath, $"could not load config '{configPath}'", ex,
        AuditLog.Format(AuditLog.ConfigLoad, AuditLog.Fail, $"path={configPath}"));
    return 2;
}

string logDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
string logPath = Path.Combine(logDir, "service.log");
// Security review rec 3: service.log will carry audit records, so
// restrict it at startup (file-scoped; the directory also holds the
// catalog and job state and is left alone). Best-effort: a missing
// or unwritable directory must not fail startup.
try
{
    if (!File.Exists(logPath))
        File.WriteAllText(logPath, "");
    LogFileSecurity.RestrictLogFileBestEffort(logPath);
}
catch { /* best effort */ }
object logLock = new();
void Log(string message)
{
    // Scrubbed at the sink: every operational log line passes through
    // the single scrub function, so secret-shaped content cannot leak
    // via a future log call. Paths and chunk IDs survive by design.
    string safe = SecretScrubber.Scrub(message) ?? "";
    string line = $"[{DateTime.Now:O}] {safe}{Environment.NewLine}";
    lock (logLock)
    {
        try { File.AppendAllText(logPath, line); } catch { /* best effort */ }
    }
    if (consoleMode)
        Console.WriteLine(safe);
}

FileKeep.Core.UsbDriveResolver.DiagnosticLog = Log; // MU-17: USB resolution errors reach the log
// Security review rec 6: the successful config load is itself an
// audit event (a load failure is recorded by WriteStartupError).
Log(AuditLog.Format(AuditLog.ConfigLoad, AuditLog.Ok, $"path={configPath}"));
var scheduler = new BackupScheduler(config, log: Log,
    runStateFilePath: Path.Combine(logDir, "job-state.json"));

Console.WriteLine(
    $"filekeep-service: {config.Jobs.Count} job(s); " +
    $"dashboard at http://{config.DashboardBind}:{config.DashboardPort}/");
Console.WriteLine(
    $"Scheduled backups use the stored repository secret, or the {BackupScheduler.PassphraseEnvVar} environment variable when set.");

if (consoleMode)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try
    {
        await RunAllAsync(cts.Token);
    }
    catch (OperationCanceledException) { /* Ctrl+C */ }
    return 0;
}

WindowsServiceHost.Run("FileKeep", RunAllAsync);
return 0;

async Task RunAllAsync(CancellationToken ct)
{
    // If either half dies, the whole service must die with it (SF-6) —
    // a bare WhenAll would run the scheduler forever behind a dead
    // dashboard while the host reports RUNNING.
    await ServiceSupervisor.RunTogetherAsync(
        "dashboard", t => Dashboard.RunAsync(config, scheduler, configPath, t, Log),
        "scheduler", t => scheduler.RunAsync(t),
        ct);
}

static string? ArgValue(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            return args[i + 1];
    return null;
}
