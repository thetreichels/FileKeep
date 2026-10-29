using System.Security.Cryptography;
using System.Text;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// The SetPassphrase auto-rekey hook (fail-closed): replacing the
/// passphrase in passphrase mode rewrites the offline catalog from
/// the currently configured secret to the new passphrase BEFORE the
/// config is touched. A catalog that cannot be rekeyed aborts the
/// passphrase change with the config untouched.
///
/// The hook itself is exercised directly (it never touches DPAPI);
/// the SetPassphrase wiring is covered by refusal tests plus one
/// ordering test. A successful plain-branch SetPassphrase calls
/// Dpapi.Protect, which is Windows-only, so the ordering test is
/// platform-branched: on Linux it proves the hook ran before the
/// protect step threw; on Windows it proves the full 200 path.
/// </summary>
public sealed class OfflinePassphraseRekeyHookTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;
    private readonly List<string> _log = new();

    private const string OldSecret = "the-old-passphrase";
    private const string NewSecret = "the-new-passphrase";
    private const string Content = "hook test bytes — chunk and re-encrypt me";

    public OfflinePassphraseRekeyHookTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, OldSecret);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string ConfigPath() => Path.Combine(_workDir, "service-" + Guid.NewGuid().ToString("N") + ".json");
    private string CatalogPath() => Path.Combine(_workDir, "offline-" + Guid.NewGuid().ToString("N") + ".json");

    private static ServiceConfig PassphraseConfig() => new() { PassphraseProtected = "dpapi-blob" };

    private (LocalBlobStore Store, List<string> ChunkIds) SetupCatalog(string catalogPath, string secret)
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
                Files = new() { new("a.txt", Content.Length, DateTimeOffset.UtcNow, chunkIds) },
            },
        };
        OfflineFoldersCatalog.Save(catalogPath, OfflineFoldersCatalog.DeriveKey(secret), entries);
        return (store, chunkIds);
    }

    private static Func<string, OfflineFileStore.OfflineLocationStore> Resolver(LocalBlobStore store) =>
        _ => new OfflineFileStore.OfflineLocationStore(store);

    private (int Status, object Payload)? Hook(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        string newPassphrase, Func<string, OfflineFileStore.OfflineLocationStore> resolve) =>
        DashboardApi.RekeyOfflineForPassphraseChange(config, session, catalogPath, newPassphrase, resolve, _log.Add);

    private static string ErrorOf(object payload)
    {
        var prop = payload.GetType().GetProperty("Error") ?? payload.GetType().GetProperty("error");
        Assert.NotNull(prop);
        return (string?)prop.GetValue(payload) ?? "";
    }

    private static void AssertChunkDecrypts(LocalBlobStore store, string chunkId, string secret)
    {
        byte[] blob = store.Get(chunkId);
        byte[] plain = ChunkCrypto.Decrypt(blob, OfflineFileStore.DeriveContentKey(secret), Hashing.HexToBytes(chunkId));
        Assert.Equal(Content, Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public void Hook_NoCatalogFile_ReturnsNull()
    {
        var config = PassphraseConfig();
        var result = Hook(config, new OfflineFoldersSession(), CatalogPath(), NewSecret,
            Resolver(new LocalBlobStore(Path.Combine(_workDir, "unused"))));
        Assert.Null(result);
    }

    [Fact]
    public void Hook_RekeysCatalog()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string catalog = CatalogPath();
        var (store, chunkIds) = SetupCatalog(catalog, OldSecret);

        var result = Hook(config, session, catalog, NewSecret, Resolver(store));

        Assert.Null(result);
        Assert.Contains(_log, m => m.Contains("auto-rekeyed"));
        var entries = OfflineFoldersCatalog.Load(catalog, OfflineFoldersCatalog.DeriveKey(NewSecret));
        Assert.Single(entries);
        Assert.Equal(chunkIds, entries[0].Files[0].ChunkIds);
        Assert.ThrowsAny<CryptographicException>(() =>
            OfflineFoldersCatalog.Load(catalog, OfflineFoldersCatalog.DeriveKey(OldSecret)));
        AssertChunkDecrypts(store, chunkIds[0], NewSecret);
    }

    [Fact]
    public void Hook_AlreadyUnderNewSecret_ReturnsNull()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string catalog = CatalogPath();
        // A manual rekey ran first: the catalog is already under the
        // new secret while the config still holds the old one.
        var (store, _) = SetupCatalog(catalog, NewSecret);

        var result = Hook(config, session, catalog, NewSecret, Resolver(store));

        Assert.Null(result);
        Assert.DoesNotContain(_log, m => m.Contains("auto-rekeyed"));
    }

    [Fact]
    public void Hook_BlockedWhenUnlocked()
    {
        var config = PassphraseConfig();
        string catalog = CatalogPath();
        var (store, _) = SetupCatalog(catalog, OldSecret);
        var session = new OfflineFoldersSession();
        session.Unlock(OfflineFoldersCatalog.DeriveKey(OldSecret),
            OfflineFileStore.DeriveContentKey(OldSecret), new List<OfflineFolderEntry>());

        var result = Hook(config, session, catalog, NewSecret, Resolver(store));

        Assert.NotNull(result);
        Assert.Equal(400, result.Value.Status);
        Assert.Contains("Lock Offline Folders first", ErrorOf(result.Value.Payload));
        Assert.Single(OfflineFoldersCatalog.Load(catalog, OfflineFoldersCatalog.DeriveKey(OldSecret)));
    }

    [Fact]
    public void Hook_UndecryptableCatalog_ReturnsError()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string catalog = CatalogPath();
        SetupCatalog(catalog, "some-other-secret");

        var result = Hook(config, session, catalog, NewSecret,
            Resolver(new LocalBlobStore(Path.Combine(_workDir, "unused"))));

        Assert.NotNull(result);
        Assert.Equal(400, result.Value.Status);
        Assert.Contains("could not be decrypted", ErrorOf(result.Value.Payload));
    }

    [Fact]
    public void Hook_NoResolvableSecret_ReturnsError()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        try
        {
            var config = PassphraseConfig();
            var session = new OfflineFoldersSession();
            string catalog = CatalogPath();
            SetupCatalog(catalog, "some-other-secret");

            var result = Hook(config, session, catalog, NewSecret,
                Resolver(new LocalBlobStore(Path.Combine(_workDir, "unused"))));

            Assert.NotNull(result);
            Assert.Equal(400, result.Value.Status);
            Assert.Contains("could not be resolved", ErrorOf(result.Value.Payload));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, OldSecret);
        }
    }

    [Fact]
    public void Hook_RewriteFailure_ReturnsError()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string catalog = CatalogPath();
        var (store, chunkIds) = SetupCatalog(catalog, OldSecret);

        // The folder's location is gone: every chunk lands in
        // Failures, the catalog stays under the old secret.
        Func<string, OfflineFileStore.OfflineLocationStore> gone =
            _ => throw new InvalidOperationException("Location 'L' no longer exists.");
        var result = Hook(config, session, catalog, NewSecret, gone);

        Assert.NotNull(result);
        Assert.Equal(400, result.Value.Status);
        Assert.Contains("NOT changed", ErrorOf(result.Value.Payload));
        Assert.Single(OfflineFoldersCatalog.Load(catalog, OfflineFoldersCatalog.DeriveKey(OldSecret)));
        AssertChunkDecrypts(store, chunkIds[0], OldSecret);
    }

    [Fact]
    public void SetPassphrase_HookRefusal_AbortsChange()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string catalog = CatalogPath();
        SetupCatalog(catalog, "some-other-secret");

        var (status, payload) = DashboardApi.SetPassphrase(
            config, ConfigPath(), NewSecret, useYubiKey: false, null, _log.Add,
            proofPassphrase: OldSecret,
            offlineSession: session, offlineCatalogPath: catalog,
            offlineStoreResolver: Resolver(new LocalBlobStore(Path.Combine(_workDir, "unused"))));

        Assert.Equal(400, status);
        Assert.Contains("NOT changed", ErrorOf(payload));
        Assert.Equal("dpapi-blob", config.PassphraseProtected);
    }

    [Fact]
    public void SetPassphrase_HookUnlocked_AbortsChange()
    {
        var config = PassphraseConfig();
        string catalog = CatalogPath();
        var (store, _) = SetupCatalog(catalog, OldSecret);
        var session = new OfflineFoldersSession();
        session.Unlock(OfflineFoldersCatalog.DeriveKey(OldSecret),
            OfflineFileStore.DeriveContentKey(OldSecret), new List<OfflineFolderEntry>());

        var (status, payload) = DashboardApi.SetPassphrase(
            config, ConfigPath(), NewSecret, useYubiKey: false, null, _log.Add,
            proofPassphrase: OldSecret,
            offlineSession: session, offlineCatalogPath: catalog,
            offlineStoreResolver: Resolver(store));

        Assert.Equal(400, status);
        Assert.Contains("Lock Offline Folders first", ErrorOf(payload));
        Assert.Equal("dpapi-blob", config.PassphraseProtected);
    }

    [Fact]
    public void SetPassphrase_HookRunsBeforeConfigMutation()
    {
        // The critical ordering: the hook rewrites the catalog BEFORE
        // the config is mutated. Dpapi.Protect is Windows-only, so on
        // Linux the test proves the hook ran (catalog rekeyed) before
        // the protect step threw; on Windows it proves the full 200.
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string catalog = CatalogPath();
        var (store, chunkIds) = SetupCatalog(catalog, OldSecret);

        if (OperatingSystem.IsWindows())
        {
            var (status, payload) = DashboardApi.SetPassphrase(
                config, ConfigPath(), NewSecret, useYubiKey: false, null, _log.Add,
                proofPassphrase: OldSecret,
                offlineSession: session, offlineCatalogPath: catalog,
                offlineStoreResolver: Resolver(store));
            Assert.Equal(200, status);
            Assert.Null(Assert.IsType<OkYubiKeyDto>(payload).Warning);
            Assert.NotEqual("dpapi-blob", config.PassphraseProtected);
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(() => DashboardApi.SetPassphrase(
                config, ConfigPath(), NewSecret, useYubiKey: false, null, _log.Add,
                proofPassphrase: OldSecret,
                offlineSession: session, offlineCatalogPath: catalog,
                offlineStoreResolver: Resolver(store)));
            // The throw came from Dpapi.Protect — after the hook, and
            // the config was never saved.
            Assert.Equal("dpapi-blob", config.PassphraseProtected);
        }

        // Either way, the hook ran first: the catalog is under the new
        // secret and its chunks re-encrypted.
        Assert.Contains(_log, m => m.Contains("auto-rekeyed"));
        Assert.Single(OfflineFoldersCatalog.Load(catalog, OfflineFoldersCatalog.DeriveKey(NewSecret)));
        AssertChunkDecrypts(store, chunkIds[0], NewSecret);
    }

    [Fact]
    public void Clear_WarnsWhenCatalogExists()
    {
        var config = PassphraseConfig();
        string catalog = CatalogPath();
        SetupCatalog(catalog, OldSecret);

        var (status, payload) = DashboardApi.SetPassphrase(
            config, ConfigPath(), "", useYubiKey: false, null, _log.Add,
            proofPassphrase: OldSecret,
            offlineSession: null, offlineCatalogPath: catalog);

        Assert.Equal(200, status);
        var ok = Assert.IsType<OkYubiKeyDto>(payload);
        Assert.NotNull(ok.Warning);
        Assert.Contains("stays under the old secret", ok.Warning);
        Assert.Null(config.PassphraseProtected);
    }

    [Fact]
    public void Clear_NoCatalog_NoWarning()
    {
        var config = PassphraseConfig();

        var (status, payload) = DashboardApi.SetPassphrase(
            config, ConfigPath(), "", useYubiKey: false, null, _log.Add,
            proofPassphrase: OldSecret,
            offlineSession: new OfflineFoldersSession(), offlineCatalogPath: CatalogPath());

        Assert.Equal(200, status);
        Assert.Null(Assert.IsType<OkYubiKeyDto>(payload).Warning);
    }
}
