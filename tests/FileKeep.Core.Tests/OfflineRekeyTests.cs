using System.Security.Cryptography;
using System.Text;
using FileKeep.Core;
using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Offline rekey, rewrite half: re-encrypting the catalog blob and
/// every file chunk in place, from an old secret to a new one. The
/// tests pin the three properties the design rests on: chunk IDs
/// never change (plaintext hashes), re-runs are idempotent (new-key
/// chunks are skipped), and a partial run never saves the catalog
/// under the new key (mixed state is impossible — fix and re-run).
/// The store is a plain LocalBlobStore; SMB shares the same
/// Delete + Put shape, and Usenet never holds offline chunks.
/// </summary>
public sealed class OfflineRekeyTests : IDisposable
{
    private readonly string _workDir;

    private const string OldSecret = "the-old-passphrase";
    private const string NewSecret = "the-new-passphrase";
    private const string Content = "hello offline rekey — some bytes to chunk and re-encrypt";

    public OfflineRekeyTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-rekey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private (LocalBlobStore Store, string CatalogPath, List<string> ChunkIds) SetupCatalog(string secret)
    {
        string storeRoot = Path.Combine(_workDir, "store-" + Guid.NewGuid().ToString("N"));
        var store = new LocalBlobStore(storeRoot);
        byte[] contentKey = OfflineFileStore.DeriveContentKey(secret);
        List<string> chunkIds;
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(Content)))
            chunkIds = OfflineFileStore.Store(input, store, contentKey).ChunkIds;
        var entries = new List<OfflineFolderEntry>
        {
            new("F", "L", DateTimeOffset.UtcNow)
            {
                Files = new() { new("a.txt", Content.Length, DateTimeOffset.UtcNow, chunkIds) }
            }
        };
        string catalogPath = Path.Combine(_workDir, "offline-folders-" + Guid.NewGuid().ToString("N") + ".json");
        OfflineFoldersCatalog.Save(catalogPath, OfflineFoldersCatalog.DeriveKey(secret), entries);
        return (store, catalogPath, chunkIds);
    }

    private static OfflineFileStore.OfflineLocationStore Wrap(LocalBlobStore store) =>
        new(store);

    [Fact]
    public void Rewrite_RotatesCatalogAndChunks()
    {
        var (store, catalogPath, chunkIds) = SetupCatalog(OldSecret);

        var summary = OfflineRekey.Rewrite(catalogPath, OldSecret, NewSecret, _ => Wrap(store));

        Assert.Empty(summary.Failures);
        Assert.True(summary.CatalogSaved);
        Assert.Equal(1, summary.Folders);
        Assert.Equal(1, summary.Files);
        Assert.Equal(chunkIds.Count, summary.Chunks);
        Assert.Equal(chunkIds.Count, summary.Rewritten);
        Assert.Equal(0, summary.Skipped);

        // Catalog entries are identical, now under the new key only.
        var entries = OfflineFoldersCatalog.Load(catalogPath, OfflineFoldersCatalog.DeriveKey(NewSecret));
        Assert.Single(entries);
        Assert.Equal("F", entries[0].Name);
        Assert.Equal(chunkIds, entries[0].Files[0].ChunkIds);
        Assert.ThrowsAny<CryptographicException>(() =>
            OfflineFoldersCatalog.Load(catalogPath, OfflineFoldersCatalog.DeriveKey(OldSecret)));

        // Chunks decrypt under the new content key, not the old.
        byte[] newContent = OfflineFileStore.DeriveContentKey(NewSecret);
        byte[] oldContent = OfflineFileStore.DeriveContentKey(OldSecret);
        foreach (string chunkId in chunkIds)
        {
            byte[] blob = store.Get(chunkId);
            byte[] id32 = Hashing.HexToBytes(chunkId);
            _ = ChunkCrypto.Decrypt(blob, newContent, id32);
            Assert.ThrowsAny<CryptographicException>(() => ChunkCrypto.Decrypt(blob, oldContent, id32));
        }
    }

    [Fact]
    public void Rewrite_SkipsAlreadyRewrittenChunks()
    {
        // Two files, one chunk each. Pre-rewrite the first chunk by
        // hand — the state an interrupted run leaves behind — then
        // Rewrite must skip it, rewrite the second, and save.
        string storeRoot = Path.Combine(_workDir, "store-" + Guid.NewGuid().ToString("N"));
        var store = new LocalBlobStore(storeRoot);
        byte[] oldContent = OfflineFileStore.DeriveContentKey(OldSecret);
        byte[] newContent = OfflineFileStore.DeriveContentKey(NewSecret);
        List<string> idsA, idsB;
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes("first file bytes")))
            idsA = OfflineFileStore.Store(input, store, oldContent).ChunkIds;
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes("second file bytes")))
            idsB = OfflineFileStore.Store(input, store, oldContent).ChunkIds;
        var entries = new List<OfflineFolderEntry>
        {
            new("F", "L", DateTimeOffset.UtcNow)
            {
                Files = new()
                {
                    new("a.txt", 16, DateTimeOffset.UtcNow, idsA),
                    new("b.txt", 17, DateTimeOffset.UtcNow, idsB),
                }
            }
        };
        string catalogPath = Path.Combine(_workDir, "offline-folders-" + Guid.NewGuid().ToString("N") + ".json");
        OfflineFoldersCatalog.Save(catalogPath, OfflineFoldersCatalog.DeriveKey(OldSecret), entries);

        // Simulate the interrupted run: chunk A already new-key.
        byte[] blobA = store.Get(idsA[0]);
        byte[] plainA = ChunkCrypto.Decrypt(blobA, oldContent, Hashing.HexToBytes(idsA[0]));
        byte[] newBlobA = ChunkCrypto.Encrypt(plainA, newContent, Hashing.HexToBytes(idsA[0]));
        store.Delete(idsA[0]);
        store.Put(idsA[0], newBlobA);

        var summary = OfflineRekey.Rewrite(catalogPath, OldSecret, NewSecret, _ => Wrap(store));

        Assert.Empty(summary.Failures);
        Assert.True(summary.CatalogSaved);
        Assert.Equal(2, summary.Chunks);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.Rewritten);
        // Both chunks now decrypt under the new key.
        foreach (string chunkId in idsA.Concat(idsB))
        {
            byte[] blob = store.Get(chunkId);
            _ = ChunkCrypto.Decrypt(blob, newContent, Hashing.HexToBytes(chunkId));
        }
    }

    [Fact]
    public void Rewrite_LeavesCatalogUnchangedOnFailure()
    {
        var (store, catalogPath, chunkIds) = SetupCatalog(OldSecret);
        store.Delete(chunkIds[0]);

        var summary = OfflineRekey.Rewrite(catalogPath, OldSecret, NewSecret, _ => Wrap(store));

        Assert.NotEmpty(summary.Failures);
        Assert.False(summary.CatalogSaved);
        Assert.Equal(0, summary.Rewritten);
        // The old-key catalog is still intact — fix and re-run.
        var entries = OfflineFoldersCatalog.Load(catalogPath, OfflineFoldersCatalog.DeriveKey(OldSecret));
        Assert.Single(entries);
        Assert.ThrowsAny<CryptographicException>(() =>
            OfflineFoldersCatalog.Load(catalogPath, OfflineFoldersCatalog.DeriveKey(NewSecret)));
    }

    [Fact]
    public void Rewrite_WrongOldSecret_ThrowsBeforeTouchingAnything()
    {
        var (store, catalogPath, _) = SetupCatalog(OldSecret);

        Assert.ThrowsAny<CryptographicException>(() =>
            OfflineRekey.Rewrite(catalogPath, "not-the-old-secret", NewSecret, _ => Wrap(store)));
        // Nothing changed: the catalog still opens under the old key.
        Assert.Single(OfflineFoldersCatalog.Load(catalogPath, OfflineFoldersCatalog.DeriveKey(OldSecret)));
    }

    [Fact]
    public void Handler_RefusesWhileUnlocked()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, OldSecret);
        try
        {
            var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
            var session = new OfflineFoldersSession();
            session.Unlock(
                OfflineFoldersCatalog.DeriveKey(OldSecret),
                OfflineFileStore.DeriveContentKey(OldSecret),
                new List<OfflineFolderEntry>());
            Assert.True(session.IsUnlocked);

            var (status, payload) = DashboardApi.RekeyOfflineFolders(
                config, Path.Combine(_workDir, "service.json"), session,
                oldSecret: null, newSecret: NewSecret, yubiKeyProvider: null,
                proofPassphrase: OldSecret);
            Assert.Equal(400, status);
            Assert.Contains("Lock Offline Folders first", ErrorOf(payload));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        }
    }

    [Fact]
    public void Handler_RefusesInGeneratedMode()
    {
        // Proof in generated mode is the sheet secret itself
        // (canonical form — the gate normalizes the candidate, so a
        // lowercase typed passphrase would NOT prove here).
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        try
        {
            var config = new ServiceConfig
            {
                GeneratedSecretMode = true,
                YubiKeyChallenge = "challenge",
                PassphraseYubiKeyWrapped = "wrapped",
            };
            var session = new OfflineFoldersSession();

            var (status, payload) = DashboardApi.RekeyOfflineFolders(
                config, Path.Combine(_workDir, "service.json"), session,
                oldSecret: null, newSecret: NewSecret, yubiKeyProvider: null,
                proofPassphrase: secret);
            Assert.Equal(400, status);
            Assert.Contains("passphrase-mode operation", ErrorOf(payload));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        }
    }

    private static string ErrorOf(object payload)
    {
        var prop = payload.GetType().GetProperty("Error")
            ?? payload.GetType().GetProperty("error");
        Assert.NotNull(prop);
        return (string?)prop.GetValue(payload) ?? "";
    }
}
