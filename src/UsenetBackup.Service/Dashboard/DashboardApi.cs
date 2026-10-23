using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Recovery;
using UsenetBackup.Core.Security;
using UsenetBackup.Core.Service;

namespace UsenetBackup.Service;

/// <summary>
/// DashboardApi — Core dashboard endpoints: status, jobs, repositories, backups,
/// uploads, restores, retention checks and NZB handling.
/// </summary>
public static partial class DashboardApi
{
    // P2-C5: restore targets currently being written. Two restores
    // aimed at the same destination (a double-clicked disk restore, or
    // the same backup picked twice) interleaved their writes and left
    // a spliced result both reported on only at the end. Claims are
    // per-target: different destinations restore in parallel freely.
    // Item 7 (2026-10-22): each claim is a RestoreClaim carrying the
    // restore's identity and its OWN CancellationTokenSource, so the
    // dashboard can cancel that one restore while it runs — the claim
    // is both the mutual-exclusion token and the cancel handle.
    internal sealed class RestoreClaim
    {
        public required string Key { get; init; }
        public required string Repo { get; init; }
        public required string BackupId { get; init; }
        /// <summary>Destination folder (file restore) or device path
        /// (disk restore), for display and for the cancel lookup.</summary>
        public required string Target { get; init; }
        public required DateTime StartedUtc { get; init; }
        public CancellationTokenSource Cts { get; } = new();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RestoreClaim>
        ActiveRestores = new(StringComparer.OrdinalIgnoreCase);

    internal static string FileRestoreTargetKey(string destDir) =>
        "file:" + Path.GetFullPath(destDir.Trim());

    internal static string DiskRestoreTargetKey(string device) =>
        "disk:" + device.Trim();

    internal static bool TryClaimRestoreTarget(RestoreClaim claim) =>
        ActiveRestores.TryAdd(claim.Key, claim);

    internal static void ReleaseRestoreTarget(string key) =>
        // The claim's CTS is deliberately NOT disposed here: a cancel
        // request racing the restore's final moments may still hold
        // the claim, and Cancel() on a disposed source throws. The
        // source has no timer and is tiny; the GC reclaims it.
        ActiveRestores.TryRemove(key, out _);

    /// <summary>
    /// Requests cancellation of the restore claimed under
    /// <paramref name="key"/>. Returns false when no restore is
    /// running to that target. Cancelling is a signal, not a removal:
    /// the claim stays until the restore actually stops and releases
    /// it, so a second restore to the same target still gets a 409
    /// while the first is winding down.
    /// </summary>
    internal static bool TryCancelRestore(string key)
    {
        if (!ActiveRestores.TryGetValue(key, out var claim))
            return false;
        claim.Cts.Cancel();
        return true;
    }

    internal static IReadOnlyList<RestoreClaim> ActiveRestoreList() =>
        ActiveRestores.Values.OrderBy(c => c.StartedUtc).ToList();

    /// <summary>Active restores for GET /api/operations/restores.</summary>
    public static (int Status, object Payload) ListActiveRestores() =>
        (200, ActiveRestoreList().Select(c => new ActiveRestoreDto(
            c.Key.StartsWith("disk:", StringComparison.Ordinal) ? "disk" : "file",
            c.Repo, c.BackupId, c.Target,
            DateTime.SpecifyKind(c.StartedUtc, DateTimeKind.Utc).ToLocalTime())).ToList());

    /// <summary>
    /// Cancels the restore running to the given destination folder
    /// (file restore) or device (disk restore). The restore stops at
    /// its next chunk/file boundary, records a CANCELLED outcome in
    /// the operations log (not a failure), and releases its target.
    /// </summary>
    public static (int Status, object Payload) CancelRestore(
        string? destDir, string? device)
    {
        string? key = !string.IsNullOrWhiteSpace(destDir)
            ? FileRestoreTargetKey(destDir)
            : !string.IsNullOrWhiteSpace(device)
                ? DiskRestoreTargetKey(device) : null;
        if (key is null)
            return (400, new ErrorDto("destDir or device is required."));
        return TryCancelRestore(key)
            ? (200, new OkDto(true))
            : (404, new ErrorDto("No restore is running to that destination."));
    }

    /// <summary>
    /// The installed app version, from the assembly informational version
    /// stamped at build time (run-vm-build.ps1 /p:InformationalVersion=).
    /// Falls back to the assembly version, then "0.0.0". Never throws:
    /// in single-file deployments reflection can behave unexpectedly, and
    /// a version lookup must not 500 the dashboard.
    /// </summary>
    public static string GetAppVersion()
    {
        // Primary: FileVersionInfo on the running executable. In single-file
        // deployments reflection on the entry assembly is unreliable, but
        // FileVersionInfo.ProductVersion carries the stamped InformationalVersion
        // (e.g. "0.08.15+gitsha").
        try
        {
            string? path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                string? product = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion;
                if (!string.IsNullOrWhiteSpace(product))
                {
                    string v = product.Split('+')[0].Trim();
                    if (TryParseVersion(v, out _))
                        return v;
                }
            }
        }
        catch { }
        // Fallback: assembly informational version via reflection.
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                string v = info.Split('+')[0].Trim();
                if (TryParseVersion(v, out _))
                    return v;
            }
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>
    /// The full build token: the informational version exactly as stamped,
    /// INCLUDING any "+gitsha" suffix that <see cref="GetAppVersion"/> strips
    /// for display. Two builds with the same display version but different
    /// commits produce different tokens, so the dashboard's stale-page check
    /// (page build vs. service build) catches same-version rebuilds too.
    /// Never throws; returns "" when no version can be determined, which
    /// callers must treat as "unknown — do not enforce".
    /// </summary>
    public static string GetBuildToken()
    {
        try
        {
            string? path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                string? product = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion;
                if (!string.IsNullOrWhiteSpace(product))
                    return product.Trim();
            }
        }
        catch { }
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return info.Trim();
            return asm.GetName().Version?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Checks the GitHub repo for the most recent released version by reading
    /// the VERSION file committed and pushed to main. Returns the installed
    /// version, the latest pushed version, whether an update is available,
    /// and any of the latest version's requirements this machine does not
    /// meet (from REQUIREMENTS.json next to VERSION; best-effort).
    /// </summary>
    public static async Task<(bool Ok, string? Latest, string? Error, string[] UnmetRequirements)> CheckForUpdatesAsync()
    {
        const string baseUrl = "https://raw.githubusercontent.com/thetreichels/FileKeep/main";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string latest = (await http.GetStringAsync(baseUrl + "/VERSION")).Trim();
            if (!TryParseVersion(latest, out _))
                return (false, null, $"GitHub returned an unrecognized version ('{latest}').", Array.Empty<string>());
            string[] unmet = await GetUnmetRequirementsAsync(http);
            return (true, latest, null, unmet);
        }
        catch (Exception ex)
        {
            return (false, null, $"Couldn't reach GitHub: {ex.Message}", Array.Empty<string>());
        }
    }

    /// <summary>
    /// Reads REQUIREMENTS.json from GitHub main and returns the human-readable
    /// list of requirements this machine does not meet. Empty when the file
    /// is missing, unparsable, or everything is satisfied.
    /// </summary>
    private static async Task<string[]> GetUnmetRequirementsAsync(HttpClient http)
    {
        var unmet = new List<string>();
        try
        {
            const string url = "https://raw.githubusercontent.com/thetreichels/FileKeep/main/REQUIREMENTS.json";
            string json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("requires", out var req))
                return Array.Empty<string>();
            if (req.TryGetProperty("webView2", out var wv) && wv.GetBoolean() && !IsWebView2Installed())
                unmet.Add("Microsoft Edge WebView2 runtime (the installer adds it automatically)");
            if (req.TryGetProperty("minWindowsBuild", out var wb) && wb.TryGetInt32(out int minBuild) &&
                OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build < minBuild)
                unmet.Add($"Windows 10 build {minBuild} or newer (this PC: build {Environment.OSVersion.Version.Build})");
        }
        catch
        {
            // Best-effort: a missing or broken manifest means no extra requirements.
        }
        return unmet.ToArray();
    }

    /// <summary>True when the WebView2 runtime is installed (Windows-only check).</summary>
    public static bool IsWebView2Installed()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            return key?.GetValue("pv") is string pv && !string.IsNullOrWhiteSpace(pv);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Parses "n.nn.nn" (leading zeros allowed) into ints for comparison.</summary>
    public static bool TryParseVersion(string s, out int[] parts)
    {
        parts = Array.Empty<int>();
        var p = s.Trim().Split('.');
        if (p.Length < 2 || p.Length > 4)
            return false;
        var nums = new int[p.Length];
        for (int i = 0; i < p.Length; i++)
        {
            // int.TryParse handles leading zeros ("08" -> 8).
            if (!int.TryParse(p[i], out nums[i]) || nums[i] < 0)
                return false;
        }
        parts = nums;
        return true;
    }

    /// <summary>True when <paramref name="latest"/> is newer than <paramref name="current"/>.</summary>
    public static bool IsNewerVersion(string current, string latest)
    {
        if (!TryParseVersion(current, out int[] c) || !TryParseVersion(latest, out int[] l))
            return false;
        for (int i = 0; i < Math.Max(c.Length, l.Length); i++)
        {
            int a = i < c.Length ? c[i] : 0;
            int b = i < l.Length ? l[i] : 0;
            if (b != a)
                return b > a;
        }
        return false;
    }
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
                j.LastResult.Error,
                j.LastResult.DegradedNote,
                j.LastResult.AutoUploadFailed,
                j.LastResult.AutoUploadError,
                j.LastResult.VerifyFailed,
                j.LastResult.VerifyError,
                j.LastResult.Skipped),
            scheduler.EvaluateJobState(j.Config, j.NetworkAway).ToString())).ToList());

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
                : (500, PassphraseUnavailableError(config, $"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})."));
        try
        {
            using var r = BackupRepository.Open(repo, passphrase);
            var backups = new List<BackupDto>();
            foreach (var b in r.ListBackups().OrderByDescending(b => b.CreatedUtc))
            {
                BackupManifest manifest;
                try
                {
                    manifest = r.LoadManifest(b.BackupId);
                }
                catch (Exception ex)
                {
                    // P2-D4: an unreadable/corrupt manifest used to be
                    // masked as a real backup with 0 stored bytes. Skip
                    // it in the list and leave the reason in the log —
                    // the backup is not restorable in this state and
                    // presenting it as healthy inventory misleads.
                    OperationLog.Append(repo, "vault-list",
                        $"backupId={b.BackupId} excluded: manifest unreadable ({ex.Message})");
                    continue;
                }
                backups.Add(new BackupDto(b.BackupId, b.Type, b.Source, b.CreatedUtc,
                    r.StoredBytes(manifest)));
            }
            return (200, backups.ToArray());
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
        var loc = config.SnapshotLocations().FirstOrDefault(l =>
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

        // P2-D3: the old import raw-copied the manifest file — no parse,
        // no root-hash verification, no catalog registration — so the
        // "imported" backup never appeared in the catalog-based vault
        // list yet was filtered out of future discovery scans, and a
        // tampered share manifest silently replaced a good local one.
        // Validate the ID by the same 32-hex rule LoadManifest enforces.
        if (backupId.Length != 32 || !backupId.All(Uri.IsHexDigit))
            return (400, new ErrorDto("Invalid backup ID."));

        string sourceManifest = Path.Combine(sourcePath, "manifests", backupId + ".json");
        if (!File.Exists(sourceManifest))
            return (400, new ErrorDto($"Manifest not found at {sourceManifest}."));

        BackupManifest manifest;
        try
        {
            manifest = System.Text.Json.JsonSerializer.Deserialize(
                File.ReadAllText(sourceManifest),
                UsenetBackup.Core.Json.FileKeepJsonContext.Default.BackupManifest)
                ?? throw new InvalidDataException("The manifest file is empty or invalid.");
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto($"The discovered manifest is not readable: {ex.Message}"));
        }
        if (!string.Equals(manifest.BackupId, backupId, StringComparison.OrdinalIgnoreCase))
            return (400, new ErrorDto("The discovered manifest's ID does not match the requested backup ID."));
        if (!manifest.VerifyRootHash())
            return (400, new ErrorDto(
                "The discovered manifest failed its integrity check (root hash mismatch) and was not imported."));
        // A manifest without a root_hmac uses the pre-release format —
        // LoadManifest refuses it on first open, so importing it here
        // would only plant a backup that can never load. Refuse it at
        // the door instead. (Presence only: this path deliberately
        // does not open the repo, so it cannot verify the HMAC; the
        // load-time verification remains the real gate.)
        if (manifest.RootHmac is null)
            return (400, new ErrorDto(
                "The discovered manifest uses the unsupported pre-release backup format (no root_hmac) and was not imported."));

        string destDir = Path.Combine(repo, "manifests");
        string destManifest = Path.Combine(destDir, backupId + ".json");
        try
        {
            Directory.CreateDirectory(destDir);
            if (File.Exists(destManifest))
            {
                // Never silently replace a local manifest: identical is
                // an idempotent no-op; different content is a conflict
                // the user must resolve, not an overwrite.
                var existing = System.Text.Json.JsonSerializer.Deserialize(
                    File.ReadAllText(destManifest),
                    UsenetBackup.Core.Json.FileKeepJsonContext.Default.BackupManifest);
                if (existing is null ||
                    !string.Equals(existing.RootSha256, manifest.RootSha256, StringComparison.OrdinalIgnoreCase))
                    return (409, new ErrorDto(
                        "A different manifest with this backup ID already exists locally; the discovered one was not imported."));
                // Identical: fall through to registration (idempotent).
            }
            else
            {
                // Temp + move, like WriteManifest, so a crash can't leave
                // a torn manifest behind.
                string tmp = destManifest + ".tmp-" + Guid.NewGuid().ToString("N");
                File.Copy(sourceManifest, tmp);
                File.Move(tmp, destManifest);
            }
            // Register in the catalog so the backup actually appears in
            // the vault list (BackupRepository.ImportManifest's contract,
            // applied without opening the repo — import is metadata-only
            // and the root hash above is the integrity gate).
            using (var catalog = new UsenetBackup.Core.Catalog(Path.Combine(repo, "catalog.db")))
                catalog.RecordBackup(manifest.BackupId, manifest.Type, manifest.Source ?? "",
                    manifest.CreatedUtc);
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
                : (500, PassphraseUnavailableError(config, $"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})."));
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
                messageIndex: r.MessageIndex, providerKey: providerKey,
                indexAuth: r.IndexAuth);
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
                : (500, PassphraseUnavailableError(config, $"Service passphrase is not configured ({BackupScheduler.PassphraseEnvVar})."));
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
                messageIndex: r.MessageIndex, providerKey: providerKey,
                indexAuth: r.IndexAuth);
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
    /// Starts a manual SMB sync in the background. The password comes from
    /// the service config (the SMB location for the share, or the global
    /// env var); the dashboard never handles it. Returns (202, accepted)
    /// or (400, error).
    /// </summary>
    public static (int Status, object Payload) StartSmbSync(
        ServiceConfig config, string share, string user,
        BackupScheduler? scheduler = null)
    {
        if (string.IsNullOrWhiteSpace(share))
            return (400, new ErrorDto("SMB share path is required."));
        var repos = AllJobRepos(config);
        if (repos.Count == 0)
            return (400, new ErrorDto("No backup jobs configured."));
        // Find SMB credentials: the location whose share matches the
        // requested share is the credential home (the per-job smb*
        // fields were retired in Phase 6); the env var overrides.
        var loc = config.SnapshotLocations().FirstOrDefault(l =>
            l.Type.Equals("smb", StringComparison.OrdinalIgnoreCase) &&
            l.Share.Equals(share, StringComparison.OrdinalIgnoreCase));
        string? password = Environment.GetEnvironmentVariable(BackupScheduler.SmbPasswordEnvVar);
        if (string.IsNullOrEmpty(password) && loc is not null && !string.IsNullOrEmpty(loc.SmbPasswordProtected))
            password = UsenetBackup.Core.Dpapi.Unprotect(loc.SmbPasswordProtected);
        string effectiveUser = string.IsNullOrWhiteSpace(user) ? loc?.SmbUser ?? "" : user;
        Task.Run(() =>
        {
            // P2-C3: serialize against scheduled runs and every other
            // long operation through the scheduler's operation gate.
            using var op = scheduler?.BeginDashboardOperation();
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
        RestoreSession? session = null, BackupScheduler? scheduler = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new ErrorDto("backupId is required."));
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."))
                : (500, PassphraseUnavailableError(config, "Service passphrase is not configured."));
        Task.Run(() =>
        {
            // P2-C3: serialize against scheduled runs and every other
            // long operation through the scheduler's operation gate.
            using var op = scheduler?.BeginDashboardOperation();
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
                    // MU-5: a completion line — success used to leave
                    // no operations-log trace at all, so the outcome
                    // was indistinguishable from "still running."
                    OperationLog.Append(repo, "manual-retention",
                        $"dryRun={dryRun} complete");
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
    /// Resolves a restore's SMB share, user and password. The job's SMB
    /// target LOCATION is the credential home: when the request names
    /// no share, the location's share is used automatically (with local
    /// chunks pruned after upload, the banked location is the only
    /// place the chunks can come from), and its stored user/password
    /// fill any gaps the request leaves. Explicit request values always
    /// win. (WALK-3: restores previously read only the job's legacy
    /// per-job smb* fields, which location-based jobs leave empty, so
    /// no restore could pull from an auth-required share; those fields
    /// were retired in Phase 6 and the location is now the only source.)
    /// </summary>
    public static (string? Share, string? User, string? Password) ResolveRestoreSmb(
        ServiceConfig config, string repo, string? smbShare, string? smbUser)
    {
        var job = config.SnapshotJobs().FirstOrDefault(j =>
            j.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
        BackupLocation? smbLoc = null;
        if (job is not null)
        {
            var locByName = config.SnapshotLocations().ToDictionary(
                l => l.Name, StringComparer.OrdinalIgnoreCase);
            smbLoc = job.EffectiveTargets
                .Select(t => locByName.TryGetValue(t, out var l) ? l : null)
                .FirstOrDefault(l => l is not null &&
                    l.Type.Equals("smb", StringComparison.OrdinalIgnoreCase));
        }
        if (string.IsNullOrWhiteSpace(smbShare) && smbLoc is not null)
            smbShare = smbLoc.Share;
        string? smbPassword = null;
        if (!string.IsNullOrWhiteSpace(smbShare))
        {
            smbPassword = Environment.GetEnvironmentVariable(BackupScheduler.SmbPasswordEnvVar);
            if (string.IsNullOrEmpty(smbPassword) && smbLoc is not null && !string.IsNullOrEmpty(smbLoc.SmbPasswordProtected))
                smbPassword = UsenetBackup.Core.Dpapi.Unprotect(smbLoc.SmbPasswordProtected);
            if (string.IsNullOrWhiteSpace(smbUser))
                smbUser = smbLoc?.SmbUser;
        }
        return (smbShare, smbUser, smbPassword);
    }

    /// <summary>
    /// Resolves a restore's USB chunk source: the job's first USB target
    /// location whose drive is currently plugged in, resolved by volume
    /// serial to its backup folder (e.g. "E:\FileKeep"). Returns null
    /// when the job has no USB target or no such drive is present.
    /// (WALK-8: restores previously had exactly one remote chunk source —
    /// SMB — so a USB-backed restore failed on every chunk once local
    /// staging was pruned, even with the drive plugged in.)
    /// </summary>
    public static string? ResolveRestoreUsb(ServiceConfig config, string repo)
    {
        var job = config.SnapshotJobs().FirstOrDefault(j =>
            j.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (job is null)
            return null;
        var locByName = config.SnapshotLocations().ToDictionary(
            l => l.Name, StringComparer.OrdinalIgnoreCase);
        foreach (string target in job.EffectiveTargets)
        {
            if (locByName.TryGetValue(target, out var loc) &&
                loc.Type.Equals("usb", StringComparison.OrdinalIgnoreCase))
            {
                string? path = UsbDriveResolver.ResolveBackupPath(
                    loc.VolumeSerial ?? "", loc.UsbFolder ?? "FileKeep");
                if (path is not null)
                    return path;
            }
        }
        return null;
    }

    /// <summary>
    /// Starts a restore of a backup to a destination directory in the background.
    /// </summary>
    public static (int Status, object Payload) StartRestore(
        ServiceConfig config, string repo, string backupId, string destDir,
        UsenetBackup.Core.MergePolicy mergePolicy,
        string? smbShare = null, string? smbUser = null,
        RestoreSession? session = null, CancellationToken cancellationToken = default,
        BackupScheduler? scheduler = null)
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
                : (500, PassphraseUnavailableError(config, "Service passphrase is not configured."));
        // SMB source and credentials: the job's SMB target location is
        // the credential home; see ResolveRestoreSmb.
        var (resolvedShare, resolvedUser, smbPassword) =
            ResolveRestoreSmb(config, repo, smbShare, smbUser);
        smbShare = resolvedShare;
        smbUser = resolvedUser;
        // USB source: the job's USB target location, if its drive is
        // plugged in now; see ResolveRestoreUsb.
        string? usbFolder = ResolveRestoreUsb(config, repo);
        string targetKey = FileRestoreTargetKey(destDir);
        var claim = new RestoreClaim
        {
            Key = targetKey, Repo = repo, BackupId = backupId,
            Target = destDir, StartedUtc = DateTime.UtcNow,
        };
        if (!TryClaimRestoreTarget(claim))
            return (409, new ErrorDto(
                "A restore is already writing to that destination. " +
                "Wait for it to finish before starting another one there."));
        Task.Run(() =>
        {
            // P2-C3: serialize against scheduled runs and every other
            // long operation through the scheduler's operation gate.
            using var op = scheduler?.BeginDashboardOperation();
            // The restore honors every stop signal: the dashboard host
            // token (P2-C8), the operation gate's token (service stop
            // via CancelActiveOperation), and the claim's own token —
            // the per-restore cancel (item 7).
            using var linked = op is null
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, claim.Cts.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, op.Token, claim.Cts.Token);
            var effectiveToken = linked.Token;
            try
            {
                // Pull missing chunks from the job's banked locations
                // first: the SMB share if one resolved, and the USB
                // drive if it is plugged in. Both pulls are idempotent
                // and skip chunks already staged locally.
                if (!string.IsNullOrWhiteSpace(smbShare) || usbFolder is not null)
                {
                    using var ws = new UsenetBackup.Core.Recovery.WizardState
                    {
                        RepoPath = repo,
                        Passphrase = passphrase,
                        SelectedBackupId = backupId,
                        SmbShare = smbShare ?? "",
                        SmbUser = smbUser ?? "",
                        SmbPassword = smbPassword ?? "",
                    };
                    if (!string.IsNullOrWhiteSpace(smbShare))
                        ws.EnsureSmbChunks(null, effectiveToken);
                    if (usbFolder is not null)
                        ws.EnsureLocalChunks(usbFolder, null, effectiveToken);
                }
                using var r = BackupRepository.Open(repo, passphrase);
                var restoreOptions = new UsenetBackup.Core.RestoreOptions { MergePolicy = mergePolicy };
                var restoreResult = r.Restore(backupId, destDir, restoreOptions, effectiveToken);
                // MU-15: a successful restore is the deferred proof for
                // a typed-secret (unverified) session unlock.
                if (config.PassphraseYubiKey)
                    session?.MarkVerified();
                OperationLog.Append(repo, "manual-restore",
                    $"backupId={backupId} dest={destDir} policy={mergePolicy} " +
                    $"restored={restoreResult.RestoredFiles} " +
                    $"skippedDiffering={restoreResult.SkippedDiffering.Count} OK");
            }
            catch (OperationCanceledException) when (claim.Cts.IsCancellationRequested)
            {
                // Item 7: a user cancel is a clean outcome, not a
                // failure. Files already restored stay restored; the
                // claim releases below and the staging the pull built
                // stays as ordinary (prunable) local staging.
                OperationLog.Append(repo, "manual-restore",
                    $"backupId={backupId} dest={destDir} CANCELLED by user");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-restore",
                    $"backupId={backupId} FAILED: {ex.Message}");
            }
            finally
            {
                ReleaseRestoreTarget(targetKey);
            }
        });
        OperationLog.Append(repo, "manual-restore", $"backupId={backupId} dest={destDir} started");
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// Starts an ad-hoc backup of a source directory in the background.
    /// </summary>
    public static (int Status, object Payload) StartAdhocBackup(
        ServiceConfig config, string repo, string sourceDir,
        BackupScheduler? scheduler = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return (400, new ErrorDto("sourceDir must be an existing directory."));
        string? passphrase = BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(passphrase))
            return (500, PassphraseUnavailableError(config, "Service passphrase is not configured."));
        Task.Run(() =>
        {
            // P2-C3: serialize against scheduled runs and every other
            // long operation through the scheduler's operation gate.
            using var op = scheduler?.BeginDashboardOperation();
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
                : (500, PassphraseUnavailableError(config, "Service passphrase is not configured."), "");
        try
        {
            var provider = config.EffectiveProviders.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Host));
            string newsgroup = provider?.Newsgroup ?? "alt.binaries.test";
            // P2-D16: NZB dates come from the journal of the provider
            // this NZB is for — unscoped, another provider's (or a
            // legacy) row stamped dates onto articles it never posted.
            string? providerKey = provider is null ? null :
                UsenetBackup.Core.Nntp.ChunkMessageIndex.MakeProviderKey(provider.Host, newsgroup);
            using var r = BackupRepository.Open(repo, passphrase);
            var manifest = r.LoadManifest(backupId);
            using var catalog = new Catalog(r.CatalogPath);
            string xml = NzbGenerator.Generate(
                manifest,
                chunkId => r.GetChunkBlob(chunkId),
                chunkId => catalog.GetUploadTimeUtc(chunkId, providerKey),
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
                : (500, PassphraseUnavailableError(config, "Service passphrase is not configured."));
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
        string? smbShare = null, bool sparse = true,
        BackupScheduler? scheduler = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(device))
            return (400, new ErrorDto("device is required."));
        string? passphrase = BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(passphrase))
            return (500, PassphraseUnavailableError(config, "Service passphrase is not configured."));
        Task.Run(() =>
        {
            // P2-C3: serialize against scheduled runs and every other
            // long operation through the scheduler's operation gate.
            using var op = scheduler?.BeginDashboardOperation();
            try
            {
                using var r = BackupRepository.Open(repo, passphrase);
                BackupManifest manifest;
                if (!string.IsNullOrWhiteSpace(smbShare))
                {
                    using var smb = SmbShare.Connect(smbShare, null, null);
                    var store = new SmbBlobStore(smb);
                    manifest = r.BackupDiskImageStreaming(device, store, imageName, sparse: sparse, volumeLetter: null,
                        cancellationToken: op?.Token ?? default);
                    OperationLog.Append(repo, "manual-disk-backup",
                        $"id={manifest.BackupId} device={device} smb={smbShare} sparse={sparse} OK");
                }
                else
                {
                    manifest = r.BackupDiskImage(device, imageName,
                        cancellationToken: op?.Token ?? default);
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
    /// The target is re-validated against the currently installed drives:
    /// if the disk is gone, or a different disk now sits at that device path
    /// (USB shuffle between pick and confirm), the restore is refused.
    /// Before anything is written, every chunk the image needs is staged
    /// locally — pulled from the job's banked locations, exactly as for a
    /// file restore; if any chunk cannot be obtained, the restore is
    /// refused with the target untouched (DL-6).
    /// </summary>
    public static (int Status, object Payload) StartDiskRestore(
        ServiceConfig config, string repo, string backupId, string device, bool confirmed,
        RestoreSession? session = null, string? expectedModel = null, long? expectedSizeBytes = null,
        CancellationToken cancellationToken = default, BackupScheduler? scheduler = null)
    {
        if (!IsKnownRepo(config, repo))
            return (400, new ErrorDto("Unknown repo (not a configured job)."));
        if (string.IsNullOrWhiteSpace(backupId))
            return (400, new ErrorDto("backupId is required."));
        if (string.IsNullOrWhiteSpace(device))
            return (400, new ErrorDto("device is required."));
        if (!confirmed)
            return (400, new ErrorDto("Restore not confirmed. Restore aborted."));
        // The drive list the picker showed may be stale (USB unplugged,
        // device numbers shifted). Re-enumerate and make sure the target is
        // still there and still the same disk the user picked.
        // (Windows-only: PhysicalDrives.List() is empty elsewhere.)
        if (OperatingSystem.IsWindows())
        {
            var drive = PhysicalDrives.List()
                .FirstOrDefault(d => string.Equals(d.DevicePath, device, StringComparison.OrdinalIgnoreCase));
            if (drive is null)
                return (400, new ErrorDto(
                    $"The selected disk ({device}) is no longer present. Re-select the target disk and try again."));
            if (!string.IsNullOrWhiteSpace(expectedModel) &&
                (!string.Equals(drive.Model, expectedModel, StringComparison.Ordinal) ||
                 (expectedSizeBytes.HasValue && drive.SizeBytes != expectedSizeBytes.Value)))
                return (400, new ErrorDto(
                    $"The disk at {device} changed since you selected it " +
                    $"(now: {drive.Model}, {drive.SizeBytes} bytes). Re-select the target disk and confirm again."));
        }
        string? passphrase = GetRestorePassphrase(config, session);
        if (string.IsNullOrEmpty(passphrase))
            return config.PassphraseYubiKey
                ? (401, new ErrorDto("Restore session is locked. Touch your YubiKey to unlock it."))
                : (500, PassphraseUnavailableError(config, "Service passphrase is not configured."));
        // Chunk sources, resolved exactly as for a file restore: the
        // job's banked SMB location (the credential home; see
        // ResolveRestoreSmb) and its USB location when the drive is
        // plugged in now (see ResolveRestoreUsb).
        var (resolvedShare, resolvedUser, smbPassword) =
            ResolveRestoreSmb(config, repo, null, null);
        string? usbFolder = ResolveRestoreUsb(config, repo);
        string targetKey = DiskRestoreTargetKey(device);
        var claim = new RestoreClaim
        {
            Key = targetKey, Repo = repo, BackupId = backupId,
            Target = device, StartedUtc = DateTime.UtcNow,
        };
        if (!TryClaimRestoreTarget(claim))
            return (409, new ErrorDto(
                "A restore is already writing to that disk. " +
                "Wait for it to finish before starting another one there."));
        Task.Run(() =>
        {
            // P2-C3: serialize against scheduled runs and every other
            // long operation through the scheduler's operation gate.
            using var op = scheduler?.BeginDashboardOperation();
            // Stop signals: host token (P2-C8), gate token (service
            // stop), and the claim's own token (per-restore cancel,
            // item 7).
            using var linked = op is null
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, claim.Cts.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, op.Token, claim.Cts.Token);
            var effectiveToken = linked.Token;
            try
            {
                // DL-6: stage and prove every chunk BEFORE the first
                // byte is written. Local staging is pruned after upload,
                // so without this the restore died on the first pruned
                // chunk — with the target already partly overwritten.
                EnsureDiskImageChunksStaged(repo, backupId, passphrase,
                    resolvedShare, resolvedUser, smbPassword, usbFolder,
                    effectiveToken);
                using var r = BackupRepository.Open(repo, passphrase);
                r.RestoreDiskImage(backupId, device, effectiveToken);
                // MU-15: deferred proof for a typed-secret unlock.
                if (config.PassphraseYubiKey)
                    session?.MarkVerified();
                OperationLog.Append(repo, "manual-disk-restore",
                    $"backupId={backupId} device={device} OK");
            }
            catch (OperationCanceledException) when (claim.Cts.IsCancellationRequested)
            {
                // Item 7: a user cancel is a clean outcome, not a
                // failure. (A disk image already partly written stays
                // partly written — the same as any interrupted disk
                // restore; the cancel stops further writes promptly.)
                OperationLog.Append(repo, "manual-disk-restore",
                    $"backupId={backupId} device={device} CANCELLED by user");
            }
            catch (Exception ex)
            {
                OperationLog.Append(repo, "manual-disk-restore",
                    $"backupId={backupId} device={device} FAILED: {ex.Message}");
            }
            finally
            {
                ReleaseRestoreTarget(targetKey);
            }
        });
        OperationLog.Append(repo, "manual-disk-restore", $"backupId={backupId} device={device} started");
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// DL-6: makes sure every chunk a disk-image backup needs is staged in
    /// the local repo, and throws if any cannot be obtained. Called before
    /// RestoreDiskImage writes anything, so a restore that cannot complete
    /// never touches the target device. The pulls mirror the file-restore
    /// path (WizardState over the job's banked SMB location, then its USB
    /// location when plugged in — both idempotent, skipping chunks already
    /// staged); a closing presence check over the manifest's chunk list
    /// then proves the local repo holds them all, which also covers the
    /// case where no location resolved at all.
    /// Sparse images (empty chunk IDs, which restore as zeros) skip the
    /// pulls: the shared chunk pull enumerates a sparse slot as if it were
    /// a chunk ID and would refuse it, so a sparse backup's real chunks
    /// must already be staged locally — the presence check still refuses
    /// up front when they are not, instead of failing mid-write.
    /// </summary>
    private static void EnsureDiskImageChunksStaged(
        string repo, string backupId, string passphrase,
        string? smbShare, string? smbUser, string? smbPassword, string? usbFolder,
        CancellationToken cancellationToken = default)
    {
        // The chunk set the manifest declares — volumes or per-file chunk
        // lists, the same enumeration the chunk pull uses.
        var chunkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var r = BackupRepository.Open(repo, passphrase))
        {
            var manifest = r.LoadManifest(backupId);
            if (manifest.Volumes is { Count: > 0 })
            {
                foreach (var v in manifest.Volumes)
                    foreach (string id in v.ChunkIds)
                        chunkIds.Add(id);
            }
            else
            {
                foreach (var f in manifest.Files)
                    foreach (string id in f.Chunks)
                        chunkIds.Add(id);
            }
        }
        // Pull missing chunks from the job's banked locations first: the
        // SMB share if one resolved, and the USB drive if it is plugged
        // in. Either pull throws when a chunk it is asked for is absent
        // from that source too. Sparse positions ("") are skipped by the
        // pull itself (NS-9), so sparse images pull their real chunks too.
        if (!string.IsNullOrWhiteSpace(smbShare) || usbFolder is not null)
        {
            using var ws = new UsenetBackup.Core.Recovery.WizardState
            {
                RepoPath = repo,
                Passphrase = passphrase,
                SelectedBackupId = backupId,
                SmbShare = smbShare ?? "",
                SmbUser = smbUser ?? "",
                SmbPassword = smbPassword ?? "",
            };
            if (!string.IsNullOrWhiteSpace(smbShare))
                ws.EnsureSmbChunks(null, cancellationToken);
            if (usbFolder is not null)
                ws.EnsureLocalChunks(usbFolder, null, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Presence proof: every non-sparse chunk must now be a local
        // chunk file (chunks/<first 2 hex>/<remaining 62> — the
        // LocalBlobStore layout). Anything still missing cannot be
        // obtained: refuse before the destructive write begins.
        string localChunks = Path.Combine(repo, "chunks");
        foreach (string id in chunkIds)
        {
            if (string.IsNullOrEmpty(id))
                continue; // sparse position: restores as zeros, no chunk
            string shard = id.Length >= 2 ? id[..2] : id;
            string fileName = id.Length >= 2 ? id[2..] : id;
            if (!File.Exists(Path.Combine(localChunks, shard, fileName)))
                throw new FileNotFoundException(
                    $"Chunk {id} is not available in the local repo or from any " +
                    "backup location; disk restore aborted before writing to the target.");
        }

        // P2-V1: the proof above is existence-only. When a source
        // resolved, the pull has already content-verified (and repaired)
        // the chunks it staged; when NO source resolved, nothing has
        // checked the local chunks at all — a present-but-corrupt chunk
        // would fail only mid-write, after earlier chunks already
        // overwrote the target. Verify each chunk cryptographically
        // now, while refusing is still free.
        if (string.IsNullOrWhiteSpace(smbShare) && usbFolder is null)
        {
            using var r = BackupRepository.Open(repo, passphrase);
            foreach (string id in chunkIds)
            {
                if (string.IsNullOrEmpty(id))
                    continue; // sparse position
                try
                {
                    r.VerifyChunk(id);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException(
                        $"Chunk {id} is present locally but damaged ({ex.Message}); " +
                        "disk restore aborted before writing to the target.", ex);
                }
            }
        }
    }
}
