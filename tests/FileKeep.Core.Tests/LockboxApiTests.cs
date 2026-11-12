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
/// Lockbox (.kfb) API (DashboardApi.Lockbox): create from files, create
/// from backup (delegated to ExportBundle), folder -&gt; Lockbox (folder
/// + file scope, relay-send gating), Lockbox -&gt; folder (fail-closed
/// merge), method validation + AND/OR, and the received-list.
/// Fixture notes: test configs carry a fake DPAPI blob, so the current
/// secret resolves through the FILEKEEP_PASSPHRASE env var; SMB test
/// locations point at local temp directories (SmbShare treats a local
/// path as an already-connected share, no network involved). Relay
/// success paths that need DPAPI are Windows-only (see RelayDashboardTests);
/// here the beam path is covered up to the relay gate, plus temp-file
/// cleanup on that path.
/// </summary>
public sealed class LockboxApiTests : IDisposable
{
    private const string Pass = "lockbox-test-passphrase";
    private const string LockPass = "lockbox-unlock-passphrase";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly string? _savedPassphrase;

    public LockboxApiTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "fk-lockbox-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
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

    private static DashboardApi.LockboxUnlockMethodDto YubiKeyMethod(int slot = 2) => new()
    {
        Type = "yubikey-hmac-sha1", Slot = slot,
    };

    private static SimulatedYubiKeyProvider ProgrammedKey()
    {
        var provider = new SimulatedYubiKeyProvider();
        provider.ProgramSlot(2, RandomNumberGenerator.GetBytes(20));
        return provider;
    }

    private static (string Path, byte[] Plaintext) LkFile(string path, string text) =>
        (path, Encoding.UTF8.GetBytes(text));

    private static List<(string Path, byte[] Plaintext)> OpenedFiles(
        byte[] bundleBytes, DashboardApi.LockboxUnlockDto unlock, IYubiKeyProvider? provider = null)
    {
        var (header, files, error) = DashboardApi.OpenLockbox(bundleBytes, unlock, provider);
        Assert.Null(error);
        Assert.NotNull(header);
        Assert.NotNull(files);
        return files!;
    }

    private static DashboardApi.LockboxDownloadDto Download((int Status, object Payload) result)
    {
        Assert.Equal(200, result.Status);
        return Assert.IsType<DashboardApi.LockboxDownloadDto>(result.Payload);
    }

    private string MakeBackupWithFiles()
    {
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "backup file a");
        Directory.CreateDirectory(Path.Combine(_srcDir, "sub"));
        File.WriteAllText(Path.Combine(_srcDir, "sub", "b.txt"), "backup file b");
        BackupRepository.Init(_repoDir, Pass, 64 * 1024, 10_000).Dispose();
        using var repo = BackupRepository.Open(_repoDir, Pass);
        return repo.BackupDirectory(_srcDir).BackupId;
    }

    private ServiceConfig ConfigWithRepoJob() => new()
    {
        PassphraseProtected = "dpapi-blob",
        Jobs = new List<BackupJobConfig>
        {
            new() { Name = "docs", Repo = _repoDir, Sources = new List<string> { _srcDir },
                    Schedule = "interval 60", Mode = "incremental", BackupPrivilege = false },
        },
    };

    private static RestoreSession UnlockedSession()
    {
        // Restores are session-gated whenever an unlock method is
        // enabled (the dashboard route passes its restoreSession);
        // the config below sets PassphraseProtected, so pass an
        // unlocked session like DashboardTests does.
        var s = new RestoreSession();
        s.Unlock(Pass);
        return s;
    }

    /// <summary>Provider whose touch always times out (fault injection).</summary>
    private sealed class TimeoutProvider : IYubiKeyProvider
    {
        public IReadOnlyList<YubiKeyInfo> FindDevices() =>
            new[] { new YubiKeyInfo("simulated") };

        public byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout) =>
            throw new TimeoutException("no touch (simulated)");

        public void ProgramHmacSha1Slot(int slot, byte[] secret, bool requireTouch) =>
            throw new NotSupportedException();
    }

    private static string[] LockboxTempFiles() =>
        Directory.EnumerateFiles(Path.GetTempPath(), "fk-lockbox-*.kfb").ToArray();

    // ----- 1. create from files -----

    [Fact]
    public void CreateFromFiles_Passphrase_RoundTrips()
    {
        var files = new List<(string, byte[])> { LkFile("a.txt", "hello"), LkFile("sub/b.txt", "world") };
        var result = DashboardApi.CreateLockboxFromFiles(
            files, new[] { PassphraseMethod(LockPass) }, requireAll: false);
        var dto = Download(result);
        Assert.Equal("lockbox.kfb", dto.FileName);
        var opened = OpenedFiles(dto.Bytes, new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        Assert.Equal(2, opened.Count);
        Assert.Equal("hello", Encoding.UTF8.GetString(opened.First(f => f.Path == "a.txt").Plaintext));
        Assert.Equal("world", Encoding.UTF8.GetString(opened.First(f => f.Path == "sub/b.txt").Plaintext));
        foreach (var (_, plaintext) in opened)
            CryptographicOperations.ZeroMemory(plaintext);
    }

    [Fact]
    public void CreateFromFiles_CustomName_SetsDownloadFileName()
    {
        var result = DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])> { LkFile("a.txt", "x") },
            new[] { PassphraseMethod(LockPass) }, requireAll: false, name: "my-vault");
        Assert.Equal("my-vault.kfb", Download(result).FileName);
    }

    [Fact]
    public void CreateFromFiles_YubiKeyOr_RoundTrips()
    {
        var provider = ProgrammedKey();
        var result = DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])> { LkFile("a.txt", "yk") },
            new[] { YubiKeyMethod() }, requireAll: false, yubiKeyProvider: provider);
        var dto = Download(result);
        var opened = OpenedFiles(dto.Bytes,
            new DashboardApi.LockboxUnlockDto { UseYubiKey = true }, provider);
        Assert.Single(opened);
        Assert.Equal("yk", Encoding.UTF8.GetString(opened[0].Plaintext));
        foreach (var (_, plaintext) in opened)
            CryptographicOperations.ZeroMemory(plaintext);
    }

    [Fact]
    public void CreateFromFiles_And_PassphrasePlusYubiKey_RoundTrips()
    {
        var provider = ProgrammedKey();
        var methods = new[] { PassphraseMethod(LockPass), YubiKeyMethod() };
        var result = DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])> { LkFile("a.txt", "layered") },
            methods, requireAll: true, yubiKeyProvider: provider);
        var dto = Download(result);
        // Both methods together unlock.
        var opened = OpenedFiles(dto.Bytes,
            new DashboardApi.LockboxUnlockDto { Passphrase = LockPass, UseYubiKey = true }, provider);
        Assert.Equal("layered", Encoding.UTF8.GetString(opened[0].Plaintext));
        foreach (var (_, plaintext) in opened)
            CryptographicOperations.ZeroMemory(plaintext);
        // One method alone does not: the AND group needs every share.
        var (_, _, error) = DashboardApi.OpenLockbox(dto.Bytes,
            new DashboardApi.LockboxUnlockDto { Passphrase = LockPass }, provider);
        Assert.NotNull(error);
        Assert.Contains("YubiKey", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFromFiles_ValidationErrors()
    {
        var oneFile = new List<(string, byte[])> { LkFile("a.txt", "x") };
        // No methods.
        var r = DashboardApi.CreateLockboxFromFiles(oneFile, Array.Empty<DashboardApi.LockboxUnlockMethodDto>(), false);
        Assert.Equal(400, r.Status);
        Assert.Contains("At least one unlock method", ErrorMessage(r), StringComparison.Ordinal);
        // Unknown method type.
        r = DashboardApi.CreateLockboxFromFiles(oneFile,
            new[] { new DashboardApi.LockboxUnlockMethodDto { Type = "faceid" } }, false);
        Assert.Equal(400, r.Status);
        Assert.Contains("Unsupported unlock method", ErrorMessage(r), StringComparison.Ordinal);
        // Passphrase method without a passphrase.
        r = DashboardApi.CreateLockboxFromFiles(oneFile,
            new[] { new DashboardApi.LockboxUnlockMethodDto { Type = "passphrase" } }, false);
        Assert.Equal(400, r.Status);
        Assert.Contains("Passphrase is required", ErrorMessage(r), StringComparison.Ordinal);
        // AND requires 2 methods.
        r = DashboardApi.CreateLockboxFromFiles(oneFile, new[] { PassphraseMethod(LockPass) }, true);
        Assert.Equal(400, r.Status);
        Assert.Contains("AND requires at least 2 methods", ErrorMessage(r), StringComparison.Ordinal);
        // No files.
        r = DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])>(), new[] { PassphraseMethod(LockPass) }, false);
        Assert.Equal(400, r.Status);
    }

    [Fact]
    public void CreateFromFiles_YubiKeyTimeout_Returns400()
    {
        var r = DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])> { LkFile("a.txt", "x") },
            new[] { YubiKeyMethod() }, requireAll: false, yubiKeyProvider: new TimeoutProvider());
        Assert.Equal(400, r.Status);
        Assert.Contains("timed out", ErrorMessage(r), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateFromFiles_YubiKeyNoProvider_Returns500()
    {
        var r = DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])> { LkFile("a.txt", "x") },
            new[] { YubiKeyMethod() }, requireAll: false, yubiKeyProvider: null);
        Assert.Equal(500, r.Status);
        Assert.Contains("not available", ErrorMessage(r), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateFromFiles_WrongPassphrase_CannotOpen()
    {
        var dto = Download(DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])> { LkFile("a.txt", "secret") },
            new[] { PassphraseMethod(LockPass) }, requireAll: false));
        var (_, _, error) = DashboardApi.OpenLockbox(dto.Bytes,
            new DashboardApi.LockboxUnlockDto { Passphrase = "wrong-passphrase" });
        Assert.NotNull(error);
        Assert.Contains("Could not unlock", error, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenLockbox_NotAKfb_ReturnsError()
    {
        var (_, _, error) = DashboardApi.OpenLockbox(
            Encoding.UTF8.GetBytes("this is not a bundle"),
            new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        Assert.NotNull(error);
        Assert.Contains("Not a FileKeep Lockbox", error, StringComparison.Ordinal);
    }

    // ----- 2. create from backup (delegation) -----

    [Fact]
    public void CreateFromBackup_DelegatesToExportBundle_RoundTrips()
    {
        string backupId = MakeBackupWithFiles();
        var config = ConfigWithRepoJob();
        var request = new DashboardApi.LockboxFromBackupRequestDto
        {
            BackupId = backupId,
            Methods = new List<DashboardApi.LockboxUnlockMethodDto> { PassphraseMethod(LockPass) },
            Name = "docs-backup",
        };
        var raw = DashboardApi.CreateLockboxFromBackup(config, _repoDir, request, UnlockedSession());
        Assert.True(raw.Status == 200,
            $"status={raw.Status} payload={raw.Payload.GetType().Name} " +
            $"{(raw.Payload is FileKeep.Service.ErrorDto err ? err.Error : "")}");
        var dto = Assert.IsType<DashboardApi.LockboxDownloadDto>(raw.Payload);
        // The route names the download <name>.kfb, not export's hardcoded backup.kfb.
        Assert.Equal("docs-backup.kfb", dto.FileName);
        var opened = OpenedFiles(dto.Bytes, new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        Assert.True(opened.Count == 2,
            $"count={opened.Count} paths={string.Join(",", opened.Select(f => f.Path))}");
        Assert.Equal("backup file a", Encoding.UTF8.GetString(opened.First(f => f.Path == "a.txt").Plaintext));
        Assert.Equal("backup file b", Encoding.UTF8.GetString(opened.First(f => f.Path == "sub/b.txt").Plaintext));
        foreach (var (_, plaintext) in opened)
            CryptographicOperations.ZeroMemory(plaintext);
    }

    [Fact]
    public void CreateFromBackup_UnknownRepo_Returns400()
    {
        var config = ConfigWithRepoJob();
        var request = new DashboardApi.LockboxFromBackupRequestDto
        {
            BackupId = "whatever",
            Methods = new List<DashboardApi.LockboxUnlockMethodDto> { PassphraseMethod(LockPass) },
        };
        var r = DashboardApi.CreateLockboxFromBackup(config, Path.Combine(_workDir, "no-such-repo"), request);
        Assert.Equal(400, r.Status);
        Assert.Contains("Unknown repo", ErrorMessage(r), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFromBackup_MissingBackupId_Returns400()
    {
        var config = ConfigWithRepoJob();
        var request = new DashboardApi.LockboxFromBackupRequestDto
        {
            BackupId = "",
            Methods = new List<DashboardApi.LockboxUnlockMethodDto> { PassphraseMethod(LockPass) },
        };
        var r = DashboardApi.CreateLockboxFromBackup(config, _repoDir, request);
        Assert.Equal(400, r.Status);
        Assert.Contains("backupId is required", ErrorMessage(r), StringComparison.Ordinal);
    }

    // ----- 3. folder -> Lockbox -----

    [Fact]
    public void FolderToLockbox_FolderScope_RoundTrips()
    {
        var (config, session, catalog, _) = UnlockedFolder();
        StoreFile(config, session, catalog, "Vault", "a.txt", Encoding.UTF8.GetBytes("folder a"));
        StoreFile(config, session, catalog, "Vault", "sub/b.txt", Encoding.UTF8.GetBytes("folder b"));
        var request = new DashboardApi.FolderToLockboxRequestDto
        {
            FolderName = "Vault",
            Scope = OfflineExport.ScopeFolder,
            Methods = new List<DashboardApi.LockboxUnlockMethodDto> { PassphraseMethod(LockPass) },
        };
        var dto = Download(DashboardApi.FolderToLockbox(config, session, catalog, request));
        Assert.Equal("Vault.kfb", dto.FileName);
        var opened = OpenedFiles(dto.Bytes, new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        Assert.Equal(2, opened.Count);
        Assert.Equal("folder a", Encoding.UTF8.GetString(opened.First(f => f.Path == "a.txt").Plaintext));
        Assert.Equal("folder b", Encoding.UTF8.GetString(opened.First(f => f.Path == "sub/b.txt").Plaintext));
        foreach (var (_, plaintext) in opened)
            CryptographicOperations.ZeroMemory(plaintext);
    }

    [Fact]
    public void FolderToLockbox_FileScope_OnlyThatFile()
    {
        var (config, session, catalog, _) = UnlockedFolder();
        StoreFile(config, session, catalog, "Vault", "a.txt", Encoding.UTF8.GetBytes("folder a"));
        StoreFile(config, session, catalog, "Vault", "sub/b.txt", Encoding.UTF8.GetBytes("folder b"));
        var request = new DashboardApi.FolderToLockboxRequestDto
        {
            FolderName = "Vault",
            Scope = OfflineExport.ScopeFile,
            FilePath = "sub/b.txt",
            Methods = new List<DashboardApi.LockboxUnlockMethodDto> { PassphraseMethod(LockPass) },
        };
        var dto = Download(DashboardApi.FolderToLockbox(config, session, catalog, request));
        Assert.Equal("Vault-b.txt.kfb", dto.FileName);
        var opened = OpenedFiles(dto.Bytes, new DashboardApi.LockboxUnlockDto { Passphrase = LockPass });
        var only = Assert.Single(opened);
        Assert.Equal("sub/b.txt", only.Path);
        Assert.Equal("folder b", Encoding.UTF8.GetString(only.Plaintext));
        CryptographicOperations.ZeroMemory(only.Plaintext);
    }

    [Fact]
    public void FolderToLockbox_Errors()
    {
        var (config, session, catalog, _) = UnlockedFolder();
        var methods = new List<DashboardApi.LockboxUnlockMethodDto> { PassphraseMethod(LockPass) };
        // Unknown folder.
        var r = DashboardApi.FolderToLockbox(config, session, catalog,
            new DashboardApi.FolderToLockboxRequestDto { FolderName = "Nope", Methods = methods });
        Assert.Equal(404, r.Status);
        // Bad scope.
        r = DashboardApi.FolderToLockbox(config, session, catalog,
            new DashboardApi.FolderToLockboxRequestDto { FolderName = "Vault", Scope = "everything", Methods = methods });
        Assert.Equal(400, r.Status);
        // File scope without a filePath.
        r = DashboardApi.FolderToLockbox(config, session, catalog,
            new DashboardApi.FolderToLockboxRequestDto { FolderName = "Vault", Scope = OfflineExport.ScopeFile, Methods = methods });
        Assert.Equal(400, r.Status);
        // File scope naming a file that is not in the folder.
        r = DashboardApi.FolderToLockbox(config, session, catalog,
            new DashboardApi.FolderToLockboxRequestDto { FolderName = "Vault", Scope = OfflineExport.ScopeFile, FilePath = "ghost.txt", Methods = methods });
        Assert.Equal(404, r.Status);
        // Empty folder.
        r = DashboardApi.FolderToLockbox(config, session, catalog,
            new DashboardApi.FolderToLockboxRequestDto { FolderName = "Vault", Methods = methods });
        Assert.Equal(400, r.Status);
        Assert.Contains("no files", ErrorMessage(r), StringComparison.OrdinalIgnoreCase);
        // Locked session.
        session.Lock();
        r = DashboardApi.FolderToLockbox(config, session, catalog,
            new DashboardApi.FolderToLockboxRequestDto { FolderName = "Vault", Methods = methods });
        Assert.Equal(403, r.Status);
    }

    [Fact]
    public void FolderToLockbox_SendViaRelay_Unconfigured_Returns400_AndDeletesTemp()
    {
        var (config, session, catalog, _) = UnlockedFolder();
        StoreFile(config, session, catalog, "Vault", "a.txt", Encoding.UTF8.GetBytes("relay me"));
        string[] before = LockboxTempFiles();
        var request = new DashboardApi.FolderToLockboxRequestDto
        {
            FolderName = "Vault",
            Scope = OfflineExport.ScopeFolder,
            Methods = new List<DashboardApi.LockboxUnlockMethodDto> { PassphraseMethod(LockPass) },
            SendViaRelay = true,
            Name = "vault-drop",
        };
        var r = DashboardApi.FolderToLockbox(config, session, catalog, request);
        Assert.Equal(400, r.Status);
        Assert.Contains("Relay is not configured", ErrorMessage(r), StringComparison.Ordinal);
        // The staged temp .kfb is deleted even though beaming failed.
        Assert.Equal(before.Length, LockboxTempFiles().Length);
        Assert.Empty(LockboxTempFiles().Except(before));
    }

    // ----- 4. Lockbox -> folder -----

    [Fact]
    public void LockboxToFolder_NewFolder_RoundTrips()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        var dto = Download(DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])>
            {
                LkFile("hello.txt", "imported hello"),
                LkFile("sub/world.txt", "imported world"),
            },
            new[] { PassphraseMethod(LockPass) }, requireAll: false));
        var request = new DashboardApi.LockboxToFolderRequestDto
        {
            FolderName = "Imported",
            Location = location.Name,
            Unlock = new DashboardApi.LockboxUnlockDto { Passphrase = LockPass },
        };
        var (status, payload) = DashboardApi.LockboxToFolder(config, session, catalog, dto.Bytes, request);
        Assert.Equal(200, status);
        var summary = Assert.IsType<DashboardApi.LockboxToFolderResultDto>(payload);
        Assert.Equal("Imported", summary.FolderName);
        Assert.Equal(2, summary.FileCount);
        Assert.False(summary.Merged);
        // Read the files back through the offline store: plaintext matches.
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
    public void LockboxToFolder_Merge_AppendsNewPaths()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        StoreFile(config, session, catalog, "Vault", "existing.txt", Encoding.UTF8.GetBytes("old"));
        var dto = Download(DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])> { LkFile("new.txt", "fresh") },
            new[] { PassphraseMethod(LockPass) }, requireAll: false));
        var (status, payload) = DashboardApi.LockboxToFolder(config, session, catalog, dto.Bytes,
            new DashboardApi.LockboxToFolderRequestDto
            {
                FolderName = "Vault",
                Location = location.Name,
                Unlock = new DashboardApi.LockboxUnlockDto { Passphrase = LockPass },
            });
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
    public void LockboxToFolder_SamePathConflict_Refused400_FailClosed()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        StoreFile(config, session, catalog, "Vault", "a.txt", Encoding.UTF8.GetBytes("original"));
        var dto = Download(DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])>
            {
                LkFile("a.txt", "replacement"),
                LkFile("b.txt", "innocent"),
            },
            new[] { PassphraseMethod(LockPass) }, requireAll: false));
        var r = DashboardApi.LockboxToFolder(config, session, catalog, dto.Bytes,
            new DashboardApi.LockboxToFolderRequestDto
            {
                FolderName = "Vault",
                Location = location.Name,
                Unlock = new DashboardApi.LockboxUnlockDto { Passphrase = LockPass },
            });
        // Same rule as ImportOfflineFolder: never silently replace — the
        // import is refused naming the conflicting path, and NOTHING is
        // imported (not even the non-conflicting file).
        Assert.Equal(400, r.Status);
        string message = ErrorMessage(r);
        Assert.Contains("Already in folder 'Vault'", message, StringComparison.Ordinal);
        Assert.Contains("a.txt", message, StringComparison.Ordinal);
        var folder = session.GetEntries()!.First(e => e.Name == "Vault");
        Assert.Single(folder.Files);
        Assert.Equal("original", Encoding.UTF8.GetString(ReadBack(session, location, folder.Files[0])));
    }

    [Fact]
    public void LockboxToFolder_WrongPassphrase_Returns400()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        var dto = Download(DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])> { LkFile("a.txt", "x") },
            new[] { PassphraseMethod(LockPass) }, requireAll: false));
        var r = DashboardApi.LockboxToFolder(config, session, catalog, dto.Bytes,
            new DashboardApi.LockboxToFolderRequestDto
            {
                FolderName = "Imported",
                Location = location.Name,
                Unlock = new DashboardApi.LockboxUnlockDto { Passphrase = "wrong" },
            });
        Assert.Equal(400, r.Status);
        Assert.Contains("Could not unlock", ErrorMessage(r), StringComparison.Ordinal);
        Assert.DoesNotContain(session.GetEntries()!, e => e.Name == "Imported");
    }

    [Fact]
    public void LockboxToFolder_Errors()
    {
        var (config, session, catalog, location) = UnlockedFolder();
        var dto = Download(DashboardApi.CreateLockboxFromFiles(
            new List<(string, byte[])> { LkFile("a.txt", "x") },
            new[] { PassphraseMethod(LockPass) }, requireAll: false));
        DashboardApi.LockboxUnlockDto unlock = new() { Passphrase = LockPass };
        // Unknown location.
        var r = DashboardApi.LockboxToFolder(config, session, catalog, dto.Bytes,
            new DashboardApi.LockboxToFolderRequestDto { FolderName = "Imported", Location = "nope", Unlock = unlock });
        Assert.Equal(400, r.Status);
        Assert.Contains("That location does not exist", ErrorMessage(r), StringComparison.Ordinal);
        // Empty upload.
        r = DashboardApi.LockboxToFolder(config, session, catalog, Array.Empty<byte>(),
            new DashboardApi.LockboxToFolderRequestDto { FolderName = "Imported", Location = location.Name, Unlock = unlock });
        Assert.Equal(400, r.Status);
        // Not a .kfb at all.
        r = DashboardApi.LockboxToFolder(config, session, catalog, Encoding.UTF8.GetBytes("junk"),
            new DashboardApi.LockboxToFolderRequestDto { FolderName = "Imported", Location = location.Name, Unlock = unlock });
        Assert.Equal(400, r.Status);
        Assert.Contains("Not a FileKeep Lockbox", ErrorMessage(r), StringComparison.Ordinal);
        // Locked session.
        session.Lock();
        r = DashboardApi.LockboxToFolder(config, session, catalog, dto.Bytes,
            new DashboardApi.LockboxToFolderRequestDto { FolderName = "Imported", Location = location.Name, Unlock = unlock });
        Assert.Equal(403, r.Status);
    }

    // ----- 5. received Lockboxes -----

    [Fact]
    public void ListReceivedLockboxes_Unconfigured_Returns400()
    {
        var (status, payload) = DashboardApi.ListReceivedLockboxes(new ServiceConfig());
        Assert.Equal(400, status);
        Assert.Contains("Relay is not configured", ErrorMessage((status, payload)), StringComparison.Ordinal);
    }

    [Fact]
    public void ListReceivedLockboxes_ConfiguredButUnpaired_Returns400()
    {
        var config = new ServiceConfig { RelayFolder = Path.Combine(_workDir, "relay") };
        var (status, payload) = DashboardApi.ListReceivedLockboxes(config);
        Assert.Equal(400, status);
        Assert.Contains("No phone is paired", ErrorMessage((status, payload)), StringComparison.Ordinal);
    }

    [Fact]
    public void ListReceivedLockboxes_SimulatedPhoneDrop_ListsIt()
    {
        string relayRoot = Path.Combine(_workDir, "relay");
        byte[] seed = RelayCrypto.NewPairingSecret();
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(seed);
        byte[] encKey = RelayCrypto.DeriveEncKey(seed);
        var writer = new RelayWriter(relayRoot, hmacKey, encKey); // creates the relay folders
        var inbox = new RelayInbox(relayRoot, hmacKey, encKey, writer);
        byte[] kfb = Encoding.UTF8.GetBytes("fake bundle bytes for listing");
        SimulatePhoneDrop(relayRoot, "aaaa1111", kfb, "from-phone.kfb", hmacKey, signCorrectly: true);
        SimulatePhoneDrop(relayRoot, "bbbb2222", kfb, "unsigned.kfb", hmacKey, signCorrectly: false);

        var (status, payload) = DashboardApi.ListReceivedLockboxes(inbox);
        Assert.Equal(200, status);
        var list = Assert.IsType<DashboardApi.ReceivedLockboxDto[]>(payload);
        Assert.Equal(2, list.Length);
        var good = list.First(d => d.Token == "aaaa1111");
        Assert.Equal("from-phone.kfb", good.Name);
        Assert.Equal(kfb.Length, good.Size);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero), good.ReceivedUtc);
        Assert.True(good.SignatureValid);
        var bad = list.First(d => d.Token == "bbbb2222");
        Assert.False(bad.SignatureValid);
        Assert.NotNull(bad.RejectionReason);
    }

    [Fact]
    public void ListReceivedLockboxes_EmptyInbox_ReturnsEmpty()
    {
        string relayRoot = Path.Combine(_workDir, "relay");
        byte[] seed = RelayCrypto.NewPairingSecret();
        var writer = new RelayWriter(relayRoot,
            RelayCrypto.DeriveHmacKey(seed), RelayCrypto.DeriveEncKey(seed));
        var inbox = new RelayInbox(relayRoot,
            RelayCrypto.DeriveHmacKey(seed), RelayCrypto.DeriveEncKey(seed), writer);
        var (status, payload) = DashboardApi.ListReceivedLockboxes(inbox);
        Assert.Equal(200, status);
        Assert.Empty(Assert.IsType<DashboardApi.ReceivedLockboxDto[]>(payload));
    }

    private static void SimulatePhoneDrop(
        string relayRoot, string token, byte[] kfb, string name, byte[] hmacKey, bool signCorrectly)
    {
        // Same shape as RelayPollerTests.SimulatePhoneDrop: .kfb + .par2
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

    // ----- filename helper -----

    [Fact]
    public void DownloadFileName_Sanitizes()
    {
        Assert.Equal("vault.kfb", DashboardApi.DownloadFileName("vault", "fallback.kfb"));
        Assert.Equal("vault.kfb", DashboardApi.DownloadFileName("vault.kfb", "fallback.kfb"));
        Assert.Equal("a-b.kfb", DashboardApi.DownloadFileName("a/b", "fallback.kfb"));
        Assert.Equal("fallback.kfb", DashboardApi.DownloadFileName("   ", "fallback.kfb"));
        Assert.Equal("fallback.kfb", DashboardApi.DownloadFileName(null, "fallback.kfb"));
    }
}
