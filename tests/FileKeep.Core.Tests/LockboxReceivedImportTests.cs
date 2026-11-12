using System.Security.Cryptography;
using System.Text;
using FileKeep.Core;
using FileKeep.Core.Relay;
using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Import a received Lockbox by relay-inbox token
/// (DashboardApi.ImportReceivedLockbox): the one-click import behind
/// the per-row "Import as folder" button in the Lockboxes view.
/// Fixture notes: same shape as LockboxApiTests — test configs carry a
/// fake DPAPI blob, so the current secret resolves through the
/// FILEKEEP_PASSPHRASE env var; SMB test locations point at local temp
/// directories (SmbShare treats a local path as an already-connected
/// share, no network involved). The inbox seam
/// (ImportReceivedLockbox over an explicit RelayInbox) is driven with
/// simulated phone drops, so no paired relay or DPAPI is needed here.
/// The small fixtures below mirror LockboxApiTests' private helpers —
/// that file is owned by another worker, so they are not shared.
/// </summary>
public sealed class LockboxReceivedImportTests : IDisposable
{
    private const string Pass = "lockbox-test-passphrase";
    private const string LockPass = "lockbox-unlock-passphrase";

    private readonly string _workDir;
    private readonly string _srcDir;
    private readonly string? _savedPassphrase;

    public LockboxReceivedImportTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "fk-lockbox-recv-" + Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    // ----- fixtures -----

    private string CatalogPath() => Path.Combine(_workDir, "offline-folders.json");

    private string ShareDir(string name)
    {
        string dir = Path.Combine(_workDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ServiceConfig ConfigWith(params BackupLocation[] locations)
    {
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        foreach (var loc in locations)
            config.Locations.Add(loc);
        return config;
    }

    private static BackupLocation SmbLocation(string name, string shareDir) => new()
    {
        Name = name, Type = "smb", Share = shareDir,
    };

    private (ServiceConfig Config, OfflineFoldersSession Session, string Catalog, BackupLocation Location)
        UnlockedFolder(string folderName = "Vault")
    {
        var location = SmbLocation("loc1", ShareDir("share-" + folderName));
        var config = ConfigWith(location);
        var session = new OfflineFoldersSession();
        string catalog = CatalogPath();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, catalog, Pass, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, catalog, folderName, location.Name).Status);
        return (config, session, catalog, location);
    }

    private static void StoreFile(
        ServiceConfig config, OfflineFoldersSession session, string catalog,
        string folderName, string path, byte[] content)
    {
        using var stream = new MemoryStream(content);
        var (status, _) = DashboardApi.StoreOfflineFile(
            config, session, catalog, folderName, path, stream, DateTimeOffset.UtcNow, null);
        Assert.Equal(200, status);
    }

    private static byte[] ReadBack(
        OfflineFoldersSession session, BackupLocation location, OfflineFileEntry entry)
    {
        byte[]? key = session.CopyContentKey();
        Assert.NotNull(key);
        try
        {
            using var store = OfflineFileStore.ResolveStore(location);
            using var ms = new MemoryStream();
            OfflineFileStore.Retrieve(entry.ChunkIds, store.Store, key!, ms);
            return ms.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key!);
        }
    }

    private static string ErrorMessage((int Status, object Payload) result)
    {
        Assert.NotEqual(200, result.Status);
        var prop = result.Payload.GetType().GetProperty("Error");
        Assert.NotNull(prop);
        return (string?)prop.GetValue(result.Payload) ?? "";
    }

    private static DashboardApi.LockboxUnlockMethodDto PassphraseMethod(string passphrase) => new()
    {
        Type = "passphrase", Passphrase = passphrase,
    };

    private static (string Path, byte[] Plaintext) LkFile(string path, string text) =>
        (path, Encoding.UTF8.GetBytes(text));

    private static DashboardApi.LockboxDownloadDto Download((int Status, object Payload) result)
    {
        Assert.Equal(200, result.Status);
        return Assert.IsType<DashboardApi.LockboxDownloadDto>(result.Payload);
    }

    private static RelayInbox SimulatedInbox(string relayRoot, out byte[] hmacKey)
    {
        byte[] seed = RelayCrypto.NewPairingSecret();
        hmacKey = RelayCrypto.DeriveHmacKey(seed);
        byte[] encKey = RelayCrypto.DeriveEncKey(seed);
        var writer = new RelayWriter(relayRoot, hmacKey, encKey); // creates the relay folders
        return new RelayInbox(relayRoot, hmacKey, encKey, writer);
    }

    private static void SimulatePhoneDrop(
        string relayRoot, string token, byte[] kfb, string name, byte[] hmacKey, bool signCorrectly)
    {
        // Same shape as LockboxApiTests.SimulatePhoneDrop: .kfb + .par2
        // + signed .json in inbox/laptop/.
        string inboxDir = Path.Combine(relayRoot, "inbox", "laptop");
        File.WriteAllBytes(Path.Combine(inboxDir, token + ".kfb"), kfb);
        File.WriteAllBytes(Path.Combine(inboxDir, token + ".kfb.par2"), RelayPar2.Generate(kfb));
        var created = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var meta = new RelayInboxMetadata
        {
            Token = token,
            Name = name,
            Size = kfb.Length,
            CreatedUtc = created,
            Hmac = signCorrectly
                ? RelayCrypto.ComputeInboxHmac(hmacKey, token, name, kfb.Length, created)
                : "00",
        };
        File.WriteAllText(Path.Combine(inboxDir, token + ".json"),
            RelayJson.Serialize(meta, RelayJson.Indented, RelayJsonContext.Default.RelayInboxMetadata));
    }

    private static DashboardApi.ImportReceivedLockboxRequestDto ImportRequest(
        string token, string folderName, string location, string passphrase) => new()
    {
        Token = token,
        FolderName = folderName,
        Location = location,
        Unlock = new DashboardApi.LockboxUnlockDto { Passphrase = passphrase },
    };

    private byte[] KfbWith(params (string Path, string Text)[] files) =>
        Download(DashboardApi.CreateLockboxFromFiles(
            files.Select(f => LkFile(f.Path, f.Text)).ToList(),
            new[] { PassphraseMethod(LockPass) }, requireAll: false)).Bytes;

    // ----- import by token -----

    [Fact]
    public void ImportReceivedLockbox_HappyPath_CreatesFolderAndReadsBack()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        string relayRoot = Path.Combine(_workDir, "relay");
        var inbox = SimulatedInbox(relayRoot, out byte[] hmacKey);
        byte[] kfb = KfbWith(("hello.txt", "imported hello"), ("sub/world.txt", "imported world"));
        SimulatePhoneDrop(relayRoot, "recv0001", kfb, "from-phone.kfb", hmacKey, signCorrectly: true);

        var (status, payload) = DashboardApi.ImportReceivedLockbox(
            config, inbox, session, catalog,
            ImportRequest("recv0001", "Imported", location.Name, LockPass));
        Assert.Equal(200, status);
        var summary = Assert.IsType<DashboardApi.LockboxToFolderResultDto>(payload);
        Assert.Equal("Imported", summary.FolderName);
        Assert.Equal(2, summary.FileCount);
        Assert.False(summary.Merged);

        var entries = session.GetEntries();
        Assert.NotNull(entries);
        var folder = entries!.First(e => e.Name == "Imported");
        Assert.Equal(2, folder.Files.Count);
        foreach (var entry in folder.Files)
        {
            byte[] back = ReadBack(session, location, entry);
            string expected = entry.Path == "hello.txt" ? "imported hello" : "imported world";
            Assert.Equal(expected, Encoding.UTF8.GetString(back));
        }
    }

    [Fact]
    public void ImportReceivedLockbox_UnknownToken_Returns404()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        string relayRoot = Path.Combine(_workDir, "relay");
        var inbox = SimulatedInbox(relayRoot, out byte[] hmacKey);
        byte[] kfb = KfbWith(("a.txt", "x"));
        SimulatePhoneDrop(relayRoot, "recv0001", kfb, "from-phone.kfb", hmacKey, signCorrectly: true);

        var r = DashboardApi.ImportReceivedLockbox(
            config, inbox, session, catalog,
            ImportRequest("no-such-token", "Imported", location.Name, LockPass));
        Assert.Equal(404, r.Status);
        Assert.Contains("No received Lockbox with that token", ErrorMessage(r), StringComparison.Ordinal);
        Assert.DoesNotContain(session.GetEntries()!, e => e.Name == "Imported");
    }

    [Fact]
    public void ImportReceivedLockbox_Merge_AppendsNewPaths()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        StoreFile(config, session, catalog, "Vault", "existing.txt", Encoding.UTF8.GetBytes("old"));
        string relayRoot = Path.Combine(_workDir, "relay");
        var inbox = SimulatedInbox(relayRoot, out byte[] hmacKey);
        byte[] kfb = KfbWith(("new.txt", "fresh"));
        SimulatePhoneDrop(relayRoot, "recv0002", kfb, "from-phone.kfb", hmacKey, signCorrectly: true);

        var (status, payload) = DashboardApi.ImportReceivedLockbox(
            config, inbox, session, catalog,
            ImportRequest("recv0002", "Vault", location.Name, LockPass));
        Assert.Equal(200, status);
        Assert.True(Assert.IsType<DashboardApi.LockboxToFolderResultDto>(payload).Merged);
        var folder = session.GetEntries()!.First(e => e.Name == "Vault");
        Assert.Equal(2, folder.Files.Count);
        Assert.Contains(folder.Files, f => f.Path == "existing.txt");
        Assert.Contains(folder.Files, f => f.Path == "new.txt");
        Assert.Equal("fresh", Encoding.UTF8.GetString(ReadBack(session, location,
            folder.Files.First(f => f.Path == "new.txt"))));
    }

    [Fact]
    public void ImportReceivedLockbox_SamePathConflict_Refused400_NothingImported()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        StoreFile(config, session, catalog, "Vault", "a.txt", Encoding.UTF8.GetBytes("original"));
        string relayRoot = Path.Combine(_workDir, "relay");
        var inbox = SimulatedInbox(relayRoot, out byte[] hmacKey);
        byte[] kfb = KfbWith(("a.txt", "replacement"), ("b.txt", "innocent"));
        SimulatePhoneDrop(relayRoot, "recv0003", kfb, "from-phone.kfb", hmacKey, signCorrectly: true);

        var r = DashboardApi.ImportReceivedLockbox(
            config, inbox, session, catalog,
            ImportRequest("recv0003", "Vault", location.Name, LockPass));
        // Same rule as lockbox-to-folder / ImportOfflineFolder: never
        // silently replace — refused naming the conflicting path, and
        // NOTHING is imported (not even the non-conflicting file).
        Assert.Equal(400, r.Status);
        string message = ErrorMessage(r);
        Assert.Contains("Already in folder 'Vault'", message, StringComparison.Ordinal);
        Assert.Contains("a.txt", message, StringComparison.Ordinal);
        var folder = session.GetEntries()!.First(e => e.Name == "Vault");
        Assert.Single(folder.Files);
        Assert.Equal("original", Encoding.UTF8.GetString(ReadBack(session, location, folder.Files[0])));
    }

    [Fact]
    public void ImportReceivedLockbox_WrongPassphrase_Returns400()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        string relayRoot = Path.Combine(_workDir, "relay");
        var inbox = SimulatedInbox(relayRoot, out byte[] hmacKey);
        byte[] kfb = KfbWith(("a.txt", "x"));
        SimulatePhoneDrop(relayRoot, "recv0004", kfb, "from-phone.kfb", hmacKey, signCorrectly: true);

        var r = DashboardApi.ImportReceivedLockbox(
            config, inbox, session, catalog,
            ImportRequest("recv0004", "Imported", location.Name, "wrong"));
        Assert.Equal(400, r.Status);
        Assert.Contains("Could not unlock", ErrorMessage(r), StringComparison.Ordinal);
        Assert.DoesNotContain(session.GetEntries()!, e => e.Name == "Imported");
    }

    [Fact]
    public void ImportReceivedLockbox_BadSignature_Returns400()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        string relayRoot = Path.Combine(_workDir, "relay");
        var inbox = SimulatedInbox(relayRoot, out byte[] hmacKey);
        byte[] kfb = KfbWith(("a.txt", "x"));
        SimulatePhoneDrop(relayRoot, "recv0005", kfb, "unsigned.kfb", hmacKey, signCorrectly: false);

        var r = DashboardApi.ImportReceivedLockbox(
            config, inbox, session, catalog,
            ImportRequest("recv0005", "Imported", location.Name, LockPass));
        Assert.Equal(400, r.Status);
        Assert.Contains("HMAC", ErrorMessage(r), StringComparison.Ordinal);
        Assert.DoesNotContain(session.GetEntries()!, e => e.Name == "Imported");
    }
}
