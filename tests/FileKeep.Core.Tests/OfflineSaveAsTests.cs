using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Offline Folders slice 3 part 1: Save As — a one-way decrypted
/// export to a local path, a USB stick (by volume serial), or an
/// SMB share. The copy is streamed through StreamOfflineFile, so a
/// failed transfer must leave no partial plaintext behind.
///
/// Fixture note (same as the slice 2 file tests): test configs carry
/// a fake DPAPI blob, so the "current secret" resolves through the
/// FILEKEEP_PASSPHRASE env var. SMB locations point at local temp
/// directories: SmbShare treats a local path as an already-connected
/// share, so no network is involved. USB resolution needs a real
/// drive letter, so USB coverage is the not-connected case (on
/// Linux, volume serials never resolve).
/// </summary>
public sealed class OfflineSaveAsTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string Pass = "the-current-passphrase";

    public OfflineSaveAsTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-saveas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string CatalogPath() => Path.Combine(_workDir, "offline-folders.json");

    private string Dir(string name)
    {
        string dir = Path.Combine(_workDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ServiceConfig ConfigWith(params BackupLocation[] locations)
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        foreach (var loc in locations)
            config.Locations.Add(loc);
        return config;
    }

    private static (ServiceConfig Config, OfflineFoldersSession Session, string Catalog) UnlockedFolder(
        string catalogPath, BackupLocation location, string folderName = "Vault")
    {
        var config = ConfigWith(location);
        var session = new OfflineFoldersSession();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, catalogPath, Pass, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, catalogPath, folderName, location.Name).Status);
        return (config, session, catalogPath);
    }

    private static byte[] Content(int size, int seed)
    {
        var rnd = new Random(seed);
        byte[] data = new byte[size];
        rnd.NextBytes(data);
        return data;
    }

    private static void Store(ServiceConfig config, OfflineFoldersSession session, string catalog,
        string path, byte[] data)
    {
        var stored = DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            path, new MemoryStream(data), DateTimeOffset.UtcNow);
        Assert.Equal(200, stored.Status);
    }

    [Fact]
    public void Local_SaveAs_RoundTrip_MultiChunk()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        byte[] data = Content(5 * 1024 * 1024 + 7, seed: 11);
        Store(config, session, catalog, "big.bin", data);

        string destDir = Dir("out");
        string dest = Path.Combine(destDir, "big.bin");
        var (status, payload) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "big.bin",
            "local", null, dest, null, overwrite: false);
        Assert.Equal(200, status);
        var dto = Assert.IsType<OfflineSaveAsDto>(payload);
        Assert.Equal("big.bin", dto.FileName);
        Assert.Equal("local", dto.Destination);
        Assert.Equal(data.Length, dto.Bytes);
        Assert.Equal(data, File.ReadAllBytes(dest));

        // Save As must not touch the catalog: the file still streams.
        using var outMs = new MemoryStream();
        var (streamStatus, streamError) = DashboardApi.StreamOfflineFile(config, session, "Vault", "big.bin", outMs);
        Assert.Equal(200, streamStatus);
        Assert.Null(streamError);
        Assert.Equal(data, outMs.ToArray());
    }

    [Fact]
    public void Local_SaveAs_LockedRefused()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Store(config, session, catalog, "a.bin", Content(100, seed: 1));
        Assert.Equal(200, DashboardApi.LockOfflineFolders(session).Status);

        string dest = Path.Combine(Dir("out"), "a.bin");
        var (status, _) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "a.bin",
            "local", null, dest, null, overwrite: false);
        Assert.Equal(403, status);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void Local_SaveAs_UnknownFolder_And_File()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Store(config, session, catalog, "a.bin", Content(100, seed: 1));
        string dest = Path.Combine(Dir("out"), "a.bin");

        var (noFolder, _) = DashboardApi.SaveOfflineFileAs(config, session, "Nope", "a.bin",
            "local", null, dest, null, overwrite: false);
        Assert.Equal(404, noFolder);
        var (noFile, _) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "missing.bin",
            "local", null, dest, null, overwrite: false);
        Assert.Equal(404, noFile);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void Local_SaveAs_ExistingRefused_ThenOverwrite()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Store(config, session, catalog, "a.bin", Content(100, seed: 1));
        string dest = Path.Combine(Dir("out"), "a.bin");

        Assert.Equal(200, DashboardApi.SaveOfflineFileAs(config, session, "Vault", "a.bin",
            "local", null, dest, null, overwrite: false).Status);
        var (refused, _) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "a.bin",
            "local", null, dest, null, overwrite: false);
        Assert.Equal(400, refused);

        Store(config, session, catalog, "b.bin", Content(50, seed: 2));
        Assert.Equal(200, DashboardApi.SaveOfflineFileAs(config, session, "Vault", "b.bin",
            "local", null, dest, null, overwrite: true).Status);
        using var outMs = new MemoryStream();
        Assert.Equal(200, DashboardApi.StreamOfflineFile(config, session, "Vault", "b.bin", outMs).Status);
        Assert.Equal(outMs.ToArray(), File.ReadAllBytes(dest));
    }

    [Fact]
    public void Local_SaveAs_DirectoryPath_AppendsLeaf()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        byte[] data = Content(100, seed: 3);
        Store(config, session, catalog, "sub/dir/file.bin", data);

        string destDir = Dir("out");
        var (status, payload) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "sub/dir/file.bin",
            "local", null, destDir, null, overwrite: false);
        Assert.Equal(200, status);
        Assert.Equal("file.bin", Assert.IsType<OfflineSaveAsDto>(payload).FileName);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(destDir, "file.bin")));
    }

    [Fact]
    public void SaveAs_BadFileName_Refused()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Store(config, session, catalog, "a.bin", Content(100, seed: 1));
        string dest = Path.Combine(Dir("out"), "a.bin");

        var (status, _) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "a.bin",
            "local", null, dest, "../evil.bin", overwrite: false);
        Assert.Equal(400, status);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void Usb_SaveAs_AbsentDrive_Refused()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Store(config, session, catalog, "a.bin", Content(100, seed: 1));
        config.Locations.Add(new BackupLocation { Name = "stick", Type = "usb", VolumeSerial = "DEADBEEF" });

        var (status, _) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "a.bin",
            "usb", "stick", null, null, overwrite: false);
        Assert.Equal(400, status);
    }

    [Fact]
    public void SaveAs_WrongType_And_UnknownLocation_Refused()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Store(config, session, catalog, "a.bin", Content(100, seed: 1));

        var (wrongType, _) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "a.bin",
            "usb", "nas", null, null, overwrite: false);
        Assert.Equal(400, wrongType);
        var (unknown, _) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "a.bin",
            "smb", "ghost", null, null, overwrite: false);
        Assert.Equal(400, unknown);
    }

    [Fact]
    public void Smb_SaveAs_LocalShare_RoundTrip()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        byte[] data = Content(1000, seed: 5);
        Store(config, session, catalog, "doc.txt", data);
        string destShare = Dir("dest");
        config.Locations.Add(new BackupLocation { Name = "nas2", Type = "smb", Share = destShare });

        var (status, payload) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "doc.txt",
            "smb", "nas2", null, null, overwrite: false);
        Assert.Equal(200, status);
        var dto = Assert.IsType<OfflineSaveAsDto>(payload);
        Assert.Equal("doc.txt", dto.FileName);
        Assert.Equal("smb", dto.Destination);
        Assert.Equal(data.Length, dto.Bytes);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(destShare, "doc.txt")));
    }

    [Fact]
    public void SaveAs_MissingChunk_LeavesNoPartialFile()
    {
        string share = Dir("share");
        var (config, session, catalog) = UnlockedFolder(CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Store(config, session, catalog, "big.bin", Content(5 * 1024 * 1024 + 7, seed: 9));

        string chunksDir = Path.Combine(share, "chunks");
        string[] chunkFiles = Directory.GetFiles(chunksDir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(chunkFiles);
        File.Delete(chunkFiles[0]);

        string dest = Path.Combine(Dir("out"), "big.bin");
        var (status, _) = DashboardApi.SaveOfflineFileAs(config, session, "Vault", "big.bin",
            "local", null, dest, null, overwrite: false);
        Assert.Equal(400, status);
        Assert.False(File.Exists(dest));
    }
}
