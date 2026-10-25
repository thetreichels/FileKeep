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
void WriteStartupError(string? cfgPath, string what, Exception ex)
{
    string line = $"[{DateTime.Now:O}] FATAL startup: {what}: {ex.Message}{Environment.NewLine}";
    Console.Error.WriteLine($"error: {what}: {ex.Message}");
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
        try { File.AppendAllText(candidate, line); return; } catch { /* try next */ }
    }
}

string? configPath = null;
try
{
    configPath = ArgValue(args, "--config") ?? DefaultConfigPath();

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
    WriteStartupError(configPath, $"could not load config '{configPath}'", ex);
    return 2;
}

string logDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
string logPath = Path.Combine(logDir, "service.log");
object logLock = new();
void Log(string message)
{
    string line = $"[{DateTime.Now:O}] {message}{Environment.NewLine}";
    lock (logLock)
    {
        try { File.AppendAllText(logPath, line); } catch { /* best effort */ }
    }
    if (consoleMode)
        Console.WriteLine(message);
}

FileKeep.Core.UsbDriveResolver.DiagnosticLog = Log; // MU-17: USB resolution errors reach the log
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
