using FileKeep.Core;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// v2 milestone 1: selective and merge restore.
/// <see cref="RestoreOptions.Include"/>/<see cref="RestoreOptions.Exclude"/>
/// narrow the restore by glob; <see cref="RestoreOptions.Merge"/> never
/// overwrites a file already present in the destination — identical files
/// are skipped silently, differing files are skipped and reported.
/// </summary>
public sealed class MergeRestoreTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public MergeRestoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-merge-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string BackupSource()
    {
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(_srcDir, "sub"));
        File.WriteAllText(Path.Combine(_srcDir, "sub", "b.txt"), "beta");
        File.WriteAllText(Path.Combine(_srcDir, "sub", "c.log"), "gamma");
        using var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations);
        return repo.BackupDirectory(_srcDir).BackupId;
    }

    private RestoreResult RestoreWith(string backupId, RestoreOptions options, string destName = "restored")
    {
        string dest = Path.Combine(_workDir, destName);
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        return repo.Restore(backupId, dest, options);
    }

    [Fact]
    public void SelectiveRestore_IncludeOnlyMatchingFiles()
    {
        string backupId = BackupSource();
        var options = new RestoreOptions();
        options.Include.Add("sub/*.txt");

        RestoreWith(backupId, options);

        string dest = Path.Combine(_workDir, "restored");
        Assert.True(File.Exists(Path.Combine(dest, "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "sub", "c.log")));
    }

    [Fact]
    public void SelectiveRestore_Exclude()
    {
        string backupId = BackupSource();
        var options = new RestoreOptions();
        options.Exclude.Add("*.log");

        RestoreWith(backupId, options);

        string dest = Path.Combine(_workDir, "restored");
        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "sub", "c.log")));
    }

    [Fact]
    public void SelectiveRestore_DoubleStarMatchesAnyDepth()
    {
        string backupId = BackupSource();
        var options = new RestoreOptions();
        options.Include.Add("**/*.txt");

        RestoreWith(backupId, options);

        string dest = Path.Combine(_workDir, "restored");
        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "sub", "c.log")));
    }

    [Fact]
    public void SelectiveRestore_BareDirNameImpliesSubtree()
    {
        string backupId = BackupSource();
        var options = new RestoreOptions();
        options.Include.Add("sub");

        RestoreWith(backupId, options);

        string dest = Path.Combine(_workDir, "restored");
        Assert.True(File.Exists(Path.Combine(dest, "sub", "b.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "c.log")));
        Assert.False(File.Exists(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void SelectiveRestore_NoMatchRestoresNothing()
    {
        string backupId = BackupSource();
        var options = new RestoreOptions();
        options.Include.Add("zzz/**");
        string dest = Path.Combine(_workDir, "restored");
        Directory.CreateDirectory(dest);

        var result = RestoreWith(backupId, options);

        Assert.Equal(0, result.RestoredFiles);
        Assert.False(Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void Merge_RestoresMissingFiles_SkipsIdentical()
    {
        string backupId = BackupSource();
        string dest = Path.Combine(_workDir, "restored");
        Directory.CreateDirectory(Path.Combine(dest, "sub"));
        File.WriteAllText(Path.Combine(dest, "a.txt"), "alpha"); // identical to backup

        var result = RestoreWith(backupId, new RestoreOptions { MergePolicy = MergePolicy.SkipDiffering });

        Assert.Equal(2, result.RestoredFiles); // sub/b.txt + sub/c.log
        Assert.Equal(1, result.SkippedIdentical);
        Assert.Empty(result.SkippedDiffering);
        Assert.Equal("beta", File.ReadAllText(Path.Combine(dest, "sub", "b.txt")));
    }

    [Fact]
    public void Merge_SkipsDifferingFiles_LeavesThemUntouched()
    {
        string backupId = BackupSource();
        string dest = Path.Combine(_workDir, "restored");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "user's newer content");

        var result = RestoreWith(backupId, new RestoreOptions { MergePolicy = MergePolicy.SkipDiffering });

        Assert.Equal("user's newer content", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal(new[] { "a.txt" }, result.SkippedDiffering);
        Assert.Equal(2, result.RestoredFiles);
        Assert.Equal(0, result.SkippedIdentical);
    }

    [Fact]
    public void KeepNewer_KeepsNewerDestFile()
    {
        string backupId = BackupSource();
        string dest = Path.Combine(_workDir, "restored");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "user's newer content");
        File.SetLastWriteTimeUtc(Path.Combine(dest, "a.txt"), DateTime.UtcNow.AddHours(1));

        var result = RestoreWith(backupId, new RestoreOptions { MergePolicy = MergePolicy.KeepNewer });

        Assert.Equal("user's newer content", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal(new[] { "a.txt" }, result.SkippedDiffering);
    }

    [Fact]
    public void KeepNewer_OverwritesWhenBackupIsNewer()
    {
        string backupId = BackupSource();
        string dest = Path.Combine(_workDir, "restored");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "stale content");
        File.SetLastWriteTimeUtc(Path.Combine(dest, "a.txt"), new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = RestoreWith(backupId, new RestoreOptions { MergePolicy = MergePolicy.KeepNewer });

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Empty(result.SkippedDiffering);
        Assert.Equal(3, result.RestoredFiles);
    }

    [Fact]
    public void Merge_SymlinkIdenticalSkipped_DifferingReported()
    {
        string backupId = BackupSource();
        string dest = Path.Combine(_workDir, "restored");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "user's newer content");

        var result = RestoreWith(backupId, new RestoreOptions());

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal(3, result.RestoredFiles);
        Assert.Empty(result.SkippedDiffering);
    }

    [Fact]
    public void NonMerge_OverwritesDifferingFiles()
    {
        SkipIfNoSymlinkPrivilege();
        File.WriteAllText(Path.Combine(_srcDir, "real.txt"), "data");
        File.CreateSymbolicLink(Path.Combine(_srcDir, "link.txt"), "real.txt");
        using (var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations))
        {
            string backupId = repo.BackupDirectory(_srcDir).BackupId;

            // Identical link already present (placed by a prior restore, so
            // the stored absolute target matches): skipped silently.
            string dest1 = Path.Combine(_workDir, "dest1");
            var selective = new RestoreOptions();
            selective.Include.Add("link.txt");
            using (var r = BackupRepository.Open(_repoDir, Passphrase))
            {
                r.Restore(backupId, dest1, selective);
                var res1 = r.Restore(backupId, dest1, new RestoreOptions { MergePolicy = MergePolicy.SkipDiffering });
                Assert.Equal(1, res1.SkippedIdentical);
                Assert.Empty(res1.SkippedDiffering);
            }

            // Differing link already present: reported, left alone.
            string dest2 = Path.Combine(_workDir, "dest2");
            Directory.CreateDirectory(dest2);
            File.CreateSymbolicLink(Path.Combine(dest2, "link.txt"), "other.txt");
            using (var r = BackupRepository.Open(_repoDir, Passphrase))
            {
                var res2 = r.Restore(backupId, dest2, new RestoreOptions { MergePolicy = MergePolicy.SkipDiffering });
                Assert.Equal(new[] { "link.txt" }, res2.SkippedDiffering);
                Assert.Equal(Path.Combine(dest2, "other.txt"),
                    File.ResolveLinkTarget(Path.Combine(dest2, "link.txt"), returnFinalTarget: false)?.ToString());
            }
        }
    }

    private static void SkipIfNoSymlinkPrivilege()
    {
        string probeDir = Path.Combine(Path.GetTempPath(), "ub-merge-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(probeDir);
            Directory.CreateSymbolicLink(Path.Combine(probeDir, "link"), probeDir);
        }
        catch (UnauthorizedAccessException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Creating symlinks requires privilege on this machine.");
        }
        finally
        {
            try { Directory.Delete(probeDir, recursive: true); } catch { }
        }
    }
}
