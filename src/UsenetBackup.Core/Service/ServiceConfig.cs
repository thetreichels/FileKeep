using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsenetBackup.Core.Service;

/// <summary>
/// Usenet (NNTP) provider configuration. The password is NEVER stored here —
/// it comes from the USENETBACKUP_NNTP_PASSWORD environment variable at runtime.
/// </summary>
public sealed class NntpConfig
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 119;

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("ssl")]
    public bool Ssl { get; set; }

    [JsonPropertyName("connections")]
    public int Connections { get; set; } = 2;
}

/// <summary>
/// One scheduled backup job from the service configuration file.
/// </summary>
public sealed class BackupJobConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>
    /// <c>"daily HH:mm"</c> (local time, e.g. <c>"daily 02:00"</c>) or
    /// <c>"interval N"</c> (every N minutes).
    /// </summary>
    [JsonPropertyName("schedule")]
    public string Schedule { get; set; } = "";

    /// <summary><c>"incremental"</c> (default) or <c>"full"</c>.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "incremental";

    /// <summary>Read files with Windows backup privilege to bypass exclusive locks (Windows only, admin required).</summary>
    [JsonPropertyName("backup-privilege")]
    public bool BackupPrivilege { get; set; }

    /// <summary>Automatically upload the backup to Usenet via NNTP after it completes.</summary>
    [JsonPropertyName("auto-upload")]
    public bool AutoUpload { get; set; }
}

/// <summary>
/// Service configuration, loaded from JSON (see <c>service.example.json</c>).
/// </summary>
public sealed class ServiceConfig
{
    [JsonPropertyName("dashboardPort")]
    public int DashboardPort { get; set; } = 15789;

    /// <summary>
    /// Dashboard bind address. Defaults to loopback only; the dashboard has
    /// no authentication in v0.7, so do not bind it to a public interface
    /// without a reverse proxy in front.
    /// </summary>
    [JsonPropertyName("dashboardBind")]
    public string DashboardBind { get; set; } = "127.0.0.1";

    [JsonPropertyName("jobs")]
    public List<BackupJobConfig> Jobs { get; set; } = new();

    /// <summary>
    /// Usenet provider for automatic uploads. Null/empty host means no Usenet
    /// configured; jobs with auto-upload will fail with a clear error.
    /// The password comes from USENETBACKUP_NNTP_PASSWORD, never from this file.
    /// </summary>
    [JsonPropertyName("nntp")]
    public NntpConfig? Nntp { get; set; }

    public static ServiceConfig Load(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { throw new InvalidOperationException($"Cannot read service config '{path}': {ex.Message}", ex); }
        ServiceConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ServiceConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid service config '{path}': {ex.Message}", ex); }
        if (config is null)
            throw new InvalidOperationException($"Invalid service config '{path}': empty document.");
        config.Validate();
        return config;
    }

    /// <summary>
    /// Validates and writes the configuration to <paramref name="path"/>
    /// (used by the dashboard Settings UI). Writes atomically via a temp file.
    /// </summary>
    public void Save(string path)
    {
        Validate();
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public void Validate()
    {
        if (DashboardPort is < 1 or > 65535)
            throw new InvalidOperationException($"dashboardPort {DashboardPort} is out of range.");
        if (string.IsNullOrWhiteSpace(DashboardBind))
            throw new InvalidOperationException("dashboardBind must not be empty.");
        if (Jobs.Count == 0)
            throw new InvalidOperationException("Service config defines no jobs.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Name))
                throw new InvalidOperationException("A job has no name.");
            if (!names.Add(job.Name))
                throw new InvalidOperationException($"Duplicate job name '{job.Name}'.");
            if (string.IsNullOrWhiteSpace(job.Repo))
                throw new InvalidOperationException($"Job '{job.Name}' has no repo path.");
            if (string.IsNullOrWhiteSpace(job.Source))
                throw new InvalidOperationException($"Job '{job.Name}' has no source path.");
            ScheduleParser.Parse(job.Schedule); // validates format
            if (job.Mode is not ("incremental" or "full"))
                throw new InvalidOperationException(
                    $"Job '{job.Name}' has unknown mode '{job.Mode}' (expected \"incremental\" or \"full\").");
            if (job.AutoUpload && (Nntp is null || string.IsNullOrWhiteSpace(Nntp.Host)))
                throw new InvalidOperationException(
                    $"Job '{job.Name}' has auto-upload enabled but no Usenet provider is configured.");
        }
        if (Nntp is not null && !string.IsNullOrWhiteSpace(Nntp.Host))
        {
            if (Nntp.Port is < 1 or > 65535)
                throw new InvalidOperationException($"nntp.port {Nntp.Port} is out of range.");
            if (Nntp.Connections < 1 || Nntp.Connections > 10)
                throw new InvalidOperationException("nntp.connections must be between 1 and 10.");
        }
    }
}
