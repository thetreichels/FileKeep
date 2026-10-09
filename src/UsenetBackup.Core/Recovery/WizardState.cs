using System.Security.Cryptography;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;

namespace UsenetBackup.Core.Recovery;

/// <summary>
/// Testable workflow state for the recovery wizard. The WinForms UI is a thin
/// shell over this: each wizard page reads/writes these properties and calls
/// these methods. All FileKeep directives apply (no invented crypto,
/// fails-closed verification, append-only logging).
/// </summary>
public sealed class WizardState : IDisposable
{
    private BackupRepository? _repo;

    /// <summary>Path to the repo metadata folder (repo.json, manifests/, catalog.db).</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Repository passphrase. Held in memory only; never written to disk.</summary>
    public string Passphrase { get; set; } = "";

    public string NntpHost { get; set; } = "";
    public int NntpPort { get; set; } = 119;
    public bool NntpSsl { get; set; }
    public string NntpUser { get; set; } = "";
    public string NntpPassword { get; set; } = "";
    public string Newsgroup { get; set; } = "";

    /// <summary>
    /// LAN server URL (e.g., http://192.168.1.10:8477) for LAN restores.
    /// Set when user checks LAN; null if not using LAN.
    /// </summary>
    public string? LanServer { get; set; }

    /// <summary>Selected backup ID, or null if none selected yet.</summary>
    public string? SelectedBackupId { get; set; }

    /// <summary>Parsed NZB for the selected backup's chunks.</summary>
    public NzbDocument? Nzb { get; set; }

    public void Dispose() => _repo?.Dispose();

    /// <summary>
    /// Validates that <see cref="RepoPath"/> looks like repo metadata
    /// (repo.json + manifests/ present). Does not open the repo.
    /// </summary>
    public (bool ok, string error) ValidateRepoPath()
    {
        if (string.IsNullOrWhiteSpace(RepoPath))
            return (false, "Choose the folder containing the repo metadata.");
        if (!Directory.Exists(RepoPath))
            return (false, "Folder does not exist.");
        if (!File.Exists(Path.Combine(RepoPath, "repo.json")))
            return (false, "repo.json not found. This folder is not repo metadata.");
        if (!Directory.Exists(Path.Combine(RepoPath, "manifests")))
            return (false, "manifests/ not found. This folder is not repo metadata.");
        return (true, "");
    }

    /// <summary>
    /// Opens the repo with the passphrase. Note: the passphrase itself is
    /// only verified when encrypted data is touched; use
    /// <see cref="ValidatePassphrase"/> for an early check.
    /// </summary>
    public BackupRepository OpenRepo()
    {
        if (_repo is not null)
            return _repo;
        _repo = BackupRepository.Open(RepoPath, Passphrase);
        return _repo;
    }

    /// <summary>
    /// Tries to verify the passphrase by decrypting a chunk if one is
    /// available locally. Returns (true, "") on success, (false, reason) on
    /// wrong passphrase, and (true, "unverified") when there are no local
    /// chunks to check against (passphrase will be verified at download).
    /// </summary>
    public (bool ok, string note) ValidatePassphrase()
    {
        var repo = OpenRepo();
        string chunksDir = Path.Combine(RepoPath, "chunks");
        if (!Directory.Exists(chunksDir))
            return (true, "unverified");
        // Chunks are sharded: chunks/ab/cdef... -> chunk id abcdef...
        string? first = Directory.EnumerateFiles(chunksDir, "*", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (first is null)
            return (true, "unverified");
        string rel = Path.GetRelativePath(chunksDir, first);
        string chunkId = rel.Replace(Path.DirectorySeparatorChar.ToString(), "");
        if (chunkId.Length != 64)
            return (true, "unverified");
        try
        {
            // AES-GCM authentication fails here on a wrong passphrase.
            repo.VerifyChunk(chunkId);
            return (true, "");
        }
        catch (Exception ex) when (ex is CryptographicException || ex is InvalidDataException)
        {
            return (false, "Passphrase is incorrect (chunk decryption failed).");
        }
    }

    /// <summary>Lists available backups from the repo catalog.</summary>
    public IReadOnlyList<BackupSummary> ListBackups() => OpenRepo().ListBackups();

    /// <summary>
    /// Loads and parses the NZB file for chunk download.
    /// </summary>
    public void LoadNzb(string nzbPath)
    {
        string xml = File.ReadAllText(nzbPath);
        Nzb = NzbParser.Parse(xml);
    }

    /// <summary>
    /// Downloads chunks with progress callback (downloaded, total).
    /// Fails closed: bad chunks are never stored.
    /// </summary>
    public DownloadResult Download(Action<int, int>? progress = null)
    {
        if (Nzb is null)
            throw new InvalidOperationException("Load an NZB first.");
        var repo = OpenRepo();

        // If a LAN server was discovered, download chunks from it via HTTP.
        // Otherwise fall back to Usenet via NNTP.
        if (!string.IsNullOrWhiteSpace(LanServer))
        {
            using var remote = new Lan.HttpBlobStore(LanServer);
            return repo.DownloadChunks(Nzb, remote, progress);
        }

        var client = new NntpClient(NntpHost, NntpPort, NntpSsl);
        try
        {
            client.Connect();
            if (!string.IsNullOrEmpty(NntpUser))
                client.Authenticate(NntpUser, NntpPassword);
            using var remote = new NntpBlobStore(client, Newsgroup, repo.RepoId, repo.CatalogPath, messageIndex: repo.MessageIndex,
                providerKey: ChunkMessageIndex.MakeProviderKey(NntpHost, Newsgroup));
            // Fetch the latest published message-identity index BEFORE
            // downloading chunks. A retention refresh on the original
            // machine republishes articles under new message IDs; without
            // the remote index this machine would request stale (possibly
            // expired) IDs. The local index is updated as a side effect.
            try
            {
                string? remoteIndex = remote.GetLatestMessageIndex();
                if (remoteIndex is not null)
                    repo.MessageIndex.LoadFromJson(remoteIndex);
            }
            catch
            {
                // Index fetch is best-effort: fall back to the local index
                // (or deterministic IDs) if the remote index is unavailable.
            }
            return repo.DownloadChunks(Nzb, remote, progress);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Verifies the selected backup. Returns empty on success, issues on failure.
    /// </summary>
    public IReadOnlyList<string> Verify()
    {
        if (SelectedBackupId is null)
            throw new InvalidOperationException("Select a backup first.");
        return OpenRepo().Verify(SelectedBackupId);
    }

    /// <summary>Restores files to a directory.</summary>
    public void RestoreFiles(string destDir)
    {
        if (SelectedBackupId is null)
            throw new InvalidOperationException("Select a backup first.");
        OpenRepo().Restore(SelectedBackupId, destDir);
    }

    /// <summary>
    /// Lists physical drives for the restore-target picker.
    /// The enumerator is injected (WMI on Windows, fake in tests).
    /// </summary>
    public IReadOnlyList<PhysicalDriveInfo> ListPhysicalDrives(IDriveEnumerator enumerator) =>
        enumerator.ListDrives();

    /// <summary>
    /// Restores a disk image to a device. The caller must confirm the device
    /// path (typed, not selected) — mirrors the CLI's --yes safety.
    /// </summary>
    public void RestoreDisk(string devicePath, string confirmedDevicePath)
    {
        if (SelectedBackupId is null)
            throw new InvalidOperationException("Select a backup first.");
        if (!string.Equals(devicePath, confirmedDevicePath, StringComparison.Ordinal))
            throw new InvalidOperationException("Device path confirmation does not match.");
        OpenRepo().RestoreDiskImage(SelectedBackupId, devicePath);
    }

    /// <summary>
    /// Validates a drive selection for disk restore. The drive must come from
    /// <see cref="ListPhysicalDrives"/> (not free-typed). The UI confirms via
    /// a native TaskDialog before calling this.
    /// </summary>
    public void RestoreDiskToDrive(PhysicalDriveInfo drive)
    {
        if (SelectedBackupId is null)
            throw new InvalidOperationException("Select a backup first.");
        ArgumentNullException.ThrowIfNull(drive);
        OpenRepo().RestoreDiskImage(SelectedBackupId, drive.DevicePath);
    }

    /// <summary>
    /// Discovers encrypted manifests on Usenet not present locally.
    /// The caller provides a connected NntpBlobStore.
    /// </summary>
    public IReadOnlyList<BackupRepository.RemoteManifest> DiscoverRemoteManifests(NntpBlobStore remote) =>
        OpenRepo().DiscoverRemoteManifests(remote);

    /// <summary>
    /// Saves discovered remote manifests to the local manifests directory
    /// so download/restore treat them like local backups. Also records them
    /// in the catalog.
    /// </summary>
    public void SaveRemoteManifests(IReadOnlyList<BackupRepository.RemoteManifest> manifests)
    {
        var repo = OpenRepo();
        foreach (var m in manifests)
            repo.ImportManifest(m.Manifest);
    }

    /// <summary>True if the selected backup is a disk image.</summary>
    public bool SelectedIsDiskImage()
    {
        if (SelectedBackupId is null)
            return false;
        var manifest = OpenRepo().ListBackups()
            .FirstOrDefault(b => b.BackupId == SelectedBackupId);
        // BackupSummary doesn't carry kind; load the manifest for certainty.
        // For the wizard's purposes, check via the manifest file directly.
        string manifestPath = Path.Combine(RepoPath, "manifests", SelectedBackupId + ".json");
        if (!File.Exists(manifestPath))
            return false;
        string json = File.ReadAllText(manifestPath);
        return json.Contains("\"kind\":\"disk-image\"") || json.Contains("\"kind\": \"disk-image\"");
    }
}
