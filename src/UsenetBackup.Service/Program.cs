using UsenetBackup.Core.Service;
using UsenetBackup.Service;

static string DefaultConfigPath()
{
    if (OperatingSystem.IsWindows())
    {
        // Windows: writable machine-wide location. Program Files is read-only
        // for standard users and services shouldn't write there.
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UsenetBackup");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "service.json");
    }
    // Non-Windows: keep config next to the binary (console/dev usage).
    return Path.Combine(AppContext.BaseDirectory, "service.json");
}

string? configPath = ArgValue(args, "--config") ?? DefaultConfigPath();

// First-run: if the config doesn't exist, seed it from the example file
// installed in the docs subdirectory (Program Files\UsenetBackup\docs).
// This preserves the old workflow where users copied service.example.json to service.json.
if (!File.Exists(configPath))
{
    string? parentDir = Path.GetDirectoryName(AppContext.BaseDirectory);
    string examplePath = parentDir != null
        ? Path.Combine(parentDir, "docs", "service.example.json")
        : Path.Combine("docs", "service.example.json");
    if (File.Exists(examplePath))
    {
        File.Copy(examplePath, configPath);
        Console.WriteLine($"Created default config at {configPath} from example.");
    }
}
bool consoleMode = args.Contains("--console") || !OperatingSystem.IsWindows();

ServiceConfig config;
try
{
    config = ServiceConfig.Load(configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
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

var scheduler = new BackupScheduler(config, log: Log);

Console.WriteLine(
    $"usenet-backup-service: {config.Jobs.Count} job(s); " +
    $"dashboard at http://{config.DashboardBind}:{config.DashboardPort}/");
Console.WriteLine(
    $"Scheduled backups need the {BackupScheduler.PassphraseEnvVar} environment variable.");

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

WindowsServiceHost.Run("UsenetBackup", RunAllAsync);
return 0;

async Task RunAllAsync(CancellationToken ct)
{
    await Task.WhenAll(
        Dashboard.RunAsync(config, scheduler, configPath, ct, Log),
        scheduler.RunAsync(ct));
}

static string? ArgValue(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            return args[i + 1];
    return null;
}
