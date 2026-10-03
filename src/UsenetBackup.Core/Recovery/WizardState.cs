using System.Security.Cryptography;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;

namespace UsenetBackup.Core.Recovery;

/// <summary>
/// Testable workflow state for the recovery wizard. The WinForms UI is a thin
/// shell over this: each wizard page reads/writes these properties and calls
/// these methods. All Usenet Backup directives apply (no invented crypto,
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
        var client = new NntpClient(NntpHost, NntpPort, NntpSsl);
        try
        {
            client.Connect();
            if (!string.IsNullOrEmpty(NntpUser))
                client.Authenticate(NntpUser, NntpPassword);
            using var remote = new NntpBlobStore(client, Newsgroup, repo.RepoId, repo.CatalogPath);
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
