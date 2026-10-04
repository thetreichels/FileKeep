using UsenetBackup.Core.Service;
using UsenetBackup.Service;

string? configPath = ArgValue(args, "--config")
    ?? Path.Combine(AppContext.BaseDirectory, "service.json");
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
