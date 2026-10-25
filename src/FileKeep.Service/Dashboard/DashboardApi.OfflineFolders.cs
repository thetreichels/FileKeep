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
/// passphrase, the typed recovery secret in generated-secret mode, or
/// a YubiKey touch. A TOTP/phone code is NEVER a proof here — the
/// gate has no phone path at all — and an unlocked restore session is
/// NOT a proof either: this session is separate, and unlocking one
/// never unlocks the other. Adding/removing entries requires the
/// unlocked card session, not a fresh proof per call: the session
/// itself IS the fresh proof, bounded by the 1-hour idle expiry, the
/// same way the restore session gates restore endpoints.
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
