using UsenetBackup.Core;
using UsenetBackup.Core.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

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
    public void EffectiveTargets_DefaultsToNntp_WhenAutoUpload()
    {
        var job = new BackupJobConfig { AutoUpload = true };
        Assert.Equal(new[] { "nntp" }, job.EffectiveTargets);
    }

    [Fact]
    public void EffectiveTargets_Empty_WhenNoAutoUploadNoTargets()
    {
        var job = new BackupJobConfig();
        Assert.Empty(job.EffectiveTargets);
    }

    [Fact]
    public void EffectiveTargets_UsesExplicitTargets()
    {
        var job = new BackupJobConfig { Targets = new List<string> { "smb" } };
        Assert.Equal(new[] { "smb" }, job.EffectiveTargets);
    }

    [Fact]
    public void EffectiveTargets_SupportsBoth_NormalizesAndDedups()
    {
        var job = new BackupJobConfig
        {
            AutoUpload = true, // explicit targets win over legacy flag
            Targets = new List<string> { "NNTP", "smb", "smb", "bogus" },
        };
        Assert.Equal(new[] { "nntp", "smb" }, job.EffectiveTargets);
    }

    [Fact]
    public void SmbShare_NormalizeUnc_RejectsNonUnc()
    {
        Assert.Throws<ArgumentException>(() => SmbShare.NormalizeUnc(@"C:\local\path"));
    }

    [Fact]
    public void SmbShare_GetShareRoot_ExtractsServerAndShare()
    {
        Assert.Equal(@"\\NAS\backups", SmbShare.GetShareRoot(@"\\NAS\backups\filekeep\deep"));
        Assert.Equal(@"\\srv\share", SmbShare.GetShareRoot(@"\\srv\share"));
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
    public void SmbSync_IsIdempotent_SkipsExistingFiles()
    {
        string repo = Path.Combine(_tmp, "repo2");
        Directory.CreateDirectory(Path.Combine(repo, "chunks"));
        Directory.CreateDirectory(Path.Combine(repo, "manifests"));
        File.WriteAllBytes(Path.Combine(repo, "chunks", "chunk1"), new byte[50]);

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
}
