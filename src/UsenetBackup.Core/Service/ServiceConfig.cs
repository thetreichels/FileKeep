using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsenetBackup.Core.Service;

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

    /// <summary>Take a VSS snapshot before backing up (Windows only).</summary>
    [JsonPropertyName("vss")]
    public bool Vss { get; set; }
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
        }
    }
}
