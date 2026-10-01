using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Requirements-compliance tests: exact tree round-trip (including empty
/// directories) and the append-only operation log. These cover standing
/// project requirements rather than a single milestone.
/// </summary>
public sealed class ComplianceTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public ComplianceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-cmp-test-" + Guid.NewGuid().ToString("N"));
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

    private void WriteSrc(string rel, byte[] data)
    {
        string full = Path.Combine(_srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
    }

    private string ReadLog() => File.ReadAllText(Path.Combine(_repoDir, "operations.log"));

    [Fact]
    public void RoundTrip_PreservesEmptyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "empty"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "empty", "nested-empty"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "has-files"));
        WriteSrc("has-files/a.txt", "x"u8.ToArray());
        WriteSrc("top.txt", "y"u8.ToArray());

        string backupId;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDirectory(_srcDir);
            backupId = manifest.BackupId;
            Assert.NotNull(manifest.Directories);
            Assert.Equal(new[] { "empty", "empty/nested-empty", "has-files" },
                         manifest.Directories);
        }

        string dest = Path.Combine(_workDir, "restored");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            repo.Restore(backupId, dest);

        Assert.True(Directory.Exists(Path.Combine(dest, "empty")));
        Assert.True(Directory.Exists(Path.Combine(dest, "empty", "nested-empty")));
        Assert.True(Directory.Exists(Path.Combine(dest, "has-files")));
        Assert.Equal("x"u8.ToArray(), File.ReadAllBytes(Path.Combine(dest, "has-files", "a.txt")));
    }

    [Fact]
    public void Incremental_DirectoryAddDelete()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "gone-soon"));
        string fullId;
        using (var repo = InitRepo())
            fullId = repo.BackupDirectory(_srcDir).BackupId;

        Directory.Delete(Path.Combine(_srcDir, "gone-soon"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "brand-new"));

        string incId;
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            incId = repo.BackupIncremental(_srcDir, fullId).BackupId;

        string destInc = Path.Combine(_workDir, "restored-inc");
        string destFull = Path.Combine(_workDir, "restored-full");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            repo.Restore(incId, destInc);
            repo.Restore(fullId, destFull);
        }
        Assert.False(Directory.Exists(Path.Combine(destInc, "gone-soon")));
        Assert.True(Directory.Exists(Path.Combine(destInc, "brand-new")));
        Assert.True(Directory.Exists(Path.Combine(destFull, "gone-soon")));
    }

    [Fact]
    public void Manifest_WithoutDirectoriesField_StillVerifies()
    {
        // A flat source produces no "directories" key at all, so the
        // canonical JSON is byte-identical to what v0.1 wrote: additive
        // fields never break already-written manifests.
        WriteSrc("a.txt", "x"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDirectory(_srcDir);
            backupId = manifest.BackupId;
            Assert.Null(manifest.Directories);
        }

        string json = File.ReadAllText(Path.Combine(_repoDir, "manifests", backupId + ".json"));
        Assert.DoesNotContain("\"directories\"", json);

        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            var loaded = repo.LoadManifest(backupId); // verifies root hash
            Assert.True(loaded.VerifyRootHash());
            Assert.Empty(repo.Verify(backupId));
        }
    }

    [Fact]
    public void Operations_AreLogged()
    {
        WriteSrc("a.txt", "x"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDirectory(_srcDir).BackupId;

        string dest = Path.Combine(_workDir, "restored");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            Assert.Empty(repo.Verify(backupId));
            repo.Restore(backupId, dest);
        }

        string log = ReadLog();
        Assert.Contains("[init]", log);
        Assert.Contains("[backup]", log);
        Assert.Contains("[verify]", log);
        Assert.Contains("[restore]", log);
        Assert.Contains($"id={backupId}", log);
        // Every line is UTC-timestamped.
        foreach (string line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z \[", line);
    }
}
