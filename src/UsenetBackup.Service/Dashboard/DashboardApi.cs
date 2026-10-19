using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Security;
using UsenetBackup.Core.Service;

namespace UsenetBackup.Service;

/// <summary>
/// DashboardApi — Core dashboard endpoints: status, jobs, repositories, backups,
/// uploads, restores, retention checks and NZB handling.
/// </summary>
public static partial class DashboardApi
{
    public static StatusDto GetStatus(BackupScheduler scheduler) => new(
        scheduler.StartedLocal,
        scheduler.Jobs.Select(j => new StatusJobDto(
            j.Config.Name,
            j.Config.Repo,
            j.Config.Source,
            j.Schedule.ToString(),
            j.Config.Mode,
            j.Config.BackupPrivilege,
            j.Config.Vss,
            j.NextRunLocal,
            j.ConsecutiveFailures,
            j.LastResult is null ? null : new StatusLastResultDto(
                j.LastResult.StartedLocal,
                j.LastResult.Success,
                j.LastResult.BackupId,
                j.LastResult.Files,
                j.LastResult.Error))).ToList());

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
        config.SnapshotJobs().Any(j => string.Equals(
            Path.GetFullPath(j.Repo), Path.GetFullPath(repo), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// All distinct repository paths from the configured backup jobs, in
    /// job order. Tools operate across these without asking the user to
    /// pick one — the repo is FileKeep's internal storage.
    /// </summary>
    public static IReadOnlyList<string> AllJobRepos(ServiceConfig config) =>
        config.SnapshotJobs()
            .Select(j => j.Repo)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>First configured job's repo (the primary one), or null when no jobs exist.</summary>
    public static string? PrimaryRepo(ServiceConfig config) =>
        AllJobRepos(config).FirstOrDefault();

    /// <summary>
    /// Lists all physical drives (Windows only) for disk pickers.
    /// </summary>
    public static (int Status, object Payload) ListPhysicalDrives()
    {
        if (!OperatingSystem.IsWindows())
            return (400, new ErrorDto("Drive listing requires Windows."));
        try
        {
            // Return the named PhysicalDrive records directly (not anonymous types)
            // so System.Text.Json can serialize them under trimming.
            var drives = UsenetBackup.Core.Recovery.PhysicalDrives.List().ToArray();
            return (200, drives);
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto(ex.Message));
        }
    }

    /// <summary>Returns (200, backup list) or (400/500, error payload).</summary>
    public static (int Status, object Payload) GetBackups(ServiceConfig config, string repo, RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."))
                : (500, new ErrorDto($"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})." ));
        try
        {
            using var r = BackupRepository.Open(repo, passphrase);
            return (200, r.ListBackups().OrderByDescending(b => b.CreatedUtc).ToArray());
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// All backup IDs known to FileKeep across every configured job's local
    /// repo. Uses BackupDiscovery.ScanPath (manifests are plain JSON at rest,
    /// no passphrase needed) so this works even when the restore session is
    /// locked.
    /// </summary>
    private static HashSet<string> GetKnownBackupIds(ServiceConfig config, Action<string>? log = null)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string repo in AllJobRepos(config))
        {
            try
            {
                foreach (var b in UsenetBackup.Core.BackupDiscovery.ScanPath(repo, log))
                    known.Add(b.BackupId);
            }
            catch (Exception ex)
            {
                log?.Invoke($"dashboard: cannot scan local repo {repo} for known backups: {ex.Message}");
            }
        }
        return known;
    }

    /// <summary>
    /// Scans a configured backup location for FileKeep backups not already
    /// known locally. For SMB locations, connects to the share and scans its
    /// manifests/ directory. Returns only the unknown backups (not already
    /// shown in the Banked Vault).
    /// </summary>
    public static (int Status, object Payload) ScanLocationForBackups(
        ServiceConfig config, string name, Action<string>? log = null)
    {
        var loc = config.Locations.FirstOrDefault(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (loc is null)
            return (400, new ErrorDto($"Unknown location '{name}'."));

        IReadOnlyList<UsenetBackup.Core.BackupDiscovery.DiscoveredBackup> found;
        string scannedPath;
        if (loc.Type == "smb")
        {
            string? smbPassword = null;
            if (!string.IsNullOrEmpty(loc.SmbPasswordProtected))
            {
                try { smbPassword = UsenetBackup.Core.Dpapi.Unprotect(loc.SmbPasswordProtected); }
                catch { /* use no password */ }
            }
            scannedPath = loc.Share;
            found = UsenetBackup.Core.BackupDiscovery.ScanSmbShare(
                loc.Share, loc.SmbUser, smbPassword, log);
        }
        else
        {
            return (400, new ErrorDto(
                $"Location '{name}' is type '{loc.Type}' — backup scanning is only supported for SMB locations. " +
                "Use /api/backups/remote to discover Usenet backups."));
        }

        var known = GetKnownBackupIds(config, log);
        var unknown = found
            .Where(b => !known.Contains(b.BackupId))
            .Select(b => new DiscoveredBackupDto(b.BackupId, b.Type, b.CreatedUtc, b.Source, b.LocationPath))
            .ToList();

        log?.Invoke($"dashboard: scanned location '{name}' ({scannedPath}): {found.Count} found, {unknown.Count} unknown.");
        return (200, new ScanBackupsResultDto(scannedPath, found.Count, unknown));
    }

    /// <summary>
    /// Scans a USB drive path (e.g. "E:\\" or "E:\\backups") for FileKeep
    /// backups not already known locally. Returns only the unknown ones.
    /// </summary>
    public static (int Status, object Payload) ScanUsbForBackups(
        ServiceConfig config, string drivePath, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return (400, new ErrorDto("Drive path is required."));

        // Basic guard: only allow scanning local drive paths, not UNC or
        // arbitrary network locations, to avoid path confusion.
        string full = Path.GetFullPath(drivePath);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return (400, new ErrorDto("Use the SMB location scan for network shares."));

        var found = UsenetBackup.Core.BackupDiscovery.ScanPath(full, log);
        var known = GetKnownBackupIds(config, log);
        var unknown = found
            .Where(b => !known.Contains(b.BackupId))
            .Select(b => new DiscoveredBackupDto(b.BackupId, b.Type, b.CreatedUtc, b.Source, b.LocationPath))
            .ToList();

        log?.Invoke($"dashboard: scanned USB path '{full}': {found.Count} found, {unknown.Count} unknown.");
        return (200, new ScanBackupsResultDto(full, found.Count, unknown));
    }

    /// <summary>
    /// Imports a discovered backup manifest from an SMB share or USB drive
    /// into the local repo's manifests directory, so it appears in the
    /// Banked Vault list. The manifest file is copied from
    /// &lt;sourcePath&gt;/manifests/&lt;backupId&gt;.json to
    /// &lt;repo&gt;/manifests/&lt;backupId&gt;.json.
    /// Returns (200, {}) or (400/500, error).
    /// </summary>
    public static (int Status, object Payload) ImportDiscoveredBackup(
        ServiceConfig config, string repo, string backupId, string sourcePath, Action<string>? log = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new ErrorDto("Backup ID is required."));
        if (string.IsNullOrWhiteSpace(sourcePath))
            return (400, new ErrorDto("Source path is required."));

        // Sanitize backupId to prevent path traversal
        if (backupId.Contains("..") || backupId.Contains("/") || backupId.Contains("\\"))
            return (400, new ErrorDto("Invalid backup ID."));

        string sourceManifest = Path.Combine(sourcePath, "manifests", backupId + ".json");
        if (!File.Exists(sourceManifest))
            return (400, new ErrorDto($"Manifest not found at {sourceManifest}."));

        string destDir = Path.Combine(repo, "manifests");
        string destManifest = Path.Combine(destDir, backupId + ".json");

        try
        {
            Directory.CreateDirectory(destDir);
            File.Copy(sourceManifest, destManifest, overwrite: true);
            log?.Invoke($"dashboard: imported discovered backup '{backupId}' from '{sourcePath}' into repo '{repo}'.");
            return (200, new EmptyDto());
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto($"Failed to import backup: {ex.Message}"));
        }
    }

    /// <summary>
    /// Discovers backup manifests posted to Usenet (the encrypted manifest
    /// index). Returns manifests not present locally — "all backups FileKeep
    /// is aware of" beyond the local repo. Uses the first configured provider.
    /// </summary>
    public static (int Status, object Payload) GetRemoteBackups(ServiceConfig config, string repo, RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new ErrorDto("No Usenet provider configured."));
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."))
                : (500, new ErrorDto($"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})." ));
        try
        {
            string? nntpPassword = Environment.GetEnvironmentVariable(BackupScheduler.NntpPasswordEnvVar);
            if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(provider.PasswordProtected))
                nntpPassword = Dpapi.Unprotect(provider.PasswordProtected);

            using var client = new UsenetBackup.Core.Nntp.NntpClient(provider.Host, provider.Port, provider.Ssl);
            client.Connect();
            if (!string.IsNullOrEmpty(provider.Username))
                client.Authenticate(provider.Username, nntpPassword ?? "");

            using var r = BackupRepository.Open(repo, passphrase);
            string providerKey = UsenetBackup.Core.Nntp.ChunkMessageIndex.MakeProviderKey(provider.Host, provider.Newsgroup);
            using var store = new UsenetBackup.Core.Nntp.NntpBlobStore(
                client, provider.Newsgroup, r.RepoId, r.CatalogPath,
                messageIndex: r.MessageIndex, providerKey: providerKey);
            var remote = r.DiscoverRemoteManifests(store);
            var localIds = new HashSet<string>(r.ListBackups().Select(b => b.BackupId));
            var fresh = remote
                .Where(m => !localIds.Contains(m.Manifest.BackupId))
                .Select(m => new
                {
                    backupId = m.Manifest.BackupId,
                    createdUtc = m.Manifest.CreatedUtc,
                    type = m.Manifest.Type,
                    source = m.Manifest.Source,
                    fileCount = m.Manifest.Files.Count,
                    snapshot = m.Manifest.Snapshot,
                })
                .OrderByDescending(m => m.createdUtc)
                .ToArray();
            return (200, fresh);
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Imports remote-discovered manifests into the local repo so they can
    /// be downloaded/restored like local backups.
    /// </summary>
    public static (int Status, object Payload) ImportRemoteBackups(ServiceConfig config, string repo, string[] backupIds, RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new ErrorDto("No Usenet provider configured."));
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."))
                : (500, new ErrorDto($"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})." ));
        try
        {
            string? nntpPassword = Environment.GetEnvironmentVariable(BackupScheduler.NntpPasswordEnvVar);
            if (string.IsNullOrEmpty(nntpPassword) && !string.IsNullOrEmpty(provider.PasswordProtected))
                nntpPassword = Dpapi.Unprotect(provider.PasswordProtected);

            using var client = new UsenetBackup.Core.Nntp.NntpClient(provider.Host, provider.Port, provider.Ssl);
            client.Connect();
            if (!string.IsNullOrEmpty(provider.Username))
                client.Authenticate(provider.Username, nntpPassword ?? "");

            using var r = BackupRepository.Open(repo, passphrase);
            string providerKey = UsenetBackup.Core.Nntp.ChunkMessageIndex.MakeProviderKey(provider.Host, provider.Newsgroup);
            using var store = new UsenetBackup.Core.Nntp.NntpBlobStore(
                client, provider.Newsgroup, r.RepoId, r.CatalogPath,
                messageIndex: r.MessageIndex, providerKey: providerKey);
            var remote = r.DiscoverRemoteManifests(store);
            var wanted = new HashSet<string>(backupIds ?? Array.Empty<string>());
            int imported = 0;
            foreach (var m in remote)
            {
                if (wanted.Contains(m.Manifest.BackupId))
                {
                    r.ImportManifest(m.Manifest);
                    imported++;
                }
            }
            OperationLog.Append(repo, "import-remote",
                $"imported={imported} host={provider.Host}");
            return (200, new ImportedDto(imported));
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Starts a manual NNTP upload of a backup in the background.
    /// Returns (202, accepted) or (400, error).
    /// </summary>
    public static (int Status, object Payload) StartUpload(
        ServiceConfig config, BackupScheduler scheduler, string repo, string backupId)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new ErrorDto("backupId is required."));
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new ErrorDto("No Usenet provider configured."));
        Task.Run(() =>
        {
            try
            {
                scheduler.UploadBackup(repo, backupId);
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-upload",
                    $"backupId={backupId} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-upload", $"backupId={backupId} started");
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// Starts a manual SMB sync in the background. The password comes from the
    /// service config (per-job or global env var); the dashboard never handles
    /// it. Returns (202, accepted) or (400, error).
    /// </summary>
    public static (int Status, object Payload) StartSmbSync(
        ServiceConfig config, string share, string user)
    {
        if (string.IsNullOrWhiteSpace(share))
            return (400, new ErrorDto("SMB share path is required."));
        var repos = AllJobRepos(config);
        if (repos.Count == 0)
            return (400, new ErrorDto("No backup jobs configured."));
        // Find SMB credentials: first matching job's stored password, else env var.
        var job = config.SnapshotJobs().FirstOrDefault(j =>
            !string.IsNullOrWhiteSpace(j.SmbShare));
        string? password = Environment.GetEnvironmentVariable(BackupScheduler.SmbPasswordEnvVar);
        if (string.IsNullOrEmpty(password) && job is not null && !string.IsNullOrEmpty(job.SmbPasswordProtected))
            password = UsenetBackup.Core.Dpapi.Unprotect(job.SmbPasswordProtected);
        string effectiveUser = string.IsNullOrWhiteSpace(user) ? job?.SmbUser ?? "" : user;
        Task.Run(() =>
        {
            foreach (string repo in repos)
            {
                try
                {
                    using var smb = UsenetBackup.Core.SmbShare.Connect(share, effectiveUser, password);
                    var sync = new UsenetBackup.Core.SmbSync(msg =>
                        OperationLog.Append(repo, "smb-sync", msg));
                    var result = sync.Sync(repo, smb);
                    OperationLog.Append(repo, "smb-sync",
                        $"to {share} complete: {result.ChunksCopied} chunks, {result.ManifestsCopied} manifests");
                }
                catch (Exception ex)
                {
                    OperationLog.Append(repo, "smb-sync", $"to {share} FAILED: {ex.Message}");
                }
            }
        });
        foreach (string repo in repos)
            OperationLog.Append(repo, "smb-sync", $"to {share} started");
        return (202, new AcceptedReposDto(true, repos.Count));
    }

    /// <summary>
    /// Starts a manual backup verification in the background.
    /// </summary>
    public static (int Status, object Payload) StartVerify(
        ServiceConfig config, string repo, string backupId,
        RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new ErrorDto("backupId is required."));
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."))
                : (500, new ErrorDto("Service passphrase is not configured."));
        Task.Run(() =>
        {
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                var issues = r.Verify(backupId);
                OperationLog.Append(repo, "manual-verify",
                    issues.Count == 0 ? $"backupId={backupId} OK" : $"backupId={backupId} ISSUES: {string.Join("; ", issues.Take(5))}");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-verify",
                    $"backupId={backupId} FAILED: {ex.Message}");
            }
        });
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// Starts a manual retention check in the background.
    /// </summary>
    public static (int Status, object Payload) StartRetentionCheck(
        ServiceConfig config, BackupScheduler scheduler, bool dryRun)
    {
        var repos = AllJobRepos(config);
        if (repos.Count == 0)
            return (400, new ErrorDto("No backup jobs configured."));
        Task.Run(() =>
        {
            foreach (string repo in repos)
            {
                try
                {
                    scheduler.RunRetentionCheck(repo, dryRun);
                }
                catch (Exception ex)
                {
                    OperationLog.Append(repo, "manual-retention",
                        $"dryRun={dryRun} FAILED: {ex.Message}");
                }
            }
        });
        foreach (string repo in repos)
            OperationLog.Append(repo, "manual-retention", $"dryRun={dryRun} started");
        return (202, new AcceptedReposDto(true, repos.Count));
    }

    /// <summary>
    /// Starts a restore of a backup to a destination directory in the background.
    /// </summary>
    public static (int Status, object Payload) StartRestore(
        ServiceConfig config, string repo, string backupId, string destDir,
        UsenetBackup.Core.MergePolicy mergePolicy,
        string? smbShare = null, string? smbUser = null,
        RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new ErrorDto("backupId is required."));
        if (string.IsNullOrWhiteSpace(destDir))
            return (400, new ErrorDto("destDir is required."));
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."))
                : (500, new ErrorDto("Service passphrase is not configured."));
        // SMB credentials: matching job's stored password, else env var.
        string? smbPassword = null;
        if (!string.IsNullOrWhiteSpace(smbShare))
        {
            var job = config.SnapshotJobs().FirstOrDefault(j =>
                j.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
            smbPassword = Environment.GetEnvironmentVariable(BackupScheduler.SmbPasswordEnvVar);
            if (string.IsNullOrEmpty(smbPassword) && job is not null && !string.IsNullOrEmpty(job.SmbPasswordProtected))
                smbPassword = UsenetBackup.Core.Dpapi.Unprotect(job.SmbPasswordProtected);
            if (string.IsNullOrWhiteSpace(smbUser))
                smbUser = job?.SmbUser;
        }
        Task.Run(() =>
        {
            try
            {
                // Pull missing chunks from the SMB share first, if one was given.
                if (!string.IsNullOrWhiteSpace(smbShare))
                {
                    using var ws = new UsenetBackup.Core.Recovery.WizardState
                    {
                        RepoPath = repo,
                        Passphrase = passphrase,
                        SelectedBackupId = backupId,
                        SmbShare = smbShare,
                        SmbUser = smbUser ?? "",
                        SmbPassword = smbPassword ?? "",
                    };
                    ws.EnsureSmbChunks();
                }
                using var r = BackupRepository.Open(repo, passphrase);
                var restoreOptions = new UsenetBackup.Core.RestoreOptions { MergePolicy = mergePolicy };
                var restoreResult = r.Restore(backupId, destDir, restoreOptions);
                OperationLog.Append(repo, "manual-restore",
                    $"backupId={backupId} dest={destDir} policy={mergePolicy} " +
                    $"restored={restoreResult.RestoredFiles} " +
                    $"skippedDiffering={restoreResult.SkippedDiffering.Count} OK");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-restore",
                    $"backupId={backupId} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-restore", $"backupId={backupId} dest={destDir} started");
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// Starts an ad-hoc backup of a source directory in the background.
    /// </summary>
    public static (int Status, object Payload) StartAdhocBackup(
        ServiceConfig config, string repo, string sourceDir)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return (400, new ErrorDto("sourceDir must be an existing directory."));
        string? passphrase = BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new ErrorDto("Service passphrase is not configured."));
        Task.Run(() =>
        {
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                var manifest = r.BackupDirectory(sourceDir, snapshotProvider: null);
                OperationLog.Append(repo, "manual-backup",
                    $"id={manifest.BackupId} source={sourceDir} OK");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-backup",
                    $"source={sourceDir} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-backup", $"source={sourceDir} started");
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// Starts a one-time manual backup to a local folder chosen at run time.
    /// The only supported path to a local-only backup: no scheduled job is
    /// created. The repo is initialized if it does not exist yet. Runs in
    /// the background via the scheduler; progress lands in the Operations log.
    /// Returns (202, {accepted:true}) or (400, {error}).
    /// </summary>
    public static (int Status, object Payload) StartOneTimeBackup(
        BackupScheduler scheduler, IReadOnlyList<string> sourceDirs, string repoPath, ServiceConfig? config = null)
    {
        var sources = sourceDirs.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (sources.Count == 0 || sources.Any(s => !Directory.Exists(s)))
            return (400, new ErrorDto("Choose at least one existing source folder."));
        // A folder already includes everything below it: reject duplicates
        // and nesting (the dashboard picker guards this in the UI; this is
        // the server-side backstop).
        var normalized = new List<string>();
        foreach (string s in sources)
        {
            string n;
            try { n = Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return (400, new ErrorDto($"Invalid folder path: {s}")); }
            normalized.Add(n);
        }
        for (int i = 0; i < normalized.Count; i++)
            for (int j = 0; j < normalized.Count; j++)
            {
                if (i == j)
                    continue;
                if (normalized[i].Equals(normalized[j], StringComparison.OrdinalIgnoreCase))
                    return (400, new ErrorDto($"The same folder is listed twice: {sources[i]}"));
                if (normalized[i].StartsWith(normalized[j] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return (400, new ErrorDto($"'{sources[i]}' is inside '{sources[j]}' — a folder already includes everything below it."));
            }
        if (string.IsNullOrWhiteSpace(repoPath))
            return (400, new ErrorDto("Choose a destination folder for the backup."));
        try
        {
            // The repo must not live inside any source: the backup would try
            // to back up its own output.
            string fullRepo = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (string s in sources)
            {
                string fullSource = Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (fullRepo.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
                    return (400, new ErrorDto("The destination folder cannot be inside a source folder."));
                if (fullSource.StartsWith(fullRepo, StringComparison.OrdinalIgnoreCase) && fullSource.Length != fullRepo.Length)
                    return (400, new ErrorDto("A source folder cannot be inside the destination folder."));
            }
        }
        catch
        {
            return (400, new ErrorDto("Invalid folder paths."));
        }
        string? passphrase = config is null
            ? Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar)
            : BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(passphrase))
            return (400, new ErrorDto("Set a repository passphrase first (Settings \u2192 Security)."));
        Task.Run(() =>
        {
            try
            {
                var result = scheduler.RunOneTimeBackup(sources, repoPath);
                OperationLog.Append(repoPath, "one-time-backup",
                    $"sources={string.Join(";", sources)} {(result.Success ? $"OK id={result.BackupId} files={result.Files}" : "FAILED: " + result.Error)}");
            }
            catch (Exception ex)
            {
                try
                {
                    OperationLog.Append(repoPath, "one-time-backup",
                        $"sources={string.Join(";", sources)} FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                catch { /* best effort */ }
            }
        });
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// Generates an NZB for a backup and returns the XML bytes.
    /// </summary>
    public static (int Status, object Payload, string FileName) GenerateNzb(
        ServiceConfig config, string repo, string backupId,
        RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."), "");
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new ErrorDto("backupId is required."), "");
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."), "")
                : (500, new ErrorDto("Service passphrase is not configured."), "");
        try
        {
            var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
            string newsgroup = provider?.Newsgroup ?? "alt.binaries.test";
            using var r = BackupRepository.Open(repo, passphrase);
            var manifest = r.LoadManifest(backupId);
            using var catalog = new Catalog(r.CatalogPath);
            string xml = NzbGenerator.Generate(
                manifest,
                chunkId => r.GetChunkBlob(chunkId),
                chunkId => catalog.GetUploadTimeUtc(chunkId),
                new NzbGenerator.Options(newsgroup, "filekeep", r.RepoId));
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(xml);
            return (200, bytes, $"filekeep-{backupId}.nzb");
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto(ex.Message), "");
        }
    }

    /// <summary>
    /// Starts a download from an uploaded NZB file in the background.
    /// </summary>
    public static (int Status, object Payload) StartNzbDownload(
        ServiceConfig config, BackupScheduler scheduler, string nzbPath,
        RestoreSession? session = null)
    {
        // Chunks are content-addressed; fetch into the primary repo.
        string? repo = PrimaryRepo(config);
        if (repo is null)
            return (400, new ErrorDto("No backup jobs configured."));
        var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
        if (provider is null)
            return (400, new ErrorDto("No Usenet provider configured."));
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."))
                : (500, new ErrorDto("Service passphrase is not configured."));
        Task.Run(() =>
        {
            try
            {
                scheduler.DownloadFromNzb(repo, nzbPath, passphrase);
                OperationLog.Append(repo, "manual-nzb-download", $"nzb={Path.GetFileName(nzbPath)} OK");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-nzb-download",
                    $"nzb={Path.GetFileName(nzbPath)} FAILED: {ex.Message}");
            }
            finally
            {
                try { File.Delete(nzbPath); } catch { }
            }
        });
        OperationLog.Append(repo, "manual-nzb-download", $"nzb={Path.GetFileName(nzbPath)} started");
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// Starts a disk-image backup in the background.
    /// If <paramref name="smbShare"/> is provided, streams chunks directly
    /// to the share (sparse: used space only). Otherwise stages locally.
    /// </summary>
    public static (int Status, object Payload) StartDiskBackup(
        ServiceConfig config, string repo, string device, string imageName,
        string? smbShare = null, bool sparse = true)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(device))
            return (400, new ErrorDto("device is required."));
        string? passphrase = BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(passphrase))
            return (500, new ErrorDto("Service passphrase is not configured."));
        Task.Run(() =>
        {
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                BackupManifest manifest;
                if (!string.IsNullOrWhiteSpace(smbShare))
                {
                    using var smb = SmbShare.Connect(smbShare, null, null);
                    var store = new SmbBlobStore(smb);
                    manifest = r.BackupDiskImageStreaming(device, store, imageName, sparse: sparse, volumeLetter: null);
                    OperationLog.Append(repo, "manual-disk-backup",
                        $"id={manifest.BackupId} device={device} smb={smbShare} sparse={sparse} OK");
                }
                else
                {
                    manifest = r.BackupDiskImage(device, imageName);
                    OperationLog.Append(repo, "manual-disk-backup",
                        $"id={manifest.BackupId} device={device} OK");
                }
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-disk-backup",
                    $"device={device} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-disk-backup", $"device={device} started");
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// Starts a disk-image restore in the background. Destructive: the caller
    /// must pass confirmed=true (the UI requires an explicit checkbox plus a
    /// confirm dialog, and never lists the running system drive as a target).
    /// </summary>
    public static (int Status, object Payload) StartDiskRestore(
        ServiceConfig config, string repo, string backupId, string device, bool confirmed,
        RestoreSession? session = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new ErrorDto("backupId is required."));
        if (string.IsNullOrWhiteSpace(device))
            return (400, new ErrorDto("device is required."));
        if (!confirmed)
            return (400, new ErrorDto("Restore not confirmed. Restore aborted."));
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."))
                : (500, new ErrorDto("Service passphrase is not configured."));
        Task.Run(() =>
        {
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                r.RestoreDiskImage(backupId, device);
                OperationLog.Append(repo, "manual-disk-restore",
                    $"backupId={backupId} device={device} OK");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-disk-restore",
                    $"backupId={backupId} device={device} FAILED: {ex.Message}");
            }
        });
        OperationLog.Append(repo, "manual-disk-restore", $"backupId={backupId} device={device} started");
        return (202, new AcceptedDto(true));
    }
}
