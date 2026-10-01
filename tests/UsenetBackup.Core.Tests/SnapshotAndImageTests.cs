using System.Security.Cryptography;
using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Milestone 6 acceptance tests: snapshot providers (VSS contract +
/// passthrough), raw disk image backup/restore through the chunk pipeline,
/// and manifest backward compatibility for the new additive fields.
/// </summary>
public sealed class SnapshotAndImageTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public SnapshotAndImageTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private BackupRepository InitRepo() =>
        BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations);

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    /// <summary>
    /// Test double: freezes the source tree by copying it at construction,
    /// mimicking a point-in-time VSS shadow copy.
    /// </summary>
    private sealed class CopySnapshotProvider : ISnapshotProvider
    {
        public CopySnapshotProvider(string sourceDir)
        {
            SnapshotRoot = Path.Combine(Path.GetTempPath(), "ub-snap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SnapshotRoot);
            foreach (string dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(SnapshotRoot, Path.GetRelativePath(sourceDir, dir)));
            foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(SnapshotRoot, Path.GetRelativePath(sourceDir, file)));
        }

        public string SnapshotRoot { get; }
        public bool IsSnapshot => true;
        public string Name => "test-snap";
        public void Dispose()
        {
            try { Directory.Delete(SnapshotRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Vss_ThrowsPlatformNotSupported_OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
            return; // Real VSS path needs admin + Windows; validated there.
        Assert.Throws<PlatformNotSupportedException>(() => new VssSnapshotProvider(_srcDir));
    }

    [Fact]
    public void NullSnapshotProvider_IsLivePassthrough()
    {
        using var snap = new NullSnapshotProvider(_srcDir);
        Assert.Equal(Path.GetFullPath(_srcDir), snap.SnapshotRoot);
        Assert.False(snap.IsSnapshot);
        Assert.Equal("none", snap.Name);

        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "live"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDirectory(_srcDir, snap);
            backupId = manifest.BackupId;
            Assert.Null(manifest.Snapshot); // passthrough records no snapshot
            Assert.Null(manifest.Kind);     // pre-v0.6 shape: kind absent
            Assert.False(manifest.IsDiskImage);
        }
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            Assert.Empty(repo.Verify(backupId));
    }

    [Fact]
    public void Backup_WithSnapshotProvider_ReadsFrozenView()
    {
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "original"u8.ToArray());

        string backupId;
        using (var snap = new CopySnapshotProvider(_srcDir))
        {
            // Mutate the live tree AFTER the snapshot was taken.
            File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "changed-after-snapshot"u8.ToArray());
            File.WriteAllBytes(Path.Combine(_srcDir, "b.txt"), "new-file"u8.ToArray());

            using var repo = InitRepo();
            var manifest = repo.BackupDirectory(_srcDir, snap);
            backupId = manifest.BackupId;
            Assert.Equal("test-snap", manifest.Snapshot);
            // Frozen view: only a.txt with original content.
            Assert.Single(manifest.Files);
            Assert.Equal("a.txt", manifest.Files[0].Path);
        }

        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            Assert.Empty(repo.Verify(backupId));
            string dest = Path.Combine(_workDir, "restored");
            repo.Restore(backupId, dest);
            Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(Path.Combine(dest, "a.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "b.txt")));
        }
    }

    [Fact]
    public void DiskImage_BackupAndRestore_RoundTrips()
    {
        // Deliberately not a multiple of the chunk size.
        byte[] diskBytes = RandomBytes(2 * ChunkSize + 12345);
        string disk = Path.Combine(_workDir, "disk.bin");
        File.WriteAllBytes(disk, diskBytes);

        string backupId;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDiskImage(disk, "disk.img");
            backupId = manifest.BackupId;
            Assert.Equal("disk-image", manifest.Kind);
            Assert.True(manifest.IsDiskImage);
            Assert.Equal("full", manifest.Type);
            Assert.Null(manifest.Snapshot);
            Assert.Single(manifest.Files);
            var entry = manifest.Files[0];
            Assert.Equal("disk.img", entry.Path);
            Assert.Equal(diskBytes.Length, entry.Size);
            Assert.Equal(3, entry.Chunks.Count); // 2 full chunks + 1 partial
            Assert.NotEqual(entry.Chunks[0], entry.Chunks[1]);
        }

        // Restore to a fresh target of exactly the image size.
        string target = Path.Combine(_workDir, "restored-disk.bin");
        File.WriteAllBytes(target, new byte[diskBytes.Length]);
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            Assert.Empty(repo.Verify(backupId));
            repo.RestoreDiskImage(backupId, target);
        }
        Assert.Equal(diskBytes, File.ReadAllBytes(target));
    }

    [Fact]
    public void RestoreDiskImage_RefusesDirectoryBackup()
    {
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "x"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDirectory(_srcDir).BackupId;

        string target = Path.Combine(_workDir, "target.bin");
        File.WriteAllBytes(target, new byte[1024]);
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            Assert.Throws<InvalidOperationException>(() => repo.RestoreDiskImage(backupId, target));
    }

    [Fact]
    public void RestoreDiskImage_RefusesUndersizedTarget()
    {
        byte[] diskBytes = RandomBytes(ChunkSize + 7);
        string disk = Path.Combine(_workDir, "disk.bin");
        File.WriteAllBytes(disk, diskBytes);

        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDiskImage(disk).BackupId;

        string target = Path.Combine(_workDir, "small.bin");
        File.WriteAllBytes(target, new byte[diskBytes.Length - 1]);
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            Assert.Throws<IOException>(() => repo.RestoreDiskImage(backupId, target));
    }

    [Fact]
    public void RestoreDiskImage_RejectsCorruptedChunk()
    {
        byte[] diskBytes = RandomBytes(ChunkSize + 7);
        string disk = Path.Combine(_workDir, "disk.bin");
        File.WriteAllBytes(disk, diskBytes);

        string backupId;
        string chunkId;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDiskImage(disk);
            backupId = manifest.BackupId;
            chunkId = manifest.Files[0].Chunks[0];
        }

        // Corrupt one byte of the stored chunk blob: AES-GCM must reject it.
        string chunkPath = Path.Combine(_repoDir, "chunks", chunkId[..2], chunkId[2..]);
        byte[] blob = File.ReadAllBytes(chunkPath);
        blob[10] ^= 0xFF;
        File.WriteAllBytes(chunkPath, blob);

        string target = Path.Combine(_workDir, "target.bin");
        File.WriteAllBytes(target, new byte[diskBytes.Length]);
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            Assert.ThrowsAny<CryptographicException>(() => repo.RestoreDiskImage(backupId, target));
    }

    [Fact]
    public void Manifest_WithoutKindOrSnapshot_StillVerifies()
    {
        // New fields are additive and null-ignorable: a directory backup's
        // manifest JSON contains neither "kind" nor "snapshot", exactly the
        // v0.5 shape, and verifies unchanged.
        File.WriteAllBytes(Path.Combine(_srcDir, "a.txt"), "x"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDirectory(_srcDir).BackupId;

        string json = File.ReadAllText(Path.Combine(_repoDir, "manifests", backupId + ".json"));
        Assert.DoesNotContain("\"kind\"", json);
        Assert.DoesNotContain("\"snapshot\"", json);

        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            var loaded = repo.LoadManifest(backupId); // verifies root hash
            Assert.True(loaded.VerifyRootHash());
            Assert.Null(loaded.Kind);
            Assert.Null(loaded.Snapshot);
            Assert.False(loaded.IsDiskImage);
        }
    }
}
