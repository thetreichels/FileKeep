using FileKeep.Core;
using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>Tests for SMB share sync and per-job upload targets.</summary>
public sealed class SmbTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "fk-smb-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    [Fact]
    public void EffectiveTargets_Empty_WhenNoTargets()
    {
        var job = new BackupJobConfig();
        Assert.Empty(job.EffectiveTargets);
    }

    [Fact]
    public void EffectiveTargets_UsesExplicitTargets()
    {
        var job = new BackupJobConfig { Targets = new List<string> { "NAS backups" } };
        Assert.Equal(new[] { "NAS backups" }, job.EffectiveTargets);
    }

    [Fact]
    public void EffectiveTargets_NormalizesAndDedups()
    {
        var job = new BackupJobConfig
        {
            Targets = new List<string> { " Frugal Usenet ", "NAS backups", "NAS backups" },
        };
        Assert.Equal(new[] { "Frugal Usenet", "NAS backups" }, job.EffectiveTargets);
    }

    [Fact]
    public void SmbShare_NormalizeUnc_RejectsNonUnc()
    {
        Assert.Throws<ArgumentException>(() => SmbShare.NormalizeUnc(@"C:\local\path"));
    }

    [Fact]
    public void Load_LegacyShapedConfig_Throws()
    {
        // No backwards compatibility: a pre-locations config file
        // carrying the retired per-job fields (autoUpload, smbShare,
        // smbUser, smbPasswordProtected) no longer loads — the
        // fail-closed loader rejects the unrecognized members.
        Directory.CreateDirectory(_tmp);
        string repo = Path.Combine(_tmp, "repo");
        string src = Path.Combine(_tmp, "src");
        string path = Path.Combine(_tmp, "service.json");
        string json = $$"""
        {
          "dashboardPort": 15789,
          "nntp": { "host": "news.example.com", "port": 119 },
          "jobs": [
            { "name": "docs", "repo": "{{repo}}", "source": "{{src}}",
              "schedule": "daily 02:00", "autoUpload": true,
              "smbShare": "\\\\NAS\\backups", "smbUser": "u",
              "smbPasswordProtected": "AAAA",
              "targets": ["nntp", "smb"] }
          ]
        }
        """;
        File.WriteAllText(path, json);

        var ex = Assert.Throws<InvalidOperationException>(() => ServiceConfig.Load(path));
        Assert.Contains("Invalid service config", ex.Message);
    }

    [Fact]
    public void SmbDiscovery_GetScanTargets_DoesNotThrow()
    {
        // Depends on real NICs; just verify it enumerates without throwing
        // and only yields IPv4 addresses.
        foreach (var ip in SmbDiscovery.GetScanTargets().Take(10))
        {
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip.AddressFamily);
        }
    }

    [Fact]
    public void SmbSync_CopiesChunksManifestsAndRepoJson()
    {
        // Build a fake repo.
        string repo = Path.Combine(_tmp, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "chunks", "ab"));
        Directory.CreateDirectory(Path.Combine(repo, "manifests"));
        File.WriteAllBytes(Path.Combine(repo, "chunks", "ab", "abcdef1234"), new byte[100]);
        File.WriteAllText(Path.Combine(repo, "manifests", "b1.json"), "{}");
        File.WriteAllText(Path.Combine(repo, "repo.json"), "{}");

        // The "share" is just a local directory for the test (no creds needed).
        string shareDir = Path.Combine(_tmp, "share");
        Directory.CreateDirectory(shareDir);
        using var share = SmbShare.Connect(ToUncOrLocal(shareDir), null, null);

        var sync = new SmbSync();
        var result = sync.Sync(repo, share);

        Assert.Equal(1, result.ChunksCopied);
        Assert.Equal(1, result.ManifestsCopied);
        Assert.True(File.Exists(Path.Combine(shareDir, "chunks", "ab", "abcdef1234")));
        Assert.True(File.Exists(Path.Combine(shareDir, "manifests", "b1.json")));
        Assert.True(File.Exists(Path.Combine(shareDir, "repo.json")));
    }

    [Fact]
    public void SmbSync_NonexistentRepo_Throws()
    {
        // SF-19: a wrong repo path must fail loudly, not "sync"
        // successfully with zero files.
        string shareDir = Path.Combine(_tmp, "share-missing");
        Directory.CreateDirectory(shareDir);
        using var share = SmbShare.Connect(ToUncOrLocal(shareDir), null, null);
        var sync = new SmbSync();
        Assert.Throws<DirectoryNotFoundException>(() =>
            sync.Sync(Path.Combine(_tmp, "no-such-repo"), share));
        // And it must not manufacture an empty repo layout on the share.
        Assert.False(Directory.Exists(Path.Combine(shareDir, "chunks")));
    }

    [Fact]
    public void SmbSync_DirectoryWithoutRepoJson_Throws()
    {
        // A directory that exists but is not a repository (no
        // repo.json) is not syncable either.
        string notARepo = Path.Combine(_tmp, "not-a-repo");
        Directory.CreateDirectory(Path.Combine(notARepo, "chunks"));
        string shareDir = Path.Combine(_tmp, "share-notrepo");
        Directory.CreateDirectory(shareDir);
        using var share = SmbShare.Connect(ToUncOrLocal(shareDir), null, null);
        var sync = new SmbSync();
        Assert.Throws<InvalidDataException>(() => sync.Sync(notARepo, share));
    }

    [Fact]
    public void SmbSync_IsIdempotent_SkipsExistingFiles()
    {
        string repo = Path.Combine(_tmp, "repo2");
        Directory.CreateDirectory(Path.Combine(repo, "chunks"));
        Directory.CreateDirectory(Path.Combine(repo, "manifests"));
        File.WriteAllBytes(Path.Combine(repo, "chunks", "chunk1"), new byte[50]);
        File.WriteAllText(Path.Combine(repo, "repo.json"), "{}");

        string shareDir = Path.Combine(_tmp, "share2");
        Directory.CreateDirectory(shareDir);
        using var share = SmbShare.Connect(ToUncOrLocal(shareDir), null, null);

        var sync = new SmbSync();
        var first = sync.Sync(repo, share);
        var second = sync.Sync(repo, share);

        Assert.Equal(1, first.ChunksCopied);
        Assert.Equal(0, second.ChunksCopied);
        Assert.Equal(1, second.ChunksSkipped);
    }

    [Fact]
    public void SmbSync_CopyChunks_MissingSource_Throws()
    {
        // DL-4: a batch chunk whose local blob is gone must fail the
        // copy — silently skipping it would let the caller prune on a
        // delivery that never happened.
        string repo = Path.Combine(_tmp, "repo-missing");
        Directory.CreateDirectory(Path.Combine(repo, "chunks", "ab"));
        string shareDir = Path.Combine(_tmp, "share-missing");
        Directory.CreateDirectory(shareDir);
        using var share = SmbShare.Connect(ToUncOrLocal(shareDir), null, null);

        var sync = new SmbSync();
        string id = "ab" + new string('c', 62);
        Assert.Throws<FileNotFoundException>(
            () => sync.CopyChunks(new[] { id }, repo, share));
    }

    [Fact]
    public void SmbSync_CopyChunks_SameLengthCorruptDest_IsRecopied()
    {
        // DL-4: a same-length destination file with different bytes is
        // NOT in sync — size equality alone must never confirm a chunk
        // the caller is about to prune the only other copy of.
        string repo = Path.Combine(_tmp, "repo-corrupt");
        string id = "ab" + new string('d', 62);
        string srcPath = Path.Combine(repo, "chunks", "ab", id[2..]);
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        byte[] good = new byte[100];
        Array.Fill(good, (byte)7);
        File.WriteAllBytes(srcPath, good);

        string shareDir = Path.Combine(_tmp, "share-corrupt");
        string destPath = Path.Combine(shareDir, "chunks", "ab", id[2..]);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        byte[] corrupt = new byte[100];
        Array.Fill(corrupt, (byte)9);
        File.WriteAllBytes(destPath, corrupt);

        using var share = SmbShare.Connect(ToUncOrLocal(shareDir), null, null);
        var sync = new SmbSync();
        int copied = sync.CopyChunks(new[] { id }, repo, share);

        Assert.Equal(1, copied);
        Assert.Equal(good, File.ReadAllBytes(destPath));
        // Second pass: present and content-verified — nothing copied.
        Assert.Equal(0, sync.CopyChunks(new[] { id }, repo, share));
    }

    [Fact]
    public void SmbSync_Sync_SameLengthStaleManifest_IsRecopied()
    {
        // DL-4 via the full Sync: same-size stale metadata must refresh
        // too (a stale manifest/repo.json is how a destination ends up
        // unable to see a backup that "synced fine").
        string repo = Path.Combine(_tmp, "repo-stale");
        Directory.CreateDirectory(Path.Combine(repo, "manifests"));
        File.WriteAllText(Path.Combine(repo, "manifests", "b1.json"), "{\"v\":2}");
        File.WriteAllText(Path.Combine(repo, "repo.json"), "{}");
        string shareDir = Path.Combine(_tmp, "share-stale");
        Directory.CreateDirectory(Path.Combine(shareDir, "manifests"));
        File.WriteAllText(Path.Combine(shareDir, "manifests", "b1.json"), "{\"v\":1}");
        using var share = SmbShare.Connect(ToUncOrLocal(shareDir), null, null);

        var sync = new SmbSync();
        var result = sync.Sync(repo, share);
        Assert.Equal(1, result.ManifestsCopied);
        Assert.Equal("{\"v\":2}",
            File.ReadAllText(Path.Combine(shareDir, "manifests", "b1.json")));
    }

    /// <summary>
    /// On Windows returns a UNC path; elsewhere returns the local path as-is
    /// (SmbShare.Connect without creds accepts local directories).
    /// </summary>
    private static string ToUncOrLocal(string localDir)
    {
        if (OperatingSystem.IsWindows())
        {
            // Administrative share to the local drive: \\localhost\C$\...
            string full = Path.GetFullPath(localDir);
            string drive = Path.GetPathRoot(full)!.TrimEnd('\\', ':');
            string rest = full.Substring(Path.GetPathRoot(full)!.Length);
            return $@"\\localhost\{drive}$\{rest}";
        }
        return localDir;
    }
    [Fact]
    public void ConnectionRefcount_LastReleaseDecidesCancellation()
    {
        // P2-C9: the OS connection may be cancelled only when the LAST
        // consumer of a process-made connection releases it — an early
        // consumer's Dispose used to cancel it under a running backup.
        string root = @"\\nas\p2c9-" + Guid.NewGuid().ToString("N");
        Assert.False(SmbShare.HasConnectionRef(root));

        // Owner connects; a second consumer reuses the connection.
        SmbShare.AcquireConnectionRef(root, processConnected: true);
        SmbShare.AcquireConnectionRef(root, processConnected: false);
        Assert.True(SmbShare.HasConnectionRef(root));

        // First release: not the last — no cancellation.
        Assert.False(SmbShare.ReleaseConnectionRef(root));
        Assert.True(SmbShare.HasConnectionRef(root));
        // Last release of a process-made connection: cancel.
        Assert.True(SmbShare.ReleaseConnectionRef(root));
        Assert.False(SmbShare.HasConnectionRef(root));

        // A connection the process did NOT make is never cancelled.
        SmbShare.AcquireConnectionRef(root, processConnected: false);
        Assert.False(SmbShare.ReleaseConnectionRef(root));
        // Unknown root: nothing to release, nothing to cancel.
        Assert.False(SmbShare.ReleaseConnectionRef(root));
    }
}
