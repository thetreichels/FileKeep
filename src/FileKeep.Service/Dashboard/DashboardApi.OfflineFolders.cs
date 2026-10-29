using System.Security.Cryptography;
using System.Text;
using FileKeep.Core;
using FileKeep.Core.Security;
using FileKeep.Core.Service;

namespace FileKeep.Service;

/// <summary>
/// DashboardApi — the Offline Folders sidebar card, slice 1: locked card
/// + encrypted catalog. The memory-served double-click open,
/// update/reload, and forensic lockup mechanism is the NEXT slice and
/// deliberately does not exist here: clicking a folder selects it only,
/// and no endpoint serves file contents.
///
/// Leak discipline, enforced in every method below:
///   - The locked status response carries one boolean and nothing else:
///     no names, no counts, no hint whether a catalog file even exists.
///   - No log call on these paths ever includes a folder name or a
///     location id, and no error message echoes one either.
///   - The decrypted catalog lives only in <see cref="OfflineFoldersSession"/>
///     memory and is wiped on lock/expiry; the file on disk is
///     ciphertext only (see <see cref="OfflineFoldersCatalog"/>).
///
/// Unlock proofs mirror the security-config gate exactly: the current
/// passphrase, the typed recovery secret in generated-secret mode,
/// the added passphrase in generated mode (unwrapped to the root
/// secret), or a YubiKey touch. A TOTP/phone code is NEVER a proof
/// here — the gate has no phone path at all — and an unlocked
/// restore session is NOT a proof either: this session is separate,
/// and unlocking one never unlocks the other. Adding/removing
/// entries requires the unlocked card session, not a fresh proof per
/// call: the session itself IS the fresh proof, bounded by the
/// 1-hour idle expiry, the same way the restore session gates
/// restore endpoints.
/// </summary>
public static partial class DashboardApi
{
    /// <summary>
    /// Card status. When locked the payload is exactly
    /// <c>{ locked: true }</c> — nothing else is ever reported.
    /// </summary>
    public static (int Status, object Payload) GetOfflineFoldersStatus(
        OfflineFoldersSession session) =>
        (200, new OfflineFoldersStatusDto(Locked: !session.IsUnlocked));

    /// <summary>
    /// Unlocks the card: validates a fresh proof, derives the catalog
    /// key AND the file-content key from the proven root secret, and
    /// loads the decrypted catalog into the session. With no catalog
    /// file yet, the session starts empty and the file is created on
    /// the first add.
    /// </summary>
    public static (int Status, object Payload) UnlockOfflineFolders(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        string? proofPassphrase, bool proofYubiKey,
        IYubiKeyProvider? yubiKeyProvider, Action<string>? log = null)
    {
        if (!HasEnabledUnlockMethod(config))
            return (400, new ErrorDto(
                "Offline Folders needs an unlock method first — set up a passphrase or YubiKey in Security."));

        string? secret = ResolveCardUnlockSecret(config, proofPassphrase, proofYubiKey, yubiKeyProvider);
        if (secret is null)
            return (403, new ErrorDto(
                "Unlocking Offline Folders requires proof of an existing unlock method."));

        byte[] key = OfflineFoldersCatalog.DeriveKey(secret);
        byte[] contentKey = OfflineFileStore.DeriveContentKey(secret);
        try
        {
            List<OfflineFolderEntry> entries;
            if (File.Exists(catalogPath))
            {
                try { entries = OfflineFoldersCatalog.Load(catalogPath, key); }
                catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException or IOException)
                {
                    return (400, new ErrorDto(
                        "The offline folders catalog cannot be decrypted with this vault secret — " +
                        "it may have been created under a different secret, or the file is damaged."));
                }
            }
            else
            {
                entries = new List<OfflineFolderEntry>();
            }
            session.Unlock(key, contentKey, entries);
            log?.Invoke("dashboard: offline folders unlocked");
            return (200, new OkDto(true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    /// <summary>
    /// Rekeys the Offline Folders catalog and its chunks from the
    /// current secret to <paramref name="newSecret"/> (the rewrite
    /// half of rekey — see <see cref="OfflineRekey"/>). Runs BEFORE
    /// the passphrase change it prepares for: the old secret is the
    /// currently configured one (resolved on this PC, or typed as
    /// <paramref name="oldSecret"/> when it cannot be resolved), and
    /// the new secret is typed here. Requires fresh proof of an
    /// existing method (the shared gate), refuses in YubiKey-only
    /// mode (the root secret never changes there), and refuses while
    /// the card is unlocked — rekey replaces the keys the open
    /// session holds, so the user locks, rekeys, changes the
    /// passphrase, and re-unlocks under the new secret. A partial
    /// run still returns 200 with its failures listed and the
    /// catalog unsaved; the user fixes the failures and re-runs
    /// with the same secrets.
    /// </summary>
    public static (int Status, object Payload) RekeyOfflineFolders(
        ServiceConfig config, string configPath, OfflineFoldersSession session,
        string? oldSecret, string? newSecret, IYubiKeyProvider? yubiKeyProvider,
        Action<string>? log = null, string? proofPassphrase = null,
        bool proofYubiKey = false, string? proofTotp = null)
    {
        var proof = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proof is not null)
            return proof.Value;
        if (config.GeneratedSecretMode)
            return (400, new ErrorDto(
                "Offline rekey is a passphrase-mode operation: in YubiKey-only mode the root secret " +
                "never changes, so the catalog never needs rekeying."));
        if (session.IsUnlocked)
            return (400, new ErrorDto(
                "Lock Offline Folders first — rekey replaces the keys the open session holds, " +
                "so it runs only while the card is locked."));
        if (string.IsNullOrEmpty(newSecret))
            return (400, new ErrorDto("A new secret is required."));
        string? old = oldSecret;
        if (string.IsNullOrEmpty(old))
        {
            if (!BackupScheduler.TryGetPassphrase(config, out string? current, out _) ||
                string.IsNullOrEmpty(current))
                return (400, new ErrorDto(
                    "The current secret could not be resolved on this PC — type it as oldSecret."));
            old = current;
        }
        string catalogPath = OfflineFoldersCatalog.CatalogPathFor(configPath);
        if (!File.Exists(catalogPath))
            return (400, new ErrorDto("No offline folders catalog exists — nothing to rekey."));
        OfflineRekeySummaryDto summary;
        try
        {
            summary = OfflineRekey.Rewrite(catalogPath, old, newSecret, OfflineStoreResolver(config), log);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException or IOException)
        {
            return (400, new ErrorDto(
                "The catalog could not be decrypted with the current secret — it may already be under " +
                "a different secret, or the file is damaged."));
        }
        if (summary.Failures.Count > 0)
            log?.Invoke("dashboard: offline folders rekey incomplete — catalog left under the old secret; fix the failures and re-run.");
        return (200, summary);
    }

    /// <summary>Maps a folder's location name to its chunk store (USB/SMB only).</summary>
    private static Func<string, OfflineFileStore.OfflineLocationStore> OfflineStoreResolver(ServiceConfig config) =>
        locationId =>
        {
            var location = config.SnapshotLocations().FirstOrDefault(l =>
                string.Equals(l.Name, locationId, StringComparison.OrdinalIgnoreCase));
            if (location is null)
                throw new InvalidOperationException($"Location '{locationId}' no longer exists.");
            return OfflineFileStore.ResolveStore(location);
        };

    /// <summary>
    /// SetPassphrase hook (auto-rekey, fail-closed): when the
    /// passphrase is being replaced in passphrase mode and an
    /// offline catalog file exists, rewrites the catalog and its
    /// chunks from the currently configured secret to
    /// <paramref name="newPassphrase"/> BEFORE the config is
    /// touched. Returns null when there is nothing to do; otherwise
    /// an error result that aborts the passphrase change with the
    /// config untouched.
    /// </summary>
    public static (int Status, object Payload)? RekeyOfflineForPassphraseChange(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        string newPassphrase,
        Func<string, OfflineFileStore.OfflineLocationStore> resolveStore,
        Action<string>? log = null)
    {
        if (!File.Exists(catalogPath))
            return null;
        if (session.IsUnlocked)
            return (400, new ErrorDto(
                "Lock Offline Folders first — changing the passphrase rekeys the offline catalog, " +
                "which runs only while the card is locked. The passphrase was NOT changed."));
        // Already under the new secret (a manual rekey ran first, or
        // a no-op change): nothing to do.
        try
        {
            _ = OfflineFoldersCatalog.Load(catalogPath, OfflineFoldersCatalog.DeriveKey(newPassphrase));
            return null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException or IOException)
        {
            // Not under the new secret — fall through to the rewrite.
        }
        if (!BackupScheduler.TryGetPassphrase(config, out string? oldSecret, out string? problem) ||
            string.IsNullOrEmpty(oldSecret))
            return (400, new ErrorDto(
                "An offline folders catalog exists that is not under the new passphrase, and the current " +
                "secret could not be resolved on this PC" +
                (string.IsNullOrEmpty(problem) ? "" : $" ({problem})") +
                " — rekey Offline Folders manually (Stealth tab, old secret typed) or delete the catalog " +
                "file, then retry. The passphrase was NOT changed."));
        OfflineRekeySummaryDto summary;
        try
        {
            summary = OfflineRekey.Rewrite(catalogPath, oldSecret, newPassphrase, resolveStore, log);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException or IOException)
        {
            return (400, new ErrorDto(
                "The offline folders catalog could not be decrypted with the current secret — it may already be " +
                "under a different secret, or the file is damaged. Rekey it manually (Stealth tab) before " +
                "changing the passphrase. The passphrase was NOT changed."));
        }
        if (summary.Failures.Count > 0)
            return (400, new ErrorDto(
                $"Offline rekey hit {summary.Failures.Count} failure(s) — the catalog was left under the old " +
                $"secret and the passphrase was NOT changed. Fix the failures and retry, or rekey manually " +
                $"(Stealth tab). First failure: {summary.Failures[0]}"));
        log?.Invoke($"dashboard: offline folders auto-rekeyed for passphrase change " +
            $"({summary.Rewritten} chunks rewritten, {summary.Skipped} skipped).");
        return null;
    }

    /// <summary>
    /// Exports one folder as an encrypted package (slice 3, part 2):
    /// the folder's manifest plus a location pointer, under a fresh
    /// export passphrase. Requires the unlocked card (the entries
    /// live only in the session). No credentials and no plaintext
    /// filenames leave in the package; the passphrase is never
    /// logged.
    /// </summary>
    public static (int Status, object Payload) ExportOfflineFolder(
        ServiceConfig config, OfflineFoldersSession session, string folderName,
        string? exportPassphrase, Action<string>? log = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            return (404, new ErrorDto("No such offline folder."));
        if (string.IsNullOrEmpty(exportPassphrase))
            return (400, new ErrorDto("An export passphrase is required."));
        var location = config.SnapshotLocations().FirstOrDefault(l =>
            string.Equals(l.Name, folder.LocationId, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return (400, new ErrorDto(
                $"The folder's location '{folder.LocationId}' no longer exists — nothing to point the export at."));
        OfflineExportPackageDto package;
        try { package = OfflineExport.CreatePackage(folder, location, exportPassphrase); }
        catch (InvalidOperationException ex) { return (400, new ErrorDto(ex.Message)); }
        log?.Invoke($"dashboard: offline folder '{folder.Name}' exported ({folder.Files.Count} files).");
        return (200, new OfflineExportResultDto($"{folder.Name}.filekeep-export.json", package));
    }

    /// <summary>
    /// Exports a single file as an encrypted package (slice 3,
    /// part 2): the same envelope as a folder export, with the
    /// folder name and location pointer unchanged and Files
    /// containing exactly that one file. Requires the unlocked
    /// card. The download filename is the folder name plus the
    /// file's basename, sanitized so it is a safe filename.
    /// </summary>
    public static (int Status, object Payload) ExportOfflineFile(
        ServiceConfig config, OfflineFoldersSession session, string folderName,
        string filePath, string? exportPassphrase, Action<string>? log = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            return (404, new ErrorDto("No such offline folder."));
        var file = folder.Files.FirstOrDefault(f =>
            string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
        if (file is null)
            return (404, new ErrorDto("No such offline file."));
        if (string.IsNullOrEmpty(exportPassphrase))
            return (400, new ErrorDto("An export passphrase is required."));
        var location = config.SnapshotLocations().FirstOrDefault(l =>
            string.Equals(l.Name, folder.LocationId, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return (400, new ErrorDto(
                $"The folder's location '{folder.LocationId}' no longer exists — nothing to point the export at."));
        OfflineExportPackageDto package;
        try { package = OfflineExport.CreateFilePackage(folder, file, location, exportPassphrase); }
        catch (InvalidOperationException ex) { return (400, new ErrorDto(ex.Message)); }
        log?.Invoke($"dashboard: offline file '{file.Path}' from folder '{folder.Name}' exported.");
        return (200, new OfflineExportResultDto(SafeExportFileName(folder.Name, file.Path), package));
    }

    /// <summary>
    /// Exports a folder as a full export (slice 3, part 3): the
    /// encrypted manifest package plus every chunk, into
    /// <c>destination/FileKeepReader/</c> (manifest +
    /// <c>chunks/</c>, the on-stick layout the portable reader
    /// reads). Chunks are copied before the manifest is written,
    /// so the manifest is the commit marker. A chunk missing from
    /// the source does not fail the export: it is reported in
    /// the summary, and the reader reports that file incomplete
    /// per file — never garbled. Requires the unlocked card.
    /// </summary>
    public static (int Status, object Payload) ExportOfflineFull(
        ServiceConfig config, OfflineFoldersSession session, string folderName,
        string destDir, string? exportPassphrase, Action<string>? log = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            return (404, new ErrorDto("No such offline folder."));
        if (string.IsNullOrEmpty(exportPassphrase))
            return (400, new ErrorDto("An export passphrase is required."));
        if (string.IsNullOrWhiteSpace(destDir))
            return (400, new ErrorDto("A destination folder is required."));
        var location = config.SnapshotLocations().FirstOrDefault(l =>
            string.Equals(l.Name, folder.LocationId, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return (400, new ErrorDto(
                $"The folder's location '{folder.LocationId}' no longer exists — nothing to copy chunks from."));
        OfflineFileStore.OfflineLocationStore store;
        try { store = OfflineFileStore.ResolveStore(location); }
        catch (InvalidOperationException ex) { return (400, new ErrorDto(ex.Message)); }
        try
        {
            return ExportOfflineFull(folder, location, store.Store, destDir, exportPassphrase, log);
        }
        finally { store.Dispose(); }
    }

    /// <summary>Full-export core over an explicit source store
    /// (test seam: tests drive it with a plain local store instead
    /// of a live USB/SMB location).</summary>
    internal static (int Status, object Payload) ExportOfflineFull(
        OfflineFolderEntry folder, BackupLocation location, IBlobStore source,
        string destDir, string exportPassphrase, Action<string>? log = null)
    {
        string readerDir = FileKeep.Core.OfflineStickLayout.ReaderFolder(destDir);
        var dest = new LocalBlobStore(readerDir);
        int copied = 0, skipped = 0;
        var missingFiles = new List<string>();
        int missingChunks = 0;
        foreach (var file in folder.Files)
        {
            bool fileMissing = false;
            foreach (string chunkId in file.ChunkIds)
            {
                if (dest.Exists(chunkId)) { skipped++; continue; }
                byte[] blob;
                try { blob = source.Get(chunkId); }
                catch (InvalidDataException) { fileMissing = true; missingChunks++; continue; }
                dest.Put(chunkId, blob);
                CryptographicOperations.ZeroMemory(blob);
                copied++;
            }
            if (fileMissing) missingFiles.Add(file.Path);
        }
        OfflineExportPackageDto package;
        try { package = OfflineExport.CreatePackage(folder, location, exportPassphrase); }
        catch (InvalidOperationException ex) { return (400, new ErrorDto(ex.Message)); }
        string manifestPath = FileKeep.Core.OfflineStickLayout.ManifestPath(destDir);
        string json = System.Text.Json.JsonSerializer.Serialize(package,
            DashboardJsonContext.Default.OfflineExportPackageDto);
        FileKeep.Core.OfflineStickLayout.WritePackageAtomic(manifestPath, json);
        log?.Invoke($"dashboard: offline folder '{folder.Name}' full-exported to '{readerDir}' " +
            $"({folder.Files.Count} files, {copied} chunks copied, {skipped} already present, {missingChunks} missing).");
        return (200, new OfflineExportFullSummaryDto(folder.Name, folder.Files.Count,
            folder.Files.Sum(f => f.ChunkIds.Count), copied, skipped,
            missingChunks, missingFiles, readerDir));
    }

    /// <summary>File name portion of a catalog path, split on either separator (paths may arrive either way).</summary>
    internal static string FileBaseName(string path)
    {
        int cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>
    /// Sanitizes a folder + file pair into a download filename: the
    /// folder name, a dash, and the file's basename, with
    /// Windows-invalid filename characters replaced by dashes.
    /// </summary>
    internal static string SafeExportFileName(string folderName, string filePath)
    {
        string raw = $"{folderName}-{FileBaseName(filePath)}.filekeep-export.json";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(invalid.Contains(c) ? '-' : c);
        return sb.ToString();
    }

    /// <summary>
    /// Imports an export package into this machine's catalog,
    /// re-encrypting it under the card secret. Requires the unlocked
    /// card. No data moves — chunks already sit on the destination,
    /// content-addressed; only the knowledge of what they add up to
    /// travels. A package for a folder name that already exists
    /// MERGES into that folder (a per-file export into its folder,
    /// or a folder re-import): same-path entries are replaced by
    /// the imported entry, new paths are appended, and the summary
    /// reports Merged. A package for a new name creates the folder.
    /// Warns when no local location matches the pointer (the folder
    /// will not resolve until one is added) or when a same-named
    /// local location is a different type.
    /// </summary>
    public static (int Status, object Payload) ImportOfflineFolder(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        OfflineExportPackageDto package, string? exportPassphrase,
        Action<string>? log = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        if (string.IsNullOrEmpty(exportPassphrase))
            return (400, new ErrorDto("The export passphrase is required."));
        OfflineExport.Payload payload;
        try { payload = OfflineExport.DecryptPackage(package, exportPassphrase); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            return (400, new ErrorDto(
                "The package could not be decrypted — wrong export passphrase, or the file is damaged or not an export."));
        }
        // A per-file package holds exactly one file; a folder-scope
        // package may hold any number. Merge semantics are the same
        // for both: an existing folder merges (same-path entries are
        // replaced by the imported entry, new paths are appended), a
        // new name creates the folder.
        if (string.Equals(payload.Scope, OfflineExport.ScopeFile, StringComparison.Ordinal) &&
            payload.Files.Count != 1)
            return (400, new ErrorDto("A per-file export package holds exactly one file."));
        var existing = entries.FirstOrDefault(e =>
            string.Equals(e.Name, payload.Folder, StringComparison.OrdinalIgnoreCase));
        bool merged;
        if (existing is not null)
        {
            // Merge: same-path entries are replaced by the imported
            // entry, new paths are appended. The list is replaced,
            // never mutated in place, so a session snapshot cannot
            // observe a half-updated index. The folder keeps its
            // existing location pointer: a per-file package carries
            // the same pointer as its folder, and a folder re-import
            // must not silently re-point a folder the user may have
            // repaired to a different location.
            var mergedFiles = existing.Files.ToList();
            foreach (var f in payload.Files)
            {
                var imported = new OfflineFileEntry(
                    f.Path, f.Size, f.ModifiedUtc, f.ChunkIds.ToList());
                mergedFiles.RemoveAll(e =>
                    string.Equals(e.Path, f.Path, StringComparison.OrdinalIgnoreCase));
                mergedFiles.Add(imported);
            }
            existing.Files = mergedFiles;
            merged = true;
        }
        else
        {
            var entry = new OfflineFolderEntry(payload.Folder, payload.Location.Name, DateTimeOffset.UtcNow)
            {
                Files = payload.Files.Select(f => new OfflineFileEntry(
                    f.Path, f.Size, f.ModifiedUtc, f.ChunkIds.ToList())).ToList(),
            };
            entries.Add(entry);
            merged = false;
        }
        var save = PersistOfflineFolders(session, catalogPath, entries);
        if (save is not null)
            return save.Value;
        session.SetEntries(entries);
        var warnings = ImportLocationWarnings(config, payload);
        log?.Invoke($"dashboard: offline folder '{payload.Folder}' imported ({payload.Files.Count} files{(merged ? ", merged" : "")}).");
        return (200, new OfflineImportSummaryDto(payload.Folder, payload.Files.Count,
            payload.Files.Sum(f => f.ChunkIds.Count), warnings, merged));
    }

    /// <summary>Location-pointer warnings shared by folder and per-file imports.</summary>
    private static List<string> ImportLocationWarnings(ServiceConfig config, OfflineExport.Payload payload)
    {
        var warnings = new List<string>();
        var local = config.SnapshotLocations().FirstOrDefault(l =>
            string.Equals(l.Name, payload.Location.Name, StringComparison.OrdinalIgnoreCase));
        if (local is null)
            warnings.Add($"No local location named '{payload.Location.Name}' — the folder will not resolve until one is added.");
        else if (!string.Equals(local.Type, payload.Location.Type, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Local location '{payload.Location.Name}' is '{local.Type}' but the package says '{payload.Location.Type}'.");
        return warnings;
    }

    /// <summary>
    /// Forgets a folder: removes its entry from the local catalog
    /// only. Destination chunks are untouched and orphaned (opaque
    /// without the manifest). Requires the unlocked card and an
    /// explicit confirmation — without an export, the folder is
    /// unrecoverable from this machine. Jobs are never touched: they
    /// reference locations, not folder entries, so nothing dangles;
    /// the summary names the live jobs still using the location so
    /// the user knows the location (and its chunks) stay.
    /// </summary>
    public static (int Status, object Payload) ForgetOfflineFolder(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        string folderName, bool confirm, Action<string>? log = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            return (404, new ErrorDto("No such offline folder."));
        if (!confirm)
            return (400, new ErrorDto(
                "Confirmation required: without an export, forgetting is unrecoverable from this machine."));
        int files = folder.Files.Count;
        int chunks = folder.Files.Sum(f => f.ChunkIds.Count);
        var jobsUsingLocation = config.Jobs
            .Where(j => j.EffectiveTargets.Contains(folder.LocationId, StringComparer.OrdinalIgnoreCase))
            .Select(j => j.Name).ToList();
        entries.Remove(folder);
        var save = PersistOfflineFolders(session, catalogPath, entries);
        if (save is not null)
            return save.Value;
        session.SetEntries(entries);
        log?.Invoke($"dashboard: offline folder '{folder.Name}' forgotten ({files} files; destination chunks orphaned).");
        return (200, new OfflineForgetSummaryDto(folder.Name, files, chunks, jobsUsingLocation));
    }

    /// <summary>
    /// Locks the card, wiping the catalog key and the decrypted
    /// entries from memory.
    /// </summary>
    public static (int Status, object Payload) LockOfflineFolders(
        OfflineFoldersSession session)
    {
        session.Lock();
        return (200, new OkDto(true));
    }

    /// <summary>Folder list. Refused (403) while the card is locked.</summary>
    public static (int Status, object Payload) GetOfflineFolders(
        OfflineFoldersSession session)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        return (200, entries
            .Select(e => new OfflineFolderDto(e.Name, e.LocationId, e.AddedUtc.ToString("O")))
            .ToList());
    }

    /// <summary>Adds a catalog entry. Requires the unlocked session.</summary>
    public static (int Status, object Payload) AddOfflineFolder(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        string? name, string? locationId, Action<string>? log = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        string? nameError = OfflineFoldersCatalog.ValidateName(name);
        if (nameError is not null)
            return (400, new ErrorDto(nameError));
        string trimmedName = name!.Trim();
        if (string.IsNullOrWhiteSpace(locationId) ||
            !config.SnapshotLocations().Any(l =>
                string.Equals(l.Name, locationId, StringComparison.OrdinalIgnoreCase)))
            return (400, new ErrorDto("That location does not exist."));
        if (entries.Any(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            return (400, new ErrorDto("A folder with that name already exists."));
        // Store the location's canonical name (the key BackupLocation uses).
        string canonicalLocation = config.SnapshotLocations()
            .First(l => string.Equals(l.Name, locationId, StringComparison.OrdinalIgnoreCase)).Name;
        var entry = new OfflineFolderEntry(trimmedName, canonicalLocation, DateTimeOffset.UtcNow);
        entries.Add(entry);
        var save = PersistOfflineFolders(session, catalogPath, entries);
        if (save is not null)
            return save.Value;
        session.SetEntries(entries);
        log?.Invoke("dashboard: offline folder added");
        return (200, new OfflineFolderDto(entry.Name, entry.LocationId, entry.AddedUtc.ToString("O")));
    }

    /// <summary>Removes a catalog entry by name. Requires the unlocked session.</summary>
    public static (int Status, object Payload) DeleteOfflineFolder(
        OfflineFoldersSession session, string catalogPath, string name,
        Action<string>? log = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        var existing = entries.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return (404, new ErrorDto("No such offline folder."));
        entries.Remove(existing);
        var save = PersistOfflineFolders(session, catalogPath, entries);
        if (save is not null)
            return save.Value;
        session.SetEntries(entries);
        log?.Invoke("dashboard: offline folder removed");
        return (200, new OkDto(true));
    }

    /// <summary>
    /// Lists a folder's files. Refused (403) while locked; 404 when the
    /// folder is not in the catalog. File names leave only while the
    /// card is unlocked, like folder names in slice 1.
    /// </summary>
    public static (int Status, object Payload) GetOfflineFolderFiles(
        OfflineFoldersSession session, string folderName)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            return (404, new ErrorDto("No such offline folder."));
        return (200, folder.Files
            .Select(f => new OfflineFileDto(f.Path, f.Size, f.ModifiedUtc.ToString("O")))
            .ToList());
    }

    /// <summary>
    /// Stores (or replaces) a file in an offline folder: chunks +
    /// encrypts the content onto the folder's location, updates the
    /// encrypted catalog atomically, and supersedes the old index
    /// entry. The old chunks are NOT deleted from the location —
    /// content addressing means they may be shared with other files —
    /// but they are no longer reachable from the catalog, which is the
    /// reload guarantee. Requires the unlocked session. The
    /// <paramref name="content"/> stream is consumed, never buffered
    /// to disk by this method.
    /// </summary>
    public static (int Status, object Payload) StoreOfflineFile(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        string folderName, string filePath, Stream content, DateTimeOffset modifiedUtc,
        Action<string>? log = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        string? pathError = OfflineFoldersCatalog.ValidateFilePath(filePath);
        if (pathError is not null)
            return (400, new ErrorDto(pathError));
        string trimmedPath = filePath.Trim();
        int folderIndex = entries.FindIndex(e =>
            string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (folderIndex < 0)
            return (404, new ErrorDto("No such offline folder."));
        var folder = entries[folderIndex];
        var location = config.SnapshotLocations().FirstOrDefault(l =>
            string.Equals(l.Name, folder.LocationId, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return (400, new ErrorDto("That folder's location no longer exists."));
        byte[]? contentKey = session.CopyContentKey();
        if (contentKey is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        try
        {
            OfflineFileStore.OfflineLocationStore store;
            try { store = OfflineFileStore.ResolveStore(location); }
            catch (InvalidOperationException ex) { return (400, new ErrorDto(ex.Message)); }
            using (store)
            {
                var (chunkIds, totalSize) = OfflineFileStore.Store(content, store.Store, contentKey);
                var newFiles = folder.Files.ToList();
                newFiles.RemoveAll(f => string.Equals(f.Path, trimmedPath, StringComparison.OrdinalIgnoreCase));
                newFiles.Add(new OfflineFileEntry(trimmedPath, totalSize, modifiedUtc, chunkIds));
                var newEntries = entries.ToList();
                newEntries[folderIndex] = folder with { Files = newFiles };
                var save = PersistOfflineFolders(session, catalogPath, newEntries);
                if (save is not null)
                    return save.Value;
                session.SetEntries(newEntries);
                log?.Invoke("dashboard: offline file stored");
                return (200, new OfflineFileDto(trimmedPath, totalSize, modifiedUtc.ToString("O")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (400, new ErrorDto("The file could not be stored on its location."));
        }
    }

    /// <summary>
    /// Stores a batch of already-opened streams (multipart upload) with
    /// per-file outcomes. Duplicate basenames are refused upfront (the
    /// same collapse hazard as files-by-path) and a locked session is
    /// refused upfront too. Individual store failures do NOT abort the
    /// batch: unlike files-by-path, multipart streams cannot be
    /// prevalidated without consuming them, and buffering plaintext to
    /// disk to stage them is forbidden here — so each failure is
    /// reported in <see cref="OfflineBatchResultDto.Failed"/> and the
    /// rest still store. The caller transfers stream ownership: every
    /// stream is disposed here on all paths (double dispose is safe).
    /// </summary>
    public static (int Status, object Payload) StoreOfflineFilesBatch(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        string folderName, IEnumerable<(string FileName, Stream Content, DateTimeOffset Modified)> files,
        Action<string>? log = null)
    {
        var list = files.ToList();
        try
        {
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (fileName, _, _) in list)
            {
                if (!seenNames.Add(fileName))
                    return (400, new ErrorDto($"Duplicate file name in selection: {fileName}"));
            }
            if (session.GetEntries() is null)
                return (403, new ErrorDto("Offline Folders is locked."));
            var stored = new List<OfflineFileDto>();
            var failed = new List<OfflineFileFailureDto>();
            foreach (var (fileName, content, modified) in list)
            {
                using (content)
                {
                    var (status, payload) = StoreOfflineFile(config, session, catalogPath,
                        folderName, fileName, content, modified, log);
                    if (status == 200)
                        stored.Add((OfflineFileDto)payload);
                    else
                        failed.Add(new OfflineFileFailureDto(fileName,
                            payload is ErrorDto err ? err.Error : "Could not store file."));
                }
            }
            return (200, new OfflineBatchResultDto(stored, failed));
        }
        finally
        {
            foreach (var (_, content, _) in list)
            {
                try { content.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Stores files chosen through a native picker (see NativeBridge.PickFiles)
    /// by local path, instead of by browser upload. The dashboard is loopback-only
    /// and CSRF-checked, so only the local GUI user can call this, exactly as with
    /// the multipart upload — it exists because the Chromium file chooser does not
    /// reliably open from a remote/automated session, while a native WinForms dialog
    /// does. Each path is read from disk and stored through <see cref="StoreOfflineFile"/>,
    /// so basename validation, chunking, and catalog persistence are identical.
    /// Requires the unlocked session.
    /// Batch semantics (transactional at the catalog level): duplicate
    /// basenames inside one selection are refused, and every file is opened
    /// before anything is stored, so a missing or unreadable file anywhere in
    /// the batch fails the whole batch with nothing stored. If a later file
    /// then fails to store, earlier files in the batch are rolled back out
    /// of the catalog (their blobs stay on the location as unreachable
    /// orphans, as with <see cref="DeleteOfflineFile"/>); a replaced entry
    /// is restored to its pre-batch form. If the rollback persist itself
    /// fails, the failure is logged and the partial batch remains — the
    /// session is left matching disk rather than diverging from it.
    /// A name that already exists in the folder is still replaced by
    /// <see cref="StoreOfflineFile"/> on success — that is the update path,
    /// shared with the multipart upload, not a collision.
    /// Service-identity limitation: paths are opened in the service process
    /// (LocalSystem), not in the GUI user's session, so user mapped drives,
    /// EFS-encrypted files, and other per-user locations may fail here with
    /// "Could not read file" even though the picker could see them. Reading
    /// in the GUI and sending bytes would remove this limitation; until then
    /// the failure is upfront and stores nothing.
    /// </summary>
    public static (int Status, object Payload) AddOfflineFilesByPath(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        string folderName, IEnumerable<string> paths, Action<string>? log = null)
    {
        var pathList = paths.ToList();
        // Duplicate basenames in one batch would collapse to a single catalog
        // entry (StoreOfflineFile replaces by path), silently dropping one
        // file. Refuse the batch before storing anything.
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in pathList)
        {
            if (string.IsNullOrWhiteSpace(path))
                return (400, new ErrorDto($"File not found: {path}"));
            string fileName = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(fileName) && !seenNames.Add(fileName))
                return (400, new ErrorDto($"Duplicate file name in selection: {fileName}"));
        }
        // Locked session: fail before opening anything.
        if (session.GetEntries() is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        // Open every file first, so a missing or unreadable file fails the
        // whole batch with nothing stored. Streams are held open (handles,
        // not memory — content still streams through chunking per file).
        var opened = new List<(string FileName, FileStream Stream, DateTimeOffset Modified)>();
        // Catalog snapshot for rollback: each successful store persists the
        // catalog, so a failure on a later file must undo the earlier ones.
        // StoreOfflineFile builds new entry lists (never mutates in place),
        // so this copy stays pre-batch. The rollback restores the catalog
        // only — blobs written before the failure stay on the location as
        // unreachable orphans, and shared chunks must never be deleted.
        List<OfflineFolderEntry>? snapshot = null;
        void Rollback()
        {
            if (snapshot is null)
                return;
            var save = PersistOfflineFolders(session, catalogPath, snapshot);
            if (save is not null)
            {
                // Disk still holds the partial batch; leave the session
                // matching disk rather than diverging from it.
                log?.Invoke("dashboard: offline by-path rollback failed to persist; earlier files in the batch remain stored");
                return;
            }
            session.SetEntries(snapshot);
            log?.Invoke("dashboard: offline by-path batch rolled back");
        }
        try
        {
            foreach (string path in pathList)
            {
                if (!File.Exists(path))
                    return (400, new ErrorDto($"File not found: {path}"));
                FileStream stream;
                try { stream = File.OpenRead(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (400, new ErrorDto($"Could not read file: {Path.GetFileName(path)}"));
                }
                opened.Add((Path.GetFileName(path), stream, File.GetLastWriteTimeUtc(path)));
            }
            snapshot = session.GetEntries()?.ToList();
            var stored = new List<OfflineFileDto>();
            try
            {
                foreach (var (fileName, stream, modified) in opened)
                {
                    using (stream)
                    {
                        var (status, payload) = StoreOfflineFile(config, session, catalogPath,
                            folderName, fileName, stream, modified, log);
                        if (status != 200)
                        {
                            Rollback();
                            return (status, payload);
                        }
                        stored.Add((OfflineFileDto)payload);
                    }
                }
            }
            catch
            {
                Rollback();
                throw;
            }
            return (200, stored);
        }
        finally
        {
            // Disposing twice is a no-op for FileStream; this covers streams
            // not yet consumed when an early return or a mid-batch failure
            // exits the loop above.
            foreach (var (_, stream, _) in opened)
            {
                try { stream.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Removes a file's index entry from its folder and persists the
    /// catalog. The chunks stay on the location as orphans (see
    /// <see cref="StoreOfflineFile"/>); they are no longer reachable
    /// from the catalog. Requires the unlocked session.
    /// </summary>
    public static (int Status, object Payload) DeleteOfflineFile(
        OfflineFoldersSession session, string catalogPath, string folderName, string filePath,
        Action<string>? log = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        int folderIndex = entries.FindIndex(e =>
            string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (folderIndex < 0)
            return (404, new ErrorDto("No such offline folder."));
        var folder = entries[folderIndex];
        var existing = folder.Files.FirstOrDefault(f =>
            string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return (404, new ErrorDto("No such offline file."));
        var newFiles = folder.Files.ToList();
        newFiles.Remove(existing);
        var newEntries = entries.ToList();
        newEntries[folderIndex] = folder with { Files = newFiles };
        var save = PersistOfflineFolders(session, catalogPath, newEntries);
        if (save is not null)
            return save.Value;
        session.SetEntries(newEntries);
        log?.Invoke("dashboard: offline file removed");
        return (200, new OkDto(true));
    }

    /// <summary>
    /// Streams one file's decrypted bytes into <paramref name="output"/>
    /// (chunk by chunk; plaintext buffers are zeroed after each write).
    /// Returns a status code and, on failure, an error message that
    /// never echoes a folder or file name. The caller owns
    /// <paramref name="output"/>; no plaintext is retained afterwards.
    /// When <paramref name="stillUnlocked"/> returns false mid-stream
    /// (card locked during transfer), streaming aborts with
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static (int Status, string? Error) StreamOfflineFile(
        ServiceConfig config, OfflineFoldersSession session,
        string folderName, string filePath, Stream output,
        Func<bool>? stillUnlocked = null)
    {
        var entries = session.GetEntries();
        if (entries is null)
            return (403, "Offline Folders is locked.");
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            return (404, "No such offline folder.");
        var file = folder.Files.FirstOrDefault(f =>
            string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
        if (file is null)
            return (404, "No such offline file.");
        var location = config.SnapshotLocations().FirstOrDefault(l =>
            string.Equals(l.Name, folder.LocationId, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return (400, "That folder's location no longer exists.");
        byte[]? contentKey = session.CopyContentKey();
        if (contentKey is null)
            return (403, "Offline Folders is locked.");
        try
        {
            OfflineFileStore.OfflineLocationStore store;
            try { store = OfflineFileStore.ResolveStore(location); }
            catch (InvalidOperationException ex) { return (400, ex.Message); }
            using (store)
            {
                OfflineFileStore.Retrieve(file.ChunkIds, store.Store, contentKey, output,
                    stillUnlocked ?? (() => session.IsUnlocked));
            }
            return (200, null);
        }
        catch (InvalidDataException)
        {
            return (400, "The file's stored content is incomplete on its location.");
        }
        catch (CryptographicException)
        {
            return (400, "The file's stored content failed verification.");
        }
        finally
        {
            if (contentKey is not null)
                CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    private static (int Status, object Payload)? PersistOfflineFolders(
        OfflineFoldersSession session, string catalogPath, List<OfflineFolderEntry> entries)
    {
        byte[]? key = session.CopyKey();
        if (key is null)
            return (403, new ErrorDto("Offline Folders is locked."));
        try
        {
            OfflineFoldersCatalog.Save(catalogPath, key, entries);
            return null;
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Resolves the vault root secret from a fresh proof, or null when
    /// no valid proof was supplied. Knowledge proof: the candidate is
    /// verified against the currently configured secret with the same
    /// constant-time check the security-config gate uses
    /// (<see cref="VerifyCurrentSecretProof"/>), and the normalized
    /// form (generated-secret mode) is what the catalog key derives
    /// from. Possession proof: a fresh touch unwraps the stored blob,
    /// exactly like the restore unlock. No phone/TOTP path exists here
    /// by design, and session state is never consulted.
    /// </summary>
    private static string? ResolveCardUnlockSecret(
        ServiceConfig config, string? proofPassphrase, bool proofYubiKey,
        IYubiKeyProvider? yubiKeyProvider)
    {
        if (!string.IsNullOrEmpty(proofPassphrase) &&
            VerifyCurrentSecretProof(config, proofPassphrase))
            return config.GeneratedSecretMode
                ? GeneratedSecret.Normalize(proofPassphrase)
                : proofPassphrase;

        // Added passphrase (generated mode): the typed candidate
        // wraps the root secret rather than being it, so unwrap it
        // (the restore unlock already accepts the wrap; the card
        // unlock was the missing twin). A wrong added passphrase
        // and a missing wrap are the same answer here: keep trying.
        if (!string.IsNullOrEmpty(proofPassphrase) &&
            TryUnwrapPassphraseWrap(config, proofPassphrase, out string? unwrapped) &&
            !string.IsNullOrEmpty(unwrapped))
            return unwrapped;

        if (proofYubiKey && yubiKeyProvider is not null &&
            (config.PassphraseYubiKey || config.GeneratedSecretMode) &&
            !string.IsNullOrEmpty(config.YubiKeyChallenge) &&
            !string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped))
        {
            try
            {
                return YubiKeySetup.Unlock(yubiKeyProvider,
                    config.YubiKeyChallenge, config.PassphraseYubiKeyWrapped,
                    config.YubiKeySlot);
            }
            catch
            {
                // No device, no touch in time, or the blob did not
                // unwrap: not a proof.
                return null;
            }
        }

        return null;
    }
}
