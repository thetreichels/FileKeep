using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Security;
using UsenetBackup.Core.Service;

namespace UsenetBackup.Service;

/// <summary>
/// DashboardApi — diagnostics, USB drives, operations log and
/// service configuration (jobs, backup locations, NNTP settings).
/// </summary>
public static partial class DashboardApi
{
    /// <summary>
    /// Runs the NNTP connectivity diagnostic synchronously (fast probes).
    /// </summary>
    public static (int Status, object Payload) TestNntpConnection(string host, int port, string user, string pass, bool ssl)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (400, new OkErrorDto(false, "host is required." ));
        try
        {
            using var client = new UsenetBackup.Core.Nntp.NntpClient(host, port, ssl);
            client.Connect();
            string greeting = client.Greeting ?? "";
            if (!string.IsNullOrEmpty(user))
            {
                client.Authenticate(user, pass);
            }
            try { client.Quit(); } catch { }
            return (200, new OkDetailDto(true, greeting.Length > 80 ? greeting[..80] + "…" : greeting ));
        }
        catch (Exception ex)
        {
            return (200, new OkErrorDto(false, ex.Message ));
        }
    }

    public static (int Status, object Payload) TestSmbConnection(string share)
    {
        if (string.IsNullOrWhiteSpace(share))
            return (400, new OkErrorDto(false, "share path is required." ));
        try
        {
            bool exists = Directory.Exists(share);
            if (!exists)
                return (200, new OkErrorDto(false, "Share not reachable or does not exist." ));
            return (200, new OkDetailDto(true, "Share is accessible." ));
        }
        catch (Exception ex)
        {
            return (200, new OkErrorDto(false, ex.Message ));
        }
    }

    public static (int Status, object Payload) RunDiagnose(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (400, new ErrorDto("host is required."));
        var results = new List<NntpProbeResultDto>();
        foreach (var (port, ssl, label) in new (int, bool, string)[]
                 { (119, false, "119/plain"), (563, true, "563/TLS"), (443, false, "443/plain") })
        {
            results.Add(new NntpProbeResultDto(label, ProbeNntp(host, port, ssl)));
        }
        bool anyOk = results.Any(r => r.Result.StartsWith("OK", StringComparison.Ordinal));
        NntpProbeResultDto? fallback = null;
        string diagnosis;
        if (anyOk)
        {
            diagnosis = $"{host} is reachable. Use port 563 (TLS) if your ISP interferes with plaintext NNTP.";
        }
        else
        {
            string fb = ProbeNntp("freenews.netfront.net", 119, false);
            fallback = new NntpProbeResultDto("freenews.netfront.net:119", fb);
            diagnosis = fb.StartsWith("OK", StringComparison.Ordinal)
                ? $"Your network CAN reach NNTP (fallback answered), so the problem is specific to {host}."
                : "No NNTP server is reachable. Your ISP or firewall is likely blocking NNTP.";
        }
        return (200, new NntpProbeResponseDto(host, results, fallback, diagnosis));
    }

    private static string ProbeNntp(string host, int port, bool ssl)
    {
        try
        {
            using var client = new UsenetBackup.Core.Nntp.NntpClient(host, port, ssl);
            client.Connect();
            string g = client.Greeting ?? "";
            try { client.Quit(); } catch { }
            return $"OK ({(g.Length <= 60 ? g : g[..60] + "…")})";
        }
        catch (Exception ex)
        {
            string msg = ex.Message ?? "";
            if (ex is TimeoutException || msg.Contains("Timed out", StringComparison.OrdinalIgnoreCase))
                return "BLOCKED/TIMEOUT (ISP or firewall may be filtering this port)";
            if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase))
                return "CONNECTION REFUSED (port closed)";
            if (msg.Contains("No such host", StringComparison.OrdinalIgnoreCase))
                return "DNS FAILED";
            return $"FAILED ({(msg.Length <= 80 ? msg : msg[..80] + "…")})";
        }
    }

    /// <summary>
    /// Lists USB-attached physical drives (Windows only).
    /// </summary>
    public static (int Status, object Payload) ListUsbDrives()
    {
        if (!OperatingSystem.IsWindows())
            return (400, new ErrorDto("USB drive listing requires Windows."));
        try
        {
            var drives = UsenetBackup.Core.Recovery.UsbDrives.List().ToArray();
            return (200, drives);
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Lists removable logical drives (USB sticks, SD cards) with their
    /// drive letters. Used by the dashboard to decide whether the
    /// "Check USB for backups" button should be shown.
    /// </summary>
    public static (int Status, object Payload) ListRemovableDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Removable && d.IsReady)
                .Select(d =>
                {
                    string label;
                    try { label = d.VolumeLabel; } catch { label = ""; }
                    return new RemovableDriveDto(d.Name, label ?? "");
                })
                .ToArray();
            return (200, drives);
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto(ex.Message));
        }
    }

    /// <summary>Returns (200, log lines) or (400, error payload).</summary>
    public static (int Status, object Payload) GetLog(ServiceConfig config, string repo, int lines = 100)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
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
            return (400, new ErrorDto("Job name is required."));
        // Default the repo to ProgramData when not specified: the repo is
        // FileKeep's internal storage and users shouldn't have to pick it.
        // ProgramData (not LocalAppData) because the service — not the user —
        // runs the backups; LocalAppData would resolve to the service account's
        // profile instead of the user's.
        if (string.IsNullOrWhiteSpace(job.Repo))
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            // Sanitize job name for use as a directory name.
            string safeName = string.Concat(job.Name.Split(Path.GetInvalidFileNameChars()));
            job.Repo = Path.Combine(programData, "FileKeep", "repos", safeName);
        }
        if (string.IsNullOrWhiteSpace(job.Source))
            return (400, new ErrorDto("Source path is required."));
        // Validate schedule format and mode via a temporary config.
        var test = new ServiceConfig
        {
            DashboardPort = config.DashboardPort,
            DashboardBind = config.DashboardBind,
            Nntp = config.Nntp,
            Locations = config.Locations,
            Jobs = config.Jobs
                .Where(j => !j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { job }).ToList(),
        };
        try { test.Validate(); }
        catch (Exception ex) { return (400, new ErrorDto(ex.Message)); }
        // Scheduled jobs must upload to at least one Usenet or network
        // location. Local-only backups are only allowed as manual one-time
        // runs, never on a schedule. This is the server-side enforcement;
        // the dashboard form and the scheduler fail closed as well.
        if (job.EffectiveTargets.Count == 0)
            return (400, new ErrorDto("Scheduled jobs must have at least one upload target (Usenet or network location)."));

        // Apply to the live config under the Jobs lock. Replace the object
        // rather than mutating in place: the scheduler may be running a job
        // with the old config object, and it must see a consistent snapshot.
        lock (config.JobsLock)
        {
            var existing = config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase));
            // Encrypt a supplied SMB password via DPAPI; blank keeps the stored blob.
            // Note: per-job SMB fields (SmbShare/SmbUser/SmbPasswordProtected) are
            // legacy — migrated to BackupLocations on config load. New jobs use
            // location names in Targets; credentials live on the location.
            string smbPwBlob = existing?.SmbPasswordProtected ?? "";
            if (!string.IsNullOrEmpty(job.SmbPasswordPlaintext))
                smbPwBlob = UsenetBackup.Core.Dpapi.Protect(job.SmbPasswordPlaintext) ?? "";
            job.SmbPasswordProtected = smbPwBlob;
            job.SmbPasswordPlaintext = null;
            if (existing is null)
                config.Jobs.Add(job);
            else
                config.Jobs[config.Jobs.IndexOf(existing)] = job;
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
            return (400, new ErrorDto(ex.Message));
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
        lock (config.JobsLock)
        {
            var existing = config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return (400, new ErrorDto($"Unknown job '{name}'." ));
            if (config.Jobs.Count == 1)
                return (400, new ErrorDto("Cannot delete the last backup job. The service requires at least one job."));
            config.Jobs.Remove(existing);
        }
        try
        {
            string json = UsenetBackup.Core.Json.FileKeepJsonOptions.Serialize(config,
                UsenetBackup.Core.Json.FileKeepJsonOptions.ServiceConfigSave,
                UsenetBackup.Core.Json.FileKeepJsonContext.Default.ServiceConfig);
            File.WriteAllText(configPath + ".tmp", json);
            File.Move(configPath + ".tmp", configPath, overwrite: true);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: job '{name}' deleted");
            return (200, new EmptyDto());
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Adds or updates a backup location (matched by name, case-insensitive).
    /// Encrypts a supplied plaintext password via DPAPI; blank keeps the
    /// existing stored blob. Returns (200, location) or (400, error).
    /// </summary>
    public static (int Status, object Payload) UpsertLocation(ServiceConfig config,
        string configPath, BackupLocation loc, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(loc.Name))
            return (400, new ErrorDto("Location name is required."));
        if (loc.Type != "nntp" && loc.Type != "smb")
            return (400, new ErrorDto("Location type must be 'nntp' or 'smb'."));
        if (loc.Type == "nntp" && string.IsNullOrWhiteSpace(loc.Host))
            return (400, new ErrorDto("Host is required for Usenet locations."));
        if (loc.Type == "smb" && string.IsNullOrWhiteSpace(loc.Share))
            return (400, new ErrorDto("Share path is required for SMB locations."));

        var existing = config.Locations.FirstOrDefault(l =>
            l.Name.Equals(loc.Name, StringComparison.OrdinalIgnoreCase));
        // Encrypt supplied passwords; blank keeps the stored blob.
        string? pwBlob = existing?.PasswordProtected;
        if (!string.IsNullOrEmpty(loc.PasswordPlaintext))
            pwBlob = UsenetBackup.Core.Dpapi.Protect(loc.PasswordPlaintext);
        string? smbPwBlob = existing?.SmbPasswordProtected;
        if (!string.IsNullOrEmpty(loc.SmbPasswordPlaintext))
            smbPwBlob = UsenetBackup.Core.Dpapi.Protect(loc.SmbPasswordPlaintext);
        loc.PasswordProtected = pwBlob;
        loc.SmbPasswordProtected = smbPwBlob;
        loc.PasswordPlaintext = null;
        loc.SmbPasswordPlaintext = null;

        if (existing is null)
            config.Locations.Add(loc);
        else
        {
            existing.Type = loc.Type;
            existing.Host = loc.Host; existing.Port = loc.Port;
            existing.Ssl = loc.Ssl; existing.Username = loc.Username;
            existing.Connections = loc.Connections;
            existing.Newsgroup = loc.Newsgroup;
            existing.RetentionDays = loc.RetentionDays;
            existing.PasswordProtected = pwBlob;
            existing.Share = loc.Share; existing.SmbUser = loc.SmbUser;
            existing.SmbPasswordProtected = smbPwBlob;
        }
        try
        {
            config.Save(configPath);
            log?.Invoke($"dashboard: location '{loc.Name}' saved");

            // Auto-scan new SMB locations for unknown backups
            if (existing is null && loc.Type == "smb")
            {
                try
                {
                    var (scanStatus, scanPayload) = ScanLocationForBackups(config, loc.Name, log);
                    // ScanLocationForBackups already logs the results
                }
                catch (Exception ex)
                {
                    log?.Invoke($"dashboard: auto-scan of new location '{loc.Name}' failed: {ex.Message}");
                }
            }

            return (200, loc);
        }
        catch (Exception ex) { return (400, new ErrorDto(ex.Message)); }
    }

    /// <summary>
    /// Deletes a location by name. Refuses if any job still targets it.
    /// Returns (200, {}) or (400, error).
    /// </summary>
    public static (int Status, object Payload) DeleteLocation(ServiceConfig config,
        string configPath, string name, Action<string>? log = null)
    {
        var existing = config.Locations.FirstOrDefault(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return (400, new ErrorDto($"Unknown location '{name}'." ));
        var usedBy = config.SnapshotJobs()
            .Where(j => j.EffectiveTargets.Any(t =>
                t.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(j => j.Name).ToList();
        if (usedBy.Count > 0)
            return (400, new ErrorDto(
                $"Location '{name}' is used by job(s): {string.Join(", ", usedBy)}. " +
                "Remove it from those jobs first."));
        config.Locations.Remove(existing);
        try
        {
            config.Save(configPath);
            log?.Invoke($"dashboard: location '{name}' deleted");
            return (200, new EmptyDto());
        }
        catch (Exception ex) { return (400, new ErrorDto(ex.Message)); }
    }

    /// <summary>
    /// Tests connectivity to a location: NNTP connect+auth, or SMB share
    /// reachability. Returns (200, { ok, message }) or (400, error).
    /// </summary>
    public static (int Status, object Payload) TestLocation(ServiceConfig config, string name)
    {
        var loc = config.Locations.FirstOrDefault(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (loc is null)
            return (400, new ErrorDto($"Unknown location '{name}'." ));
        try
        {
            if (loc.Type == "nntp")
            {
                using var client = new UsenetBackup.Core.Nntp.NntpClient(loc.Host, loc.Port, loc.Ssl);
                client.Connect();
                string? pw = Environment.GetEnvironmentVariable("USENETBACKUP_NNTP_PASSWORD");
                if (string.IsNullOrEmpty(pw) && !string.IsNullOrEmpty(loc.PasswordProtected))
                    pw = UsenetBackup.Core.Dpapi.Unprotect(loc.PasswordProtected);
                if (!string.IsNullOrEmpty(loc.Username))
                    client.Authenticate(loc.Username, pw ?? "");
                return (200, new OkMessageDto(true, $"Connected to {loc.Host}:{loc.Port}." ));
            }
            else
            {
                string? pw = Environment.GetEnvironmentVariable("USENETBACKUP_SMB_PASSWORD");
                if (string.IsNullOrEmpty(pw) && !string.IsNullOrEmpty(loc.SmbPasswordProtected))
                    pw = UsenetBackup.Core.Dpapi.Unprotect(loc.SmbPasswordProtected);
                using var share = UsenetBackup.Core.SmbShare.Connect(loc.Share, loc.SmbUser, pw);
                return (200, new OkMessageDto(true, $"Share {loc.Share} is reachable." ));
            }
        }
        catch (Exception ex)
        {
            return (200, new OkMessageDto(false, ex.Message ));
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
            // Sync the migrated "Usenet" location with the new settings so
            // scheduled uploads (which now use locations) don't go stale.
            var usenetLoc = config.Locations.FirstOrDefault(l =>
                l.Type == "nntp" && l.Name.Equals("Usenet", StringComparison.OrdinalIgnoreCase));
            if (usenetLoc is not null && config.Nntp is not null)
            {
                usenetLoc.Host = config.Nntp.Host;
                usenetLoc.Port = config.Nntp.Port;
                usenetLoc.Ssl = config.Nntp.Ssl;
                usenetLoc.Username = config.Nntp.Username;
                usenetLoc.Connections = config.Nntp.Connections;
                usenetLoc.Newsgroup = config.Nntp.Newsgroup;
                usenetLoc.RetentionDays = config.Nntp.RetentionDays;
                usenetLoc.PasswordProtected = config.Nntp.PasswordProtected;
                usenetLoc.RedundancyMode = config.Nntp.RedundancyMode;
            }
            config.Save(configPath);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: Usenet provider {(config.Nntp is null ? "cleared" : $"set to {config.Nntp.Host}")}");
            // Never return the encrypted blob to the UI; just report whether one is stored.
            object safe = config.Nntp is null
                ? new HasPasswordDto(false)
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
            return (400, new ErrorDto(ex.Message));
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
                return (400, new ErrorDto("Provider host is required."));
            if (p.Port is < 1 or > 65535)
                return (400, new ErrorDto($"Port {p.Port} is out of range." ));
            if (p.Connections is < 1 or > 100)
                return (400, new ErrorDto($"Connections must be 1-100 (got {p.Connections})." ));
        }
        // Encrypt supplied plaintext passwords via DPAPI; blank keeps the stored blob.
        // Match against existing providers by host+port to preserve blobs.
        foreach (var p in providers)
        {
            var existing = config.NntpProviders.FirstOrDefault(e =>
                e.Host.Equals(p.Host, StringComparison.OrdinalIgnoreCase) && e.Port == p.Port);
            string? blobToKeep = existing?.PasswordProtected;
            if (!string.IsNullOrEmpty(p.PasswordPlaintext))
                blobToKeep = Dpapi.Protect(p.PasswordPlaintext);
            p.PasswordProtected = blobToKeep;
            p.PasswordPlaintext = null;
        }
        config.NntpProviders = providers;
        try
        {
            config.Validate();
            config.Save(configPath);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: {providers.Count} Usenet provider(s) saved");
            return (200, new CountDto(providers.Count));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }
}
