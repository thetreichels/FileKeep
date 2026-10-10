using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Volume packing mode is sticky per backup: the manifest records the
/// packing, and flips are refused rather than silently breaking the
/// upload/download contract.
/// </summary>
public sealed class VolumeModeTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _workDir;
    private readonly string _repoDir;

    public VolumeModeTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-volmode-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static BackupManifest MakeManifest() => new()
    {
        BackupId = new string('a', 32),
        CreatedUtc = DateTime.UtcNow,
        ChunkSize = 4096,
        RootSha256 = new string('b', 64),
    };

    [Fact]
    public void FreshBackup_FollowsRepoConfig()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var manifest = MakeManifest();
        string[] chunks = [new('c', 64)];

        Assert.True(repo.ResolveVolumeMode(manifest, chunks, forceChunkMode: false, out string? r1));
        Assert.Null(r1);
        Assert.False(repo.ResolveVolumeMode(manifest, chunks, forceChunkMode: true, out string? r2));
        Assert.Null(r2);
    }

    [Fact]
    public void FreshBackup_RespectsUseVolumesFalse()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        // Flip repo.json to use_volumes=false and reopen.
        string json = File.ReadAllText(Path.Combine(_repoDir, "repo.json"));
        File.WriteAllText(Path.Combine(_repoDir, "repo.json"),
            json.Replace("\"use_volumes\": true", "\"use_volumes\": false"));
        using var reopened = BackupRepository.Open(_repoDir, Passphrase);

        var manifest = MakeManifest();
        Assert.False(reopened.ResolveVolumeMode(manifest, [new('c', 64)],
            forceChunkMode: false, out string? refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void PackedManifest_ForcesVolumeMode()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var manifest = MakeManifest();
        manifest.Volumes = [new VolumeEntry { Id = new('d', 64), ChunkIds = [new('c', 64)], SizeBytes = 100 }];
        manifest.VolumeSize = repo.VolumeSizeBytes;

        // Even with --no-volumes, a packed backup stays in volume mode.
        Assert.True(repo.ResolveVolumeMode(manifest, [new('c', 64)],
            forceChunkMode: true, out string? refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void PackedManifest_SizeChanged_Refuses()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var manifest = MakeManifest();
        manifest.Volumes = [new VolumeEntry { Id = new('d', 64), ChunkIds = [new('c', 64)], SizeBytes = 100 }];
        manifest.VolumeSize = repo.VolumeSizeBytes + 1; // config changed since packing

        Assert.False(repo.ResolveVolumeMode(manifest, [new('c', 64)],
            forceChunkMode: false, out string? refusal));
        Assert.NotNull(refusal);
        Assert.Contains("sticky", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChunkUploadedBackup_RefusesVolumeMode()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        string chunkId = new('e', 64);
        using (var catalog = new Catalog(repo.CatalogPath))
            catalog.RecordUpload("<" + chunkId + ".0123456789abcdef@usenet-backup>", chunkId);

        var manifest = MakeManifest();
        Assert.False(repo.ResolveVolumeMode(manifest, [chunkId],
            forceChunkMode: false, out string? refusal));
        Assert.NotNull(refusal);
        Assert.Contains("per-chunk", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyBackup_NeverUsesVolumes()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var manifest = MakeManifest();
        Assert.False(repo.ResolveVolumeMode(manifest, [],
            forceChunkMode: false, out string? refusal));
        Assert.Null(refusal);
    }
}
