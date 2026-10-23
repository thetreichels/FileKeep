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
    /// Status for a failed config mutation+save (MU-4): validation
    /// problems (InvalidOperationException from ServiceConfig.Validate)
    /// are the caller's (400); disk, permission, DPAPI and other
    /// server-side failures are 500. The two used to share 400, so a
    /// full disk looked like a bad request.
    /// </summary>
    private static int SaveFailureStatus(Exception ex) =>
        ex is InvalidOperationException ? 400 : 500;

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

    public static (int Status, object Payload) TestSmbConnection(
        string share, string? user = null, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(share))
            return (400, new OkErrorDto(false, "share path is required." ));
        try
        {
            // With credentials, test the way the product actually
            // connects — authenticated, as the given user. A bare
            // Directory.Exists runs as the service account and falsely
            // reports auth-required shares as unreachable even when the
            // typed credentials are correct (WALK-1).
            if (!string.IsNullOrWhiteSpace(user) || !string.IsNullOrEmpty(password))
            {
                using var conn = UsenetBackup.Core.SmbShare.Connect(
                    share, user, password, createIfMissing: false);
                return Directory.Exists(share)
                    ? (200, new OkDetailDto(true, "Share is accessible."))
                    : (200, new OkErrorDto(false, "Share not reachable or does not exist."));
            }
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
                    string serial = UsenetBackup.Core.UsbDriveResolver.GetVolumeSerial(d.Name) ?? "";
                    return new RemovableDriveDto(d.Name, label ?? "", serial);
                })
                .ToArray();
            return (200, drives);
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Total/free bytes for a drive root (a local root like "E:\" or a
    /// UNC share root), or null when the drive can't be read — not
    /// ready, unreachable, or not a real root. Never throws.
    /// </summary>
    public static (long Total, long Free)? ProbeDiskUsage(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return null;
        try
        {
            var drive = new DriveInfo(rootPath);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return null;
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch { return null; }
    }

    /// <summary>
    /// Disk usage for the backup locations that have a disk to measure:
    /// USB locations resolve by volume serial (absent drive = no entry)
    /// and network shares probe their UNC root as the service account
    /// (unreachable or auth-gated = no entry). Usenet locations have no
    /// disk and are skipped. Probes run in parallel under a bounded
    /// wait so one dead share can't hang the locations card — this is
    /// its own endpoint, fetched after the card renders, never part of
    /// /api/config.
    /// </summary>
    public static (int Status, object Payload) GetLocationDiskUsage(ServiceConfig config)
    {
        try
        {
            var probes = config.SnapshotLocations()
                .Where(l => l.Type is not null &&
                    (l.Type.Equals("usb", StringComparison.OrdinalIgnoreCase) ||
                     l.Type.Equals("smb", StringComparison.OrdinalIgnoreCase)))
                .Select(l => Task.Run(() =>
                {
                    string? root = l.Type!.Equals("usb", StringComparison.OrdinalIgnoreCase)
                        ? UsenetBackup.Core.UsbDriveResolver.ResolveDriveLetter(l.VolumeSerial)
                        : l.Share;
                    var usage = root is null ? null : ProbeDiskUsage(root);
                    return usage is null
                        ? null
                        : new LocationDiskDto(l.Name ?? "", usage.Value.Total, usage.Value.Free);
                }))
                .ToArray();
            Task.WaitAll(probes, TimeSpan.FromSeconds(10));
            var result = probes
                .Where(t => t.IsCompletedSuccessfully && t.Result is not null)
                .Select(t => t.Result!)
                .ToArray();
            return (200, result);
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
    /// The one place job/location DTOs are built, so every response —
    /// GET /api/config and the save endpoints alike — exposes the same
    /// shape and NEVER the DPAPI password blobs (CX-6: the save
    /// responses used to return the raw config objects, blobs included,
    /// while the snapshot deliberately stripped them).
    /// </summary>
    internal static ConfigJobDto ToDto(BackupJobConfig j) => new(
        j.Name, j.Repo, j.Source, j.Schedule, j.Mode, j.JobType,
        j.BackupPrivilege, j.Vss, j.Targets,
        j.AutoVerify, j.VerificationMode, j.RedundancyMode, j.Sources, j.Paused);

    internal static ConfigLocationDto ToDto(ServiceConfig config, BackupLocation l) => new(
        l.Name, l.Type, l.Host, l.Port, l.Username, l.Ssl, l.Connections,
        l.Newsgroup, l.RetentionDays, l.Share, l.SmbUser, l.HasPassword,
        config.SnapshotJobs()
            .Where(j => j.EffectiveTargets.Any(t =>
                t.Equals(l.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(j => j.Name).ToList(),
        l.VolumeLabel, l.UsbFolder);

    /// <summary>
    /// Adds a new job or updates an existing one (matched by name, case-insensitive).
    /// Validates, saves to disk, and reloads the scheduler. Returns (200, job DTO) or (400, error).
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
        if (job.EffectiveSources.Count == 0)
            return (400, new ErrorDto("Add at least one source folder."));
        if (job.IsDiskImage && job.EffectiveSources.Count > 1)
            return (400, new ErrorDto("Disk-image jobs back up a single device."));
        foreach (string src in job.EffectiveSources)
            if (!Directory.Exists(src))
                return (400, new ErrorDto($"Source folder does not exist: {src}"));
        try { ServiceConfig.ValidateNoNestedSources(job); }
        catch (Exception ex) { return (400, new ErrorDto(ex.Message)); }
        // Validate schedule format and mode via a temporary config.
        var test = new ServiceConfig
        {
            DashboardPort = config.DashboardPort,
            DashboardBind = config.DashboardBind,
            Nntp = config.Nntp,
            Locations = config.SnapshotLocations(),
            Jobs = config.SnapshotJobs()
                .Where(j => !j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { job }).ToList(),
        };
        try { test.Validate(); }
        catch (Exception ex) { return (400, new ErrorDto(ex.Message)); }
        // A job may REMAIN without an upload target but may not be
        // CREATED without one: scheduled jobs must upload to at least one
        // Usenet or network location (local-only backups exist only as
        // manual one-time runs). An existing job whose targets are all
        // removed — directly, or by deleting its locations — is not an
        // error; it becomes derived-paused (NeedsLocation) until a
        // location is attached again. This is the server-side
        // enforcement of the creation rule.
        bool isCreate = !config.SnapshotJobs().Any(j =>
            j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase));
        if (isCreate && job.EffectiveTargets.Count == 0)
            return (400, new ErrorDto("Scheduled jobs must have at least one upload target (Usenet or network location)."));

        // Apply to the live config under the Jobs lock. Replace the object
        // rather than mutating in place: the scheduler may be running a job
        // with the old config object, and it must see a consistent snapshot.
        lock (config.JobsLock)
        {
            var existing = config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase));
            // Jobs carry no credentials: jobs name locations in Targets,
            // and connection details + passwords live on the location.
            // (The per-job SMB fields were retired in Phase 6; nothing
            // reads them, so there is no blob to carry over here.)
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
            return (200, ToDto(job));
        }
        catch (Exception ex)
        {
            return (SaveFailureStatus(ex), new ErrorDto(ex.Message));
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
            // No last-job guard: zero jobs is a valid state (it is the
            // fresh-install state), and the GUI's per-delete confirm is
            // the accident protection. A previous guard here (7943f06)
            // was an invented invariant with no product requirement
            // behind it; removed 2026-10-20 at the user's decision.
            config.Jobs.Remove(existing);
        }
        try
        {
            // SF-20: persist through the safe path like every other
            // config mutation — Save validates, serializes under
            // JobsLock, and writes via a unique temp file. The previous
            // hand-rolled write skipped the lock and shared a fixed
            // ".tmp" name, so a save racing this delete could tear the
            // JSON or lose an update.
            config.Save(configPath);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: job '{name}' deleted");
            return (200, new EmptyDto());
        }
        catch (Exception ex)
        {
            return (SaveFailureStatus(ex), new ErrorDto(ex.Message));
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
        if (loc.Type != "nntp" && loc.Type != "smb" && loc.Type != "usb")
            return (400, new ErrorDto("Location type must be 'nntp', 'smb' or 'usb'."));
        if (loc.Type == "nntp" && string.IsNullOrWhiteSpace(loc.Host))
            return (400, new ErrorDto("Host is required for Usenet locations."));
        if (loc.Type == "smb" && string.IsNullOrWhiteSpace(loc.Share))
            return (400, new ErrorDto("Share path is required for SMB locations."));
        if (loc.Type == "usb" && string.IsNullOrWhiteSpace(loc.VolumeSerial))
            return (400, new ErrorDto("Volume serial is required for USB locations."));

        // P2-C1: lookup + field-by-field mutation under the config
        // lock — unlocked, a concurrent save/scan or a running job
        // could observe a torn location (new host, old credentials).
        BackupLocation? existing;
        lock (config.JobsLock)
        {
            existing = config.Locations.FirstOrDefault(l =>
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
                existing.VolumeSerial = loc.VolumeSerial;
                existing.VolumeLabel = loc.VolumeLabel;
                existing.UsbFolder = loc.UsbFolder;
            }
        }
        try
        {
            config.Save(configPath);
            log?.Invoke($"dashboard: location '{loc.Name}' saved");

            // Auto-scan new SMB/USB locations for unknown backups
            if (existing is null && (loc.Type == "smb" || loc.Type == "usb"))
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

            return (200, ToDto(config, loc));
        }
        catch (Exception ex) { return (SaveFailureStatus(ex), new ErrorDto(ex.Message)); }
    }

    /// <summary>
    /// Deletes a location by name. Jobs that target it are NOT a
    /// blocker: the location is stripped from their target lists. A job
    /// left with no targets stays — it becomes derived-paused
    /// (NeedsLocation) until a location is attached again, because a job
    /// may remain without a location even though it may not be created
    /// without one. Returns (200, {}) or (400, error).
    /// </summary>
    public static (int Status, object Payload) DeleteLocation(ServiceConfig config,
        BackupScheduler scheduler, string configPath, string name,
        Action<string>? log = null)
    {
        var strippedFrom = new List<string>();
        // P2-C1: the lookup and the Remove belong inside the same lock
        // as the job-target strip — both sat outside it, racing other
        // config mutations.
        lock (config.JobsLock)
        {
            var existing = config.Locations.FirstOrDefault(l =>
                l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return (400, new ErrorDto($"Unknown location '{name}'." ));
            foreach (var job in config.Jobs)
            {
                var targets = job.EffectiveTargets
                    .Where(t => !t.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (targets.Count != job.EffectiveTargets.Count)
                {
                    job.Targets = targets;
                    strippedFrom.Add(job.Name);
                }
            }
            config.Locations.Remove(existing);
        }
        try
        {
            config.Save(configPath);
            scheduler.ReloadJobs(config);
            log?.Invoke(strippedFrom.Count > 0
                ? $"dashboard: location '{name}' deleted; removed from job(s): {string.Join(", ", strippedFrom)}"
                : $"dashboard: location '{name}' deleted");
            return (200, new EmptyDto());
        }
        catch (Exception ex) { return (SaveFailureStatus(ex), new ErrorDto(ex.Message)); }
    }

    /// <summary>
    /// Sets or clears a job's stored user pause. Resuming never triggers
    /// a catch-up run: the scheduler discards occurrences that came due
    /// while the job could not run and re-arms for the next one.
    /// Returns (200, {}) or (400, error).
    /// </summary>
    public static (int Status, object Payload) SetJobPaused(ServiceConfig config,
        BackupScheduler scheduler, string configPath, string name, bool paused,
        Action<string>? log = null)
    {
        lock (config.JobsLock)
        {
            var job = config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (job is null)
                return (400, new ErrorDto($"Unknown job '{name}'." ));
            job.Paused = paused;
        }
        try
        {
            config.Save(configPath);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: job '{name}' {(paused ? "paused" : "resumed")}");
            return (200, new EmptyDto());
        }
        catch (Exception ex) { return (SaveFailureStatus(ex), new ErrorDto(ex.Message)); }
    }

    /// <summary>
    /// Attaches an existing location to a job's targets (the repair path
    /// for a job left without one). Adding the target is all it takes:
    /// the job's derived NeedsLocation pause lifts on its own, and a
    /// stored user pause, if any, is untouched. Returns (200, {}) or
    /// (400, error).
    /// </summary>
    public static (int Status, object Payload) AttachLocation(ServiceConfig config,
        BackupScheduler scheduler, string configPath, string jobName,
        string locationName, Action<string>? log = null)
    {
        lock (config.JobsLock)
        {
            if (!config.Locations.Any(l =>
                    l.Name.Equals(locationName, StringComparison.OrdinalIgnoreCase)))
                return (400, new ErrorDto($"Unknown location '{locationName}'." ));
            var job = config.Jobs.FirstOrDefault(j =>
                j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job is null)
                return (400, new ErrorDto($"Unknown job '{jobName}'." ));
            var targets = job.EffectiveTargets.ToList();
            if (!targets.Any(t => t.Equals(locationName, StringComparison.OrdinalIgnoreCase)))
            {
                targets.Add(locationName);
                job.Targets = targets;
            }
        }
        try
        {
            config.Save(configPath);
            scheduler.ReloadJobs(config);
            log?.Invoke($"dashboard: location '{locationName}' attached to job '{jobName}'");
            return (200, new EmptyDto());
        }
        catch (Exception ex) { return (SaveFailureStatus(ex), new ErrorDto(ex.Message)); }
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
            else if (loc.Type == "usb")
            {
                string? path = UsenetBackup.Core.UsbDriveResolver.ResolveBackupPath(
                    loc.VolumeSerial, loc.UsbFolder);
                if (path is not null)
                    return (200, new OkMessageDto(true, $"USB drive found at {path}." ));
                else
                    return (200, new OkMessageDto(false,
                        $"USB drive (serial {loc.VolumeSerial}) is not currently connected." ));
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
        // P2-C2: the Nntp assignment, the "Usenet" location sync, and the
        // save all mutated shared config unlocked. One lock across the
        // mutation + validate + save (Save's own lock is reentrant).
        lock (config.JobsLock)
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
            // Validate NNTP fields if a host is set (Validate() checks
            // the whole config, this included).
            config.Validate();
            // Sync the "Usenet" location with the new settings so
            // scheduled uploads (which use locations) don't go stale.
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
            // Named DTO, registered in DashboardJsonContext: this used to
            // return an anonymous type, whose metadata the trimmer strips
            // — in the published build the response body came back empty
            // (the SF-1 class of bug; the endpoint is not called by the
            // current page, which is why it survived both audit passes).
            object safe = config.Nntp is null
                ? new HasPasswordDto(false)
                : new ConfigNntpDto(
                    config.Nntp.Host, config.Nntp.Port, config.Nntp.Username,
                    config.Nntp.Ssl, config.Nntp.Connections, config.Nntp.HasPassword);
            return (200, safe);
        }
        catch (Exception ex)
        {
            return (SaveFailureStatus(ex), new ErrorDto(ex.Message));
        }
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
        // P2-C2: blob matching reads the live list and the wholesale
        // replacement swaps it — both under the config lock.
        lock (config.JobsLock)
        {
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
            return (SaveFailureStatus(ex), new ErrorDto(ex.Message));
        }
        }
    }
}
