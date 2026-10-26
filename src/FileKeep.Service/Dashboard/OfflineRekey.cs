using System.Security.Cryptography;
using FileKeep.Core;

namespace FileKeep.Service;

/// <summary>
/// Offline Folders rekey — the rewrite half: re-encrypt the catalog
/// blob and every file chunk in place, from an old vault secret to a
/// new one. Offline entries carry no source path, so re-encryption
/// is the path that preserves restores when the source files cannot
/// simply be re-added; the migration half (new vault, back up from
/// source) belongs to banked backups, whose jobs know their sources.
///
/// Two properties make this safe without a journal:
///
/// 1. Chunk IDs are hashes of the PLAINTEXT, so they never change:
///    the catalog entries are identical before and after, only the
///    ciphertext (and the catalog blob itself) changes.
/// 2. The rewrite is idempotent: a chunk that already decrypts under
///    the new key is skipped, so an interrupted run resumes cleanly
///    by re-running with the same old and new secrets.
///
/// And one commit-point rule keeps mixed state impossible: the
/// catalog is saved under the new key ONLY when every chunk
/// succeeded. Any failure leaves the old-key catalog in place (with
/// some chunks already new-key, which a re-run skips), so the user
/// fixes the failures and re-runs instead of landing in a catalog
/// that claims new-key while some files are still old-key.
///
/// Stores rewrite via Delete + Put because Put alone is a
/// deduplicating no-op over an existing chunk ID. If a Put fails
/// after its Delete, the old blob is restored on a best-effort
/// basis and the chunk is recorded as a failure, never silently
/// dropped.
/// </summary>
public static class OfflineRekey
{
    /// <summary>
    /// Rewrites the catalog at <paramref name="catalogPath"/> and all
    /// of its chunks from <paramref name="oldSecret"/> to
    /// <paramref name="newSecret"/>. <paramref name="resolveStore"/>
    /// maps a folder's location name to its store (USB/SMB only — the
    /// resolver throws for anything else, which lands in
    /// <see cref="OfflineRekeySummaryDto.Failures"/>). Throws
    /// <see cref="CryptographicException"/> when the old secret does
    /// not decrypt the catalog; nothing has been touched then.
    /// </summary>
    public static OfflineRekeySummaryDto Rewrite(
        string catalogPath, string oldSecret, string newSecret,
        Func<string, OfflineFileStore.OfflineLocationStore> resolveStore,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldSecret);
        ArgumentException.ThrowIfNullOrEmpty(newSecret);
        byte[] oldKey = OfflineFoldersCatalog.DeriveKey(oldSecret);
        byte[] newKey = OfflineFoldersCatalog.DeriveKey(newSecret);
        byte[] oldContent = OfflineFileStore.DeriveContentKey(oldSecret);
        byte[] newContent = OfflineFileStore.DeriveContentKey(newSecret);
        try
        {
            List<OfflineFolderEntry> entries = OfflineFoldersCatalog.Load(catalogPath, oldKey);
            int files = 0, chunks = 0, rewritten = 0, skipped = 0;
            var failures = new List<string>();
            foreach (var folder in entries)
            {
                files += folder.Files.Count;
                foreach (var file in folder.Files)
                    chunks += file.ChunkIds.Count;
            }

            foreach (var folder in entries)
            {
                OfflineFileStore.OfflineLocationStore locStore;
                try { locStore = resolveStore(folder.LocationId); }
                catch (Exception ex)
                {
                    failures.Add($"{folder.Name}: location '{folder.LocationId}' unavailable ({ex.Message}) — its chunks were not attempted.");
                    continue;
                }
                using (locStore)
                {
                    IBlobStore store = locStore.Store;
                    foreach (var file in folder.Files)
                    {
                        foreach (string chunkId in file.ChunkIds)
                        {
                            byte[] blob;
                            try { blob = store.Get(chunkId); }
                            catch (Exception ex)
                            {
                                failures.Add($"{folder.Name}/{file.Path}: chunk {chunkId} could not be read ({ex.Message}).");
                                continue;
                            }
                            try
                            {
                                if (TryDecrypt(blob, newContent, chunkId, out byte[]? trial) && trial is not null)
                                {
                                    CryptographicOperations.ZeroMemory(trial);
                                    skipped++;
                                    continue;
                                }
                                if (!TryDecrypt(blob, oldContent, chunkId, out byte[]? plaintext) || plaintext is null)
                                {
                                    failures.Add($"{folder.Name}/{file.Path}: chunk {chunkId} decrypts under neither key — left untouched.");
                                    continue;
                                }
                                try
                                {
                                    byte[] id32 = Hashing.HexToBytes(chunkId);
                                    try
                                    {
                                        byte[] newBlob = ChunkCrypto.Encrypt(plaintext, newContent, id32);
                                        try
                                        {
                                            if (!TryDecrypt(newBlob, newContent, chunkId, out byte[]? check) || check is null)
                                            {
                                                failures.Add($"{folder.Name}/{file.Path}: chunk {chunkId} failed new-key verification — left untouched.");
                                                continue;
                                            }
                                            CryptographicOperations.ZeroMemory(check);
                                            try
                                            {
                                                store.Delete(chunkId);
                                                try { store.Put(chunkId, newBlob); }
                                                catch (Exception ex)
                                                {
                                                    try { store.Put(chunkId, blob); } catch { /* best effort restore */ }
                                                    failures.Add($"{folder.Name}/{file.Path}: chunk {chunkId} could not be rewritten ({ex.Message}) — old blob restored.");
                                                    continue;
                                                }
                                                byte[] stored = store.Get(chunkId);
                                                try
                                                {
                                                    if (!TryDecrypt(stored, newContent, chunkId, out byte[]? storedCheck) || storedCheck is null)
                                                    {
                                                        try { store.Delete(chunkId); store.Put(chunkId, blob); } catch { /* best effort restore */ }
                                                        failures.Add($"{folder.Name}/{file.Path}: chunk {chunkId} failed stored verification — old blob restored.");
                                                        continue;
                                                    }
                                                    CryptographicOperations.ZeroMemory(storedCheck);
                                                }
                                                finally { CryptographicOperations.ZeroMemory(stored); }
                                                rewritten++;
                                            }
                                            catch (Exception ex)
                                            {
                                                failures.Add($"{folder.Name}/{file.Path}: chunk {chunkId} could not be rewritten ({ex.Message}).");
                                                continue;
                                            }
                                        }
                                        finally { CryptographicOperations.ZeroMemory(newBlob); }
                                    }
                                    finally { CryptographicOperations.ZeroMemory(id32); }
                                }
                                finally { CryptographicOperations.ZeroMemory(plaintext); }
                            }
                            finally { CryptographicOperations.ZeroMemory(blob); }
                        }
                    }
                }
            }

            bool saved = false;
            if (failures.Count == 0)
            {
                OfflineFoldersCatalog.Save(catalogPath, newKey, entries);
                saved = true;
                log?.Invoke($"dashboard: offline folders rekeyed — {rewritten} chunks rewritten, {skipped} already new-key.");
            }
            else
            {
                log?.Invoke($"dashboard: offline folders rekey incomplete — {failures.Count} failures, catalog left under the old secret.");
            }
            return new OfflineRekeySummaryDto(entries.Count, files, chunks, rewritten, skipped, failures, saved);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldKey);
            CryptographicOperations.ZeroMemory(newKey);
            CryptographicOperations.ZeroMemory(oldContent);
            CryptographicOperations.ZeroMemory(newContent);
        }
    }

    /// <summary>
    /// Trial decryption: true with the plaintext when the blob
    /// verifies under <paramref name="key"/> (the caller owns and
    /// zeroes the returned buffer); false when it does not, without
    /// distinguishing a wrong key from a malformed blob.
    /// </summary>
    private static bool TryDecrypt(byte[] blob, byte[] key, string chunkId, out byte[]? plaintext)
    {
        plaintext = null;
        try
        {
            plaintext = ChunkCrypto.Decrypt(blob, key, Hashing.HexToBytes(chunkId));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            plaintext = null;
            return false;
        }
    }
}
