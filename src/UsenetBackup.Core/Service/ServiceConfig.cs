using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsenetBackup.Core.Service;

/// <summary>
/// Usenet (NNTP) provider configuration. The password is stored as a DPAPI-encrypted
/// blob (Windows only) or supplied via the USENETBACKUP_NNTP_PASSWORD environment variable.
/// The env var takes precedence when set.
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
    public int Connections { get; set; } = 10;

    [JsonPropertyName("newsgroup")]
    public string Newsgroup { get; set; } = "alt.binaries.test";

    /// <summary>
    /// Usenet retention in days for this provider. Backups uploaded longer ago
    /// than this are considered expired. Default 1095 (3 years). Set to match
    /// your provider's actual retention (varies by provider).
    /// </summary>
    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = 1095;

    /// <summary>
    /// Redundancy mode for this provider: "none", "xor", or "par2".
    /// If empty, falls back to the job's RedundancyMode.
    /// Use stronger redundancy for less reliable providers.
    /// </summary>
    [JsonPropertyName("redundancyMode")]
    public string RedundancyMode { get; set; } = "";

    /// <summary>
    /// DPAPI-encrypted password (base64). Set via the dashboard; never holds plaintext.
    /// </summary>
    [JsonPropertyName("passwordProtected")]
    public string? PasswordProtected { get; set; }

    /// <summary>
    /// Plaintext password supplied by the dashboard UI for saving. Serialized as
    /// <c>"password"</c> on input only; the API encrypts it into
    /// <see cref="PasswordProtected"/> and nulls this before persisting.
    /// Not written when null.
    /// </summary>
    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordPlaintext { get; set; }

    /// <summary>True if a password is available (stored blob or env var).</summary>
    [JsonIgnore]
    public bool HasPassword =>
        !string.IsNullOrEmpty(PasswordProtected) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USENETBACKUP_NNTP_PASSWORD"));
}

/// <summary>
/// A named backup destination: either a Usenet (NNTP) provider or an SMB
/// network share. Jobs reference locations by name in their <c>targets</c>;
/// connection details and credentials live here, in one place.
/// </summary>
public sealed class BackupLocation
{
    /// <summary>User-visible name, e.g. "Frugal Usenet" or "NAS backups". Unique.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary><c>"nntp"</c> or <c>"smb"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    // ---- NNTP fields (type == "nntp") ----

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 119;

    [JsonPropertyName("ssl")]
    public bool Ssl { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("connections")]
    public int Connections { get; set; } = 10;

    [JsonPropertyName("newsgroup")]
    public string Newsgroup { get; set; } = "alt.binaries.test";

    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = 1095;

    [JsonPropertyName("passwordProtected")]
    public string? PasswordProtected { get; set; }

    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordPlaintext { get; set; }

    // ---- SMB fields (type == "smb") ----

    /// <summary>UNC path, e.g. \\NAS\backups\filekeep.</summary>
    [JsonPropertyName("share")]
    public string Share { get; set; } = "";

    [JsonPropertyName("smbUser")]
    public string SmbUser { get; set; } = "";

    [JsonPropertyName("smbPasswordProtected")]
    public string? SmbPasswordProtected { get; set; }

    [JsonPropertyName("smbPassword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SmbPasswordPlaintext { get; set; }

    /// <summary>True if a password is available (stored blob or env var).</summary>
    [JsonIgnore]
    public bool HasPassword =>
        Type == "nntp"
            ? !string.IsNullOrEmpty(PasswordProtected) ||
              !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USENETBACKUP_NNTP_PASSWORD"))
            : !string.IsNullOrEmpty(SmbPasswordProtected) ||
              !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USENETBACKUP_SMB_PASSWORD"));
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
    [JsonPropertyName("backupPrivilege")]
    public bool BackupPrivilege { get; set; }

    /// <summary>
    /// Take a Volume Shadow Copy snapshot of the source volume and back up
    /// from the shadow copy (Windows only, admin required). Provides a
    /// point-in-time frozen view so open files back up consistently.
    /// Mutually exclusive with <see cref="BackupPrivilege"/>.
    /// </summary>
    [JsonPropertyName("vss")]
    public bool Vss { get; set; }

    /// <summary>Automatically upload the backup to Usenet via NNTP after it completes.</summary>
    [JsonPropertyName("autoUpload")]
    public bool AutoUpload { get; set; }

    /// <summary>
    /// Upload targets after a backup completes: any combination of
    /// <c>"nntp"</c> (Usenet) and <c>"smb"</c> (network share). Default is
    /// <c>["nntp"]</c> when <see cref="AutoUpload"/> is true, empty otherwise.
    /// Set to <c>["smb"]</c> for SMB-only, or <c>["nntp","smb"]</c> for both.
    /// </summary>
    [JsonPropertyName("targets")]
    public List<string>? Targets { get; set; }

    /// <summary>
    /// SMB share UNC path for the "smb" target (e.g. \\NAS\backups\filekeep).
    /// </summary>
    [JsonPropertyName("smbShare")]
    public string SmbShare { get; set; } = "";

    /// <summary>Username for the SMB share (optional).</summary>
    [JsonPropertyName("smbUser")]
    public string SmbUser { get; set; } = "";

    /// <summary>
    /// DPAPI-encrypted SMB password (like the NNTP password). Set via the
    /// dashboard; decrypted only on this machine.
    /// </summary>
    [JsonPropertyName("smbPasswordProtected")]
    public string SmbPasswordProtected { get; set; } = "";

    /// <summary>
    /// Plaintext SMB password on input only; the API encrypts it into
    /// <see cref="SmbPasswordProtected"/> and nulls this before persisting.
    /// Not written when null.
    /// </summary>
    [JsonPropertyName("smbPassword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SmbPasswordPlaintext { get; set; }

    /// <summary>
    /// Effective upload targets: explicit <see cref="Targets"/> (location
    /// names), or the legacy <see cref="AutoUpload"/> default. The scheduler
    /// resolves these against <see cref="ServiceConfig.Locations"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveTargets
    {
        get
        {
            if (Targets is { Count: > 0 })
                return Targets
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            // Legacy: AutoUpload without explicit targets. The migration in
            // ServiceConfig.MigrateToLocations rewrites this to a location
            // name on load; this fallback covers configs that bypass it.
            return AutoUpload ? new List<string> { "nntp" } : new List<string>();
        }
    }

    /// <summary>
    /// Automatically verify the backup after it completes (checks all chunk
    /// hashes). Catches bitrot and corruption early.
    /// </summary>
    [JsonPropertyName("autoVerify")]
    public bool AutoVerify { get; set; }

    /// <summary>
    /// How incremental backup determines unchanged files: "fast" (default,
    /// size+mtime metadata only), "verify" (metadata + SHA-256 check of
    /// apparent matches), or "paranoid" (SHA-256 every file).
    /// </summary>
    [JsonPropertyName("verificationMode")]
    public string VerificationMode { get; set; } = "fast";

    /// <summary>
    /// Redundancy mode for Usenet uploads: "none" (default), "xor" (single
    /// parity block per 10 chunks, recovers 1 missing), or "par2"
    /// (Reed-Solomon, recovers up to 3 missing per 10 chunks).
    /// </summary>
    [JsonPropertyName("redundancyMode")]
    public string RedundancyMode { get; set; } = "none";
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
    /// Named backup destinations (Usenet providers and SMB shares). Jobs
    /// reference these by name in their <c>targets</c>. This replaces the
    /// legacy per-job connection details and the top-level <see cref="Nntp"/>
    /// config; see <see cref="MigrateToLocations"/> for the upgrade path.
    /// </summary>
    [JsonPropertyName("locations")]
    public List<BackupLocation> Locations { get; set; } = new();

    /// <summary>
    /// Usenet provider for automatic uploads. Null/empty host means no Usenet
    /// configured; jobs with auto-upload will fail with a clear error.
    /// The password comes from USENETBACKUP_NNTP_PASSWORD, never from this file.
    /// Legacy: migrated into <see cref="Locations"/> on load.
    /// </summary>
    [JsonPropertyName("nntp")]
    public NntpConfig? Nntp { get; set; }

    /// <summary>
    /// Multiple Usenet providers for redundancy. If set, uploads go to all
    /// providers; downloads try each in order until the chunk is found.
    /// If empty, falls back to <see cref="Nntp"/> for backward compatibility.
    /// </summary>
    [JsonPropertyName("nntpProviders")]
    public List<NntpConfig> NntpProviders { get; set; } = new();

    /// <summary>
    /// Gets the effective list of Usenet providers: NntpProviders if non-empty,
    /// otherwise the legacy single Nntp config as a singleton list.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<NntpConfig> EffectiveProviders =>
        NntpProviders.Count > 0 ? NntpProviders :
        Nntp is not null ? new[] { Nntp } :
        Array.Empty<NntpConfig>();

    /// <summary>
    /// Enable automatic retention checks. When true, the scheduler periodically
    /// runs RetentionManager.CheckAndRepost to keep Usenet articles alive.
    /// Default true.
    /// </summary>
    [JsonPropertyName("retentionEnabled")]
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>
    /// Hours between automatic retention checks. Default 24 (daily).
    /// </summary>
    [JsonPropertyName("retentionCheckIntervalHours")]
    public int RetentionCheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// Backups expiring within this many days are checked for retention.
    /// Default 90.
    /// </summary>
    [JsonPropertyName("retentionWarnDays")]
    public int RetentionWarnDays { get; set; } = 90;

    /// <summary>
    /// Backups with at most this many days of retention remaining are
    /// proactively refreshed. Default 30. Set to 0 to refresh only when
    /// articles are actually missing.
    /// </summary>
    [JsonPropertyName("retentionRepostThresholdDays")]
    public int RetentionRepostThresholdDays { get; set; } = 30;

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
        config.MigrateToLocations();
        config.Validate();
        return config;
    }

    /// <summary>
    /// One-time migration from legacy config shapes to <see cref="Locations"/>:
    /// <list type="bullet">
    /// <item>The top-level <c>nntp</c> (and <c>nntpProviders</c>) become NNTP locations.</item>
    /// <item>Per-job <c>smbShare</c>/<c>smbUser</c> become SMB locations.</item>
    /// <item>Job <c>targets</c> of "nntp"/"smb" are rewritten to the new location names.</item>
    /// </list>
    /// Idempotent: if <see cref="Locations"/> is already populated, only fills gaps.
    /// </summary>
    public void MigrateToLocations()
    {
        // Migrate legacy NNTP configs.
        var nntpSources = new List<(string Name, NntpConfig Cfg)>();
        if (Nntp is not null && !string.IsNullOrWhiteSpace(Nntp.Host))
            nntpSources.Add(("Usenet", Nntp));
        foreach (var p in NntpProviders)
        {
            if (!string.IsNullOrWhiteSpace(p.Host))
                nntpSources.Add(($"Usenet {NntpProviders.IndexOf(p) + 1}", p));
        }
        foreach (var (name, cfg) in nntpSources)
        {
            if (Locations.Any(l => l.Type == "nntp" &&
                l.Host.Equals(cfg.Host, StringComparison.OrdinalIgnoreCase) && l.Port == cfg.Port))
                continue;
            string locName = name;
            int n = 2;
            while (Locations.Any(l => l.Name.Equals(locName, StringComparison.OrdinalIgnoreCase)))
                locName = $"{name} {n++}";
            Locations.Add(new BackupLocation
            {
                Name = locName, Type = "nntp",
                Host = cfg.Host, Port = cfg.Port, Ssl = cfg.Ssl,
                Username = cfg.Username, Connections = cfg.Connections,
                Newsgroup = cfg.Newsgroup, RetentionDays = cfg.RetentionDays,
                PasswordProtected = cfg.PasswordProtected,
            });
        }

        // Migrate per-job SMB settings.
        foreach (var job in Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.SmbShare))
                continue;
            var existing = Locations.FirstOrDefault(l => l.Type == "smb" &&
                l.Share.Equals(job.SmbShare, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                string locName = $"{job.Name} share";
                int n = 2;
                while (Locations.Any(l => l.Name.Equals(locName, StringComparison.OrdinalIgnoreCase)))
                    locName = $"{job.Name} share {n++}";
                existing = new BackupLocation
                {
                    Name = locName, Type = "smb",
                    Share = job.SmbShare, SmbUser = job.SmbUser,
                    SmbPasswordProtected = job.SmbPasswordProtected,
                };
                Locations.Add(existing);
            }
            // Rewrite legacy "smb" target to the location name.
            if (job.Targets is not null)
            {
                for (int i = 0; i < job.Targets.Count; i++)
                {
                    if (job.Targets[i].Equals("smb", StringComparison.OrdinalIgnoreCase))
                        job.Targets[i] = existing.Name;
                    else if (job.Targets[i].Equals("nntp", StringComparison.OrdinalIgnoreCase))
                    {
                        var nntpLoc = Locations.FirstOrDefault(l => l.Type == "nntp");
                        if (nntpLoc is not null)
                            job.Targets[i] = nntpLoc.Name;
                    }
                }
            }
            else if (job.AutoUpload)
            {
                // Legacy AutoUpload (no explicit Targets): target the NNTP
                // location, plus the SMB location migrated above (if any).
                var targets = new List<string>();
                var nntpLoc = Locations.FirstOrDefault(l => l.Type == "nntp");
                if (nntpLoc is not null)
                    targets.Add(nntpLoc.Name);
                if (existing is not null)
                    targets.Add(existing.Name);
                if (targets.Count > 0)
                    job.Targets = targets;
            }
            // Clear the legacy per-job fields (now on the location).
            job.SmbShare = "";
            job.SmbUser = "";
            job.SmbPasswordProtected = "";
        }
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
            if (job.Vss && job.BackupPrivilege)
                throw new InvalidOperationException(
                    $"Job '{job.Name}': 'vss' and 'backupPrivilege' are mutually exclusive.");
            if (job.Vss && !OperatingSystem.IsWindows())
                throw new InvalidOperationException(
                    $"Job '{job.Name}': 'vss' requires Windows.");
            if (job.AutoUpload && (Nntp is null || string.IsNullOrWhiteSpace(Nntp.Host)))
                throw new InvalidOperationException(
                    $"Job '{job.Name}' has auto-upload enabled but no Usenet provider is configured.");
        }
        if (Nntp is not null && !string.IsNullOrWhiteSpace(Nntp.Host))
        {
            if (Nntp.Port is < 1 or > 65535)
                throw new InvalidOperationException($"nntp.port {Nntp.Port} is out of range.");
            if (Nntp.Connections < 1 || Nntp.Connections > 100)
                throw new InvalidOperationException("nntp.connections must be between 1 and 100.");
        }
    }
}
