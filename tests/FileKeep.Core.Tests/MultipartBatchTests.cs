using System.Text;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Multipart batch store (StoreOfflineFilesBatch): duplicate basenames are
/// refused upfront, per-file failures are reported per file instead of
/// aborting the batch, and a locked session is refused upfront. A stream
/// that throws while being read fails just that file, like any other
/// per-file store failure.
/// </summary>
public sealed class MultipartBatchTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string Pass = "the-current-passphrase";

    public MultipartBatchTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-off-batch-" + Guid.NewGuid().ToString("N"));
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

    private static MemoryStream TextStream(string text) =>
        new(Encoding.UTF8.GetBytes(text));

    private static List<OfflineFileDto> ListFiles(OfflineFoldersSession session)
    {
        var files = DashboardApi.GetOfflineFolderFiles(session, "Vault");
        Assert.Equal(200, files.Status);
        return Assert.IsAssignableFrom<List<OfflineFileDto>>(files.Payload);
    }

    [Fact]
    public void Batch_DuplicateBasenames_Is400_AndStoresNothing()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        var entries = new List<(string, Stream, DateTimeOffset)>
        {
            ("a.txt", TextStream("one"), DateTimeOffset.UtcNow),
            ("a.txt", TextStream("two"), DateTimeOffset.UtcNow),
        };
        var (status, _) = DashboardApi.StoreOfflineFilesBatch(config, session, catalog, "Vault", entries, null);
        Assert.Equal(400, status);
        Assert.Empty(ListFiles(session));
    }

    [Fact]
    public void Batch_PartialFailure_ReportsPerFile()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        var entries = new List<(string, Stream, DateTimeOffset)>
        {
            ("good.txt", TextStream("good"), DateTimeOffset.UtcNow),
            ("bad|name.txt", TextStream("bad"), DateTimeOffset.UtcNow),
        };
        var (status, payload) = DashboardApi.StoreOfflineFilesBatch(config, session, catalog, "Vault", entries, null);
        Assert.Equal(200, status);
        var result = Assert.IsAssignableFrom<OfflineBatchResultDto>(payload);
        var stored = Assert.Single(result.Stored);
        Assert.Equal("good.txt", stored.Path);
        var failed = Assert.Single(result.Failed);
        Assert.Equal("bad|name.txt", failed.Path);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
        Assert.Single(ListFiles(session));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("simulated read failure");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Batch_ThrowingStream_ReportsPerFile_AndStoresRest()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        var entries = new List<(string, Stream, DateTimeOffset)>
        {
            ("good.txt", TextStream("good"), DateTimeOffset.UtcNow),
            ("broken.txt", new ThrowingStream(), DateTimeOffset.UtcNow),
        };
        var (status, payload) = DashboardApi.StoreOfflineFilesBatch(config, session, catalog, "Vault", entries, null);
        Assert.Equal(200, status);
        var result = Assert.IsAssignableFrom<OfflineBatchResultDto>(payload);
        var stored = Assert.Single(result.Stored);
        Assert.Equal("good.txt", stored.Path);
        var failed = Assert.Single(result.Failed);
        Assert.Equal("broken.txt", failed.Path);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
        Assert.Single(ListFiles(session));
    }

    [Fact]
    public void Batch_LockedSession_Is403()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var session = new OfflineFoldersSession(); // never unlocked
        var entries = new List<(string, Stream, DateTimeOffset)>
        {
            ("hello.txt", TextStream("x"), DateTimeOffset.UtcNow),
        };
        var (status, _) = DashboardApi.StoreOfflineFilesBatch(config, session, catalog, "Vault", entries, null);
        Assert.Equal(403, status);
    }

    [Fact]
    public void Batch_AllGood_StoresAll()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(_workDir, catalog, location);

        var entries = new List<(string, Stream, DateTimeOffset)>
        {
            ("one.txt", TextStream("one"), DateTimeOffset.UtcNow),
            ("two.txt", TextStream("two"), DateTimeOffset.UtcNow),
        };
        var (status, payload) = DashboardApi.StoreOfflineFilesBatch(config, session, catalog, "Vault", entries, null);
        Assert.Equal(200, status);
        var result = Assert.IsAssignableFrom<OfflineBatchResultDto>(payload);
        Assert.Equal(2, result.Stored.Count);
        Assert.Empty(result.Failed);
    }
}
