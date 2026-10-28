using System.Security.Cryptography;
using System.Text;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Key-derived offline chunk IDs (2026-10-27 Save As fix). Store used
/// to derive each chunk ID as SHA-256 of the plaintext chunk, and
/// blob stores skip writing a blob that already exists — so two
/// profiles sharing a destination collided on identical content: the
/// second profile's catalog pointed at the first profile's blob,
/// encrypted under a key it does not hold, and every read-back
/// failed AES-GCM verification. Store must now derive IDs from the
/// content key + plaintext: different profiles get different IDs
/// for identical content, one profile still dedups, and blobs stored
/// under the old plaintext-hash scheme keep reading, since existing
/// catalogs reference them and Retrieve uses the catalog ID as AAD.
/// </summary>
public sealed class OfflineChunkIdTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string PassA = "profile-a-passphrase";
    private const string PassB = "profile-b-passphrase";

    public OfflineChunkIdTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-chunkid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string Dir(string name)
    {
        string dir = Path.Combine(_workDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Key(byte fill)
    {
        byte[] key = new byte[32];
        Array.Fill(key, fill);
        return key;
    }

    // The actual 38-byte content that collided on the laptop's stick.
    private static byte[] Content() => Encoding.UTF8.GetBytes("Hello from the Stealth Folders test.");

    private static List<string> StoreBytes(byte[] data, IBlobStore store, byte[] key)
    {
        using var input = new MemoryStream(data);
        return OfflineFileStore.Store(input, store, key).ChunkIds;
    }

    private static byte[] RetrieveOne(string chunkId, IBlobStore store, byte[] key)
    {
        using var output = new MemoryStream();
        OfflineFileStore.Retrieve([chunkId], store, key, output);
        return output.ToArray();
    }

    [Fact]
    public void DifferentKeys_DifferentIds_BothRetrieve()
    {
        var store = new LocalBlobStore(Dir("store"));
        byte[] data = Content();

        List<string> idsA = StoreBytes(data, store, Key(0xA1));
        List<string> idsB = StoreBytes(data, store, Key(0xB2));

        Assert.Single(idsA);
        Assert.Single(idsB);
        Assert.NotEqual(idsA[0], idsB[0]);
        Assert.Equal(data, RetrieveOne(idsA[0], store, Key(0xA1)));
        Assert.Equal(data, RetrieveOne(idsB[0], store, Key(0xB2)));
        // Isolation, not just distinct names: A's blob must not open under B's key
        // (AES-GCM throws a CryptographicException subclass on a bad tag).
        Assert.ThrowsAny<CryptographicException>(() => RetrieveOne(idsA[0], store, Key(0xB2)));
    }

    [Fact]
    public void SameKey_SameId_SingleBlob()
    {
        string root = Dir("store");
        var store = new LocalBlobStore(root);
        byte[] data = Content();

        List<string> first = StoreBytes(data, store, Key(0xA1));
        List<string> second = StoreBytes(data, store, Key(0xA1));

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "chunks"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void LegacyPlaintextHashBlob_StillRetrieves()
    {
        var store = new LocalBlobStore(Dir("store"));
        byte[] data = Content();
        byte[] key = Key(0xA1);

        // A pre-fix blob: ID = SHA-256 of the plaintext, AAD = that ID.
        byte[] legacyId = SHA256.HashData(data);
        string legacyHex = Convert.ToHexString(legacyId).ToLowerInvariant();
        byte[] blob = ChunkCrypto.Encrypt(data, key, legacyId);
        try { store.Put(legacyHex, blob); }
        finally { CryptographicOperations.ZeroMemory(blob); }

        Assert.Equal(data, RetrieveOne(legacyHex, store, key));
    }

    private static ServiceConfig ConfigWith(string passphrase)
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, passphrase);
        return new ServiceConfig { PassphraseProtected = "dpapi-blob" };
    }

    private static (ServiceConfig Config, OfflineFoldersSession Session, string Catalog) UnlockedFolder(
        string catalogPath, BackupLocation location, string passphrase)
    {
        var config = ConfigWith(passphrase);
        config.Locations.Add(location);
        var session = new OfflineFoldersSession();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, catalogPath, passphrase, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, catalogPath, "Vault", location.Name).Status);
        return (config, session, catalogPath);
    }

    [Fact]
    public void TwoProfiles_SharedSmbLocation_BothSaveAsSucceed()
    {
        // The laptop repro at dashboard level: two profiles, one shared
        // destination, identical content. Before the fix, the second
        // profile's Save As failed verification (400); both must pass now.
        string share = Dir("share");
        var location = new BackupLocation { Name = "nas", Type = "smb", Share = share };
        byte[] data = Content();

        var (configA, sessionA, catalogA) = UnlockedFolder(Path.Combine(_workDir, "a.json"), location, PassA);
        var (configB, sessionB, catalogB) = UnlockedFolder(Path.Combine(_workDir, "b.json"), location, PassB);

        Assert.Equal(200, DashboardApi.StoreOfflineFile(configA, sessionA, catalogA, "Vault",
            "shared.txt", new MemoryStream(data), DateTimeOffset.UtcNow).Status);
        Assert.Equal(200, DashboardApi.StoreOfflineFile(configB, sessionB, catalogB, "Vault",
            "shared.txt", new MemoryStream(data), DateTimeOffset.UtcNow).Status);

        string destA = Path.Combine(Dir("outA"), "shared.txt");
        string destB = Path.Combine(Dir("outB"), "shared.txt");
        var (statusA, payloadA) = DashboardApi.SaveOfflineFileAs(configA, sessionA, "Vault", "shared.txt",
            "local", null, destA, null, overwrite: false);
        var (statusB, payloadB) = DashboardApi.SaveOfflineFileAs(configB, sessionB, "Vault", "shared.txt",
            "local", null, destB, null, overwrite: false);
        Assert.Equal(200, statusA);
        Assert.Equal(200, statusB);
        Assert.Equal("shared.txt", Assert.IsType<OfflineSaveAsDto>(payloadA).FileName);
        Assert.Equal("shared.txt", Assert.IsType<OfflineSaveAsDto>(payloadB).FileName);
        Assert.Equal(data, File.ReadAllBytes(destA));
        Assert.Equal(data, File.ReadAllBytes(destB));
    }
}
