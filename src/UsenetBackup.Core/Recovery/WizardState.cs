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

    /// <summary>
    /// YubiKey challenge (base64) from the backup manifest, if the backup
    /// uses YubiKey protection. The challenge is public; security comes
    /// from the key's secret, which never leaves the hardware.
    /// </summary>
    public string? YubiKeyChallenge { get; set; }

    /// <summary>
    /// Raw HMAC-SHA1 response from the YubiKey touch, if the user unlocked
    /// via YubiKey. Held in memory only; wiped on dispose.
    /// </summary>
    public byte[]? YubiKeyHmacResponse { get; set; }

    /// <summary>
    /// Loads the selected backup's manifest for a state probe (MU-13).
    /// Returns null ONLY when no backup is selected or the manifest
    /// genuinely doesn't exist. A present-but-corrupt manifest (bad
    /// JSON, root-hash verification failure) THROWS: the probes below
    /// used to swallow every exception as "feature absent," so a
    /// tampered manifest steered the wizard to the wrong unlock path
    /// with no error shown.
    /// </summary>
    private BackupManifest? LoadSelectedManifestForProbe()
    {
        if (SelectedBackupId is null)
            return null;
        try { return OpenRepo().LoadManifest(SelectedBackupId); }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>
    /// True if the selected backup's manifest carries a YubiKey challenge
    /// (i.e. YubiKey unlock is available for this backup). Throws when
    /// the manifest exists but is corrupt — see the probe contract on
    /// <see cref="LoadSelectedManifestForProbe"/>.
    /// </summary>
    public bool SelectedBackupHasYubiKeyChallenge()
    {
        var manifest = LoadSelectedManifestForProbe();
        return manifest is not null && !string.IsNullOrEmpty(manifest.YubiKeyChallenge);
    }

    /// <summary>
    /// Loads the YubiKey challenge from the selected backup's manifest into
    /// <see cref="YubiKeyChallenge"/>. Returns false if none is present.
    /// Throws when the manifest exists but is corrupt (MU-13).
    /// </summary>
    public bool LoadYubiKeyChallenge()
    {
        var manifest = LoadSelectedManifestForProbe();
        if (manifest is null)
            return false;
        YubiKeyChallenge = string.IsNullOrEmpty(manifest.YubiKeyChallenge)
            ? null : manifest.YubiKeyChallenge;
        return YubiKeyChallenge is not null;
    }

    /// <summary>
    /// Unlocks the repo using a YubiKey HMAC-SHA1 response (from a physical
    /// touch). Derives the KEK, unwraps the passphrase from the manifest,
    /// and opens the repo — no passphrase typing required.
    /// The HMAC response is wiped after use.
    /// </summary>
    public void UnlockWithYubiKey(byte[] hmacResponse)
    {
        if (SelectedBackupId is null)
            throw new InvalidOperationException("Select a backup first.");
        if (hmacResponse is null || hmacResponse.Length == 0)
            throw new ArgumentException("HMAC response must not be empty.", nameof(hmacResponse));

        try
        {
            var manifest = OpenRepo().LoadManifest(SelectedBackupId);
            if (string.IsNullOrEmpty(manifest.YubiKeyWrappedPassphrase))
                throw new InvalidOperationException(
                    "This backup has no YubiKey-wrapped passphrase. Type your passphrase instead.");

            byte[] kek = UsenetBackup.Core.Security.YubiKeyCrypto.DeriveKek(hmacResponse);
            try
            {
                // CryptographicException here = wrong key (or tampered blob).
                Passphrase = UsenetBackup.Core.Security.YubiKeyCrypto.Unwrap(
                    manifest.YubiKeyWrappedPassphrase, kek);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
            // Store the HMAC for the session; wiped on Dispose.
            YubiKeyHmacResponse = (byte[])hmacResponse.Clone();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmacResponse);
        }
    }

    /// <summary>
    /// Generalizes <see cref="ValidatePassphrase"/>: validates whichever
    /// credential is set (typed passphrase or YubiKey-unwrapped passphrase).
    /// </summary>
    public (bool ok, string note) ValidateCredentials() => ValidatePassphrase();

    public string NntpHost { get; set; } = "";
    public int NntpPort { get; set; } = 119;
    public bool NntpSsl { get; set; }
    public string NntpUser { get; set; } = "";
    public string NntpPassword { get; set; } = "";
    public string Newsgroup { get; set; } = "";

    /// <summary>
    /// SMB share UNC path (e.g., \\NAS\backups) for SMB restores.
    /// Set when the user checks a share; null if not using SMB.
    /// </summary>
    public string? SmbShare { get; set; }

    /// <summary>SMB username (may be empty for guest/anonymous shares).</summary>
    public string SmbUser { get; set; } = "";

    /// <summary>
    /// SMB password. Held in memory only; never written to disk.
    /// Cleared from the UI after the share check completes.
    /// </summary>
    public string SmbPassword { get; set; } = "";

    /// <summary>Selected backup ID, or null if none selected yet.</summary>
    public string? SelectedBackupId { get; set; }

    /// <summary>Parsed NZB for the selected backup's chunks.</summary>
    public NzbDocument? Nzb { get; set; }

    public void Dispose()
    {
        if (YubiKeyHmacResponse is not null)
        {
            CryptographicOperations.ZeroMemory(YubiKeyHmacResponse);
            YubiKeyHmacResponse = null;
        }
        _repo?.Dispose();
    }

    /// <summary>
    /// Validates that <see cref="RepoPath"/> looks like repo metadata
    /// (repo.json + manifests/ present). Does not open the repo.
    /// </summary>
    public (bool ok, string error) ValidateRepoPath()
    {
        if (string.IsNullOrWhiteSpace(RepoPath))
            return (false, "Pick a backup from the list, or browse to the folder it's in.");
        if (!Directory.Exists(RepoPath))
            return (false, "Folder does not exist.");
        if (!File.Exists(Path.Combine(RepoPath, "repo.json")))
            return (false, "That folder doesn't look like a FileKeep backup (no backup catalog found).");
        if (!Directory.Exists(Path.Combine(RepoPath, "manifests")))
            return (false, "That folder doesn't look like a FileKeep backup (no backup data found).");
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
    /// <remarks>
    /// Generated-secret fallback: when the as-typed value fails chunk
    /// verification and it normalizes (<see cref="Security.GeneratedSecret.Normalize"/>)
    /// to a plausible canonical recovery-sheet secret different from what
    /// was typed — the grouped/lowercase form a user copies from the
    /// printed sheet — the normalized string is tried once. On success it
    /// replaces <see cref="Passphrase"/> for the whole session, so the
    /// re-opened repo and every later consumer use it. The as-typed value
    /// is always tried first, untouched, so ordinary passphrases (with
    /// spaces, lowercase, anything) behave exactly as before; a value
    /// that already works is never transformed, and a value whose
    /// normalized form is not canonical-shaped is never retried. If the
    /// retry also fails, the as-typed state is restored, leaving no
    /// trace of the attempt. The unverified success (no local chunks to
    /// check against — the common Usenet-restore case) gets the same
    /// treatment for the opposite reason: nothing proved the as-typed
    /// value, so if it is a sheet secret typed in display form, the
    /// canonical string is adopted immediately; otherwise the grouped
    /// form would stay in force and the restore would fail later, at
    /// the first downloaded chunk, with the wrong derived key. This
    /// method is the single funnel: the
    /// wizard's credentials page validates only through here (and
    /// <see cref="ValidateCredentials"/> delegates here), and only this
    /// class owns the cached repo whose derived key must be rebuilt when
    /// the credential changes.
    /// </remarks>
    public (bool ok, string note) ValidatePassphrase()
    {
        var result = ValidatePassphraseOnce();
        if (result.ok)
        {
            if (result.note == "unverified")
                AdoptNormalizedSheetSecret();
            return result;
        }

        string typed = Passphrase;
        string normalized = Security.GeneratedSecret.Normalize(typed);
        if (normalized == typed || !Security.GeneratedSecret.IsCanonicalForm(normalized))
            return result;

        // The cached repo holds the key derived from the as-typed value;
        // drop it so the retry (and, on success, the whole session) runs
        // on the normalized string.
        ResetRepo();
        Passphrase = normalized;
        var retry = ValidatePassphraseOnce();
        if (retry.ok)
            return retry;

        // Both forms failed: restore the as-typed state verbatim.
        ResetRepo();
        Passphrase = typed;
        return result;
    }

    /// <summary>Drops the cached repo so the next <see cref="OpenRepo"/> re-derives the key from the current <see cref="Passphrase"/>.</summary>
    private void ResetRepo()
    {
        _repo?.Dispose();
        _repo = null;
    }

    /// <summary>
    /// Companion to the failure retry in <see cref="ValidatePassphrase"/>
    /// for the unverified-success branch: when the typed value normalizes
    /// to a canonical recovery-sheet secret different from itself, adopt
    /// the canonical string as the session passphrase and rebuild the
    /// cached repo on it (same reset/re-open mechanics as the retry), so
    /// chunks downloaded later derive — and verify — under the right key.
    /// A value that cannot normalize to canonical form (an ordinary
    /// passphrase) is left exactly as typed.
    /// </summary>
    private void AdoptNormalizedSheetSecret()
    {
        string typed = Passphrase;
        string normalized = Security.GeneratedSecret.Normalize(typed);
        if (normalized == typed || !Security.GeneratedSecret.IsCanonicalForm(normalized))
            return;

        ResetRepo();
        Passphrase = normalized;
        // Nothing can be verified here (that is why this is the
        // unverified branch), but if the re-open under the canonical
        // string unexpectedly fails, restore the as-typed state rather
        // than keep a broken session.
        if (!ValidatePassphraseOnce().ok)
        {
            ResetRepo();
            Passphrase = typed;
        }
    }

    /// <summary>One as-is verification attempt against the current <see cref="Passphrase"/>.</summary>
    private (bool ok, string note) ValidatePassphraseOnce()
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
            using var remote = new NntpBlobStore(client, Newsgroup, repo.RepoId, repo.CatalogPath, messageIndex: repo.MessageIndex,
                providerKey: ChunkMessageIndex.MakeProviderKey(NntpHost, Newsgroup));
            // Volume articles are volume-sized, not chunk-sized.
            remote.MaxArticleBytes = Math.Max(repo.MaxDownloadBytes, repo.VolumeSizeBytes * 2);
            // Fetch the latest published message-identity index BEFORE
            // downloading chunks. A retention refresh on the original
            // machine republishes articles under new message IDs; without
            // the remote index this machine would request stale (possibly
            // expired) IDs. The local index is updated as a side effect.
            // Throws an explicit recovery error if an index was published
            // but cannot be retrieved or parsed — never silently falls back.
            remote.SyncMessageIndex(repo.MessageIndex);
            return repo.DownloadNzb(Nzb, remote, progress);
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

    /// <summary>Restores files to a directory (classic snapshot/overwrite behavior).</summary>
    public void RestoreFiles(string destDir, Action<int, int>? progress = null) =>
        RestoreFiles(destDir, MergePolicy.Overwrite, progress);

    /// <summary>
    /// Restores files to a directory. <paramref name="policy"/> decides what
    /// happens when a file already exists in the destination (chosen by the
    /// user on the wizard's restore page). Returns the restore outcome so
    /// the UI can report skipped files.
    /// </summary>
    public RestoreResult RestoreFiles(string destDir, MergePolicy policy, Action<int, int>? progress = null)
    {
        if (SelectedBackupId is null)
            throw new InvalidOperationException("Select a backup first.");
        EnsureSmbChunks(progress);
        return OpenRepo().Restore(SelectedBackupId, destDir, new RestoreOptions { MergePolicy = policy });
    }

    /// <summary>
    /// If an SMB share was checked, copies the selected backup's chunks from
    /// the share into the local repo before restore. Chunks are
    /// content-addressed: files already present are skipped, so this is
    /// idempotent and resumable. Verifies the manifest exists on the share.
    /// </summary>
    public void EnsureSmbChunks(Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SmbShare) || SelectedBackupId is null)
            return;
        using var smb = UsenetBackup.Core.SmbShare.Connect(
            SmbShare,
            string.IsNullOrEmpty(SmbUser) ? null : SmbUser,
            string.IsNullOrEmpty(SmbPassword) ? null : SmbPassword,
            createIfMissing: false);
        EnsureChunksFrom(smb.Combine("chunks"),
            id => $"Chunk {id} not found on SMB share {SmbShare}.", progress,
            cancellationToken);
    }

    /// <summary>
    /// If a plain local folder holds a copy of this backup — e.g. a USB
    /// backup location resolved to its current drive path — copies the
    /// selected backup's missing chunks from it into the local repo
    /// before restore. The folder layout is identical to a share's
    /// (chunks/manifests/repo.json, written by the scheduler's location
    /// sync), and the pull is the same content-addressed, idempotent
    /// copy as the SMB path. (WALK-8: the dashboard restore never looked
    /// at USB locations, so a USB-backed restore failed on every chunk
    /// once local staging was pruned.)
    /// </summary>
    public void EnsureLocalChunks(string folderRoot, Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderRoot) || SelectedBackupId is null)
            return;
        EnsureChunksFrom(Path.Combine(folderRoot, "chunks"),
            id => $"Chunk {id} not found in folder {folderRoot}.", progress,
            cancellationToken);
    }

    /// <summary>
    /// Copies the selected backup's chunks from a chunks root — an SMB
    /// share or a plain folder, same sharded layout — into the local
    /// repo, skipping chunks already present.
    /// </summary>
    private void EnsureChunksFrom(string remoteChunks, Func<string, string> notFoundMessage,
        Action<int, int>? progress, CancellationToken cancellationToken = default)
    {
        var repo = OpenRepo();
        var manifest = repo.LoadManifest(SelectedBackupId!);
        // Collect chunk IDs from the manifest (volumes or per-file chunk lists).
        var chunkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Volumes is { Count: > 0 })
        {
            foreach (var v in manifest.Volumes)
                foreach (string id in v.ChunkIds)
                    // Sparse positions are recorded as "" — there is no
                    // chunk to pull; they restore as zeros (NS-9).
                    if (!string.IsNullOrEmpty(id))
                        chunkIds.Add(id);
        }
        else
        {
            foreach (var f in manifest.Files)
                foreach (string id in f.Chunks)
                    if (!string.IsNullOrEmpty(id))
                        chunkIds.Add(id);
        }
        string localChunks = Path.Combine(RepoPath, "chunks");
        int done = 0;
        int total = chunkIds.Count;
        foreach (string id in chunkIds)
        {
            // Per-restore cancel (item 7, 2026-10-22): the pull honors
            // the restore's token between chunks, so cancelling during
            // a long pull stops promptly instead of after the last copy.
            cancellationToken.ThrowIfCancellationRequested();
            // Chunk files are sharded: chunks/ab/cdef... (first 2 hex chars
            // as the directory, remaining 62 as the file name) — same layout
            // as LocalBlobStore.
            string shard = id.Length >= 2 ? id[..2] : id;
            string fileName = id.Length >= 2 ? id[2..] : id;
            string localPath = Path.Combine(localChunks, shard, fileName);
            // SF-13: an existing local file counts only if it VERIFIES —
            // a corrupt local chunk used to be trusted forever and the
            // pull below copied straight to the final path, so a torn
            // copy became permanent too. Pull to a temp file, verify,
            // then move into place.
            bool haveGoodLocal = File.Exists(localPath) && repo.VerifyChunkBlobAt(localPath, id);
            if (!haveGoodLocal)
            {
                string remotePath = Path.Combine(remoteChunks, shard, fileName);
                if (!File.Exists(remotePath))
                    throw new FileNotFoundException(notFoundMessage(id));
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                string tmpPath = localPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(remotePath, tmpPath);
                    if (!repo.VerifyChunkBlobAt(tmpPath, id))
                        throw new InvalidDataException(
                            $"Chunk {id} pulled from {remoteChunks} failed verification; it was not stored.");
                    File.Move(tmpPath, localPath, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }
            }
            done++;
            if (done % 25 == 0) progress?.Invoke(done, total);
        }
        progress?.Invoke(done, total);
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
    /// a native TaskDialog before calling this. System drives require explicit
    /// opt-in via <paramref name="allowSystemDrive"/>.
    /// </summary>
    public void RestoreDiskToDrive(PhysicalDriveInfo drive, Action<int, int>? progress = null,
        bool allowSystemDrive = false)
    {
        if (SelectedBackupId is null)
            throw new InvalidOperationException("Select a backup first.");
        ArgumentNullException.ThrowIfNull(drive);
        if (drive.IsSystemDrive && !allowSystemDrive)
            throw new InvalidOperationException(
                $"Refusing to restore to {drive.DevicePath}: it hosts the running Windows installation. " +
                "If you are sure, confirm explicitly.");
        EnsureSmbChunks(progress);
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

    /// <summary>
    /// True if the selected backup is a disk image. Throws when the
    /// manifest exists but is corrupt (MU-13) — a corrupt manifest must
    /// not be steered down the file-restore path as "not a disk image."
    /// </summary>
    public bool SelectedIsDiskImage()
    {
        var manifest = LoadSelectedManifestForProbe();
        return manifest?.IsDiskImage ?? false;
    }
}
