using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Native-picker upload (files-by-path): the desktop app's native file dialog
/// returns local paths, not browser File objects, so the service stores by path
/// through the same StoreOfflineFile path as the multipart upload.
/// </summary>
public sealed class OfflineFilesByPathTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string Pass = "the-current-passphrase";

    public OfflineFilesByPathTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-off-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private static (ServiceConfig Config, OfflineFoldersSession Session, string Catalog) UnlockedFolder(
        string workDir, string catalogPath, BackupLocation location, string folderName = "Vault")
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        config.Locations.Add(location);
        var session = new OfflineFoldersSession();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, catalogPath, Pass, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, catalogPath, folderName, location.Name).Status);
        return (config, session, catalogPath);
    }

    [Fact]
    public void ByPath_StoresRealFile_AndListsIt()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        string source = Path.Combine(_workDir, "hello.txt");
        File.WriteAllText(source, "Hello from the native picker test.");

        var (status, payload) = DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault", new[] { source });
        Assert.Equal(200, status);
        var stored = Assert.IsAssignableFrom<List<OfflineFileDto>>(payload);
        Assert.Single(stored);
        Assert.Equal("hello.txt", stored[0].Path);

        var files = DashboardApi.GetOfflineFolderFiles(session, "Vault");
        Assert.Equal(200, files.Status);
        var list = Assert.IsAssignableFrom<List<OfflineFileDto>>(files.Payload);
        Assert.Contains(list, f => f.Path == "hello.txt");
    }

    [Fact]
    public void ByPath_MissingFile_Is400_AndStoresNothing()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        var (status, _) = DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault",
            new[] { Path.Combine(_workDir, "does-not-exist.txt") });
        Assert.Equal(400, status);

        var files = DashboardApi.GetOfflineFolderFiles(session, "Vault");
        var list = Assert.IsAssignableFrom<List<OfflineFileDto>>(files.Payload);
        Assert.Empty(list);
    }

    [Fact]
    public void ByPath_LockedSession_Is403()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var session = new OfflineFoldersSession(); // never unlocked
        string source = Path.Combine(_workDir, "hello.txt");
        File.WriteAllText(source, "x");

        var (status, _) = DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault", new[] { source });
        Assert.Equal(403, status);
    }

    [Fact]
    public void ByPath_MixedBatch_StopsBeforeStoringAnything()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        string good1 = Path.Combine(_workDir, "good1.txt");
        string good2 = Path.Combine(_workDir, "good2.txt");
        File.WriteAllText(good1, "first");
        File.WriteAllText(good2, "second");

        // Previously good1 was stored before the missing file failed the batch.
        var (status, _) = DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault",
            new[] { good1, Path.Combine(_workDir, "does-not-exist.txt"), good2 });
        Assert.Equal(400, status);

        var files = DashboardApi.GetOfflineFolderFiles(session, "Vault");
        var list = Assert.IsAssignableFrom<List<OfflineFileDto>>(files.Payload);
        Assert.Empty(list);
    }

    [Fact]
    public void ByPath_DuplicateBasenamesInBatch_Is400_AndStoresNothing()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        string dirA = Path.Combine(_workDir, "a");
        string dirB = Path.Combine(_workDir, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        string fileA = Path.Combine(dirA, "summary.pdf");
        string fileB = Path.Combine(dirB, "summary.pdf");
        File.WriteAllText(fileA, "report A");
        File.WriteAllText(fileB, "report B");

        // Previously the second file silently replaced the first in the catalog.
        var (status, _) = DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault",
            new[] { fileA, fileB });
        Assert.Equal(400, status);

        var files = DashboardApi.GetOfflineFolderFiles(session, "Vault");
        var list = Assert.IsAssignableFrom<List<OfflineFileDto>>(files.Payload);
        Assert.Empty(list);
    }

    [Fact]
    public void ByPath_ReaddSameName_ReplacesEntry()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        string source = Path.Combine(_workDir, "notes.txt");
        File.WriteAllText(source, "v1");
        Assert.Equal(200, DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault", new[] { source }).Status);

        // Re-adding the same file after editing it is the update path, not a
        // collision: the entry is replaced, not duplicated.
        File.WriteAllText(source, "v2-longer-content");
        Assert.Equal(200, DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault", new[] { source }).Status);

        var files = DashboardApi.GetOfflineFolderFiles(session, "Vault");
        var list = Assert.IsAssignableFrom<List<OfflineFileDto>>(files.Payload);
        var entry = Assert.Single(list);
        Assert.Equal("notes.txt", entry.Path);
        Assert.Equal("v2-longer-content".Length, entry.Size);
    }

    [Fact]
    public void ByPath_MidBatchStoreFailure_RollsBackEarlierFiles()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        string good = Path.Combine(_workDir, "good.txt");
        // '|' passes the open phase (a real, readable file) but fails name
        // validation inside the store, so it is a deterministic mid-batch
        // failure after the first file already stored.
        string bad = Path.Combine(_workDir, "bad|name.txt");
        File.WriteAllText(good, "stored before the failure");
        File.WriteAllText(bad, "fails validation");

        var (status, _) = DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault",
            new[] { good, bad });
        Assert.Equal(400, status);

        var files = DashboardApi.GetOfflineFolderFiles(session, "Vault");
        var list = Assert.IsAssignableFrom<List<OfflineFileDto>>(files.Payload);
        Assert.Empty(list);
    }

    [Fact]
    public void ByPath_MidBatchStoreFailure_RestoresReplacedEntry()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        string source = Path.Combine(_workDir, "notes.txt");
        string bad = Path.Combine(_workDir, "bad|name.txt");
        File.WriteAllText(source, "v1");
        Assert.Equal(200, DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault", new[] { source }).Status);

        // The batch replaces notes.txt, then fails on the second file: the
        // rollback must restore the original entry, not leave the replacement.
        File.WriteAllText(source, "v2-longer-content");
        File.WriteAllText(bad, "fails validation");
        var (status, _) = DashboardApi.AddOfflineFilesByPath(config, session, catalog, "Vault",
            new[] { source, bad });
        Assert.Equal(400, status);

        var files = DashboardApi.GetOfflineFolderFiles(session, "Vault");
        var list = Assert.IsAssignableFrom<List<OfflineFileDto>>(files.Payload);
        var entry = Assert.Single(list);
        Assert.Equal("notes.txt", entry.Path);
        Assert.Equal("v1".Length, entry.Size);
    }
}
