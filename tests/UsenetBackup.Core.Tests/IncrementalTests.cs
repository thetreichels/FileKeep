using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Milestone 2 acceptance tests: parent-linked incremental backups.
/// Incremental manifests are self-contained (every file listed with full
/// chunk lists), so restore/verify never need the parent chain.
/// </summary>
public sealed class IncrementalTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public IncrementalTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-inc-test-" + Guid.NewGuid().ToString("N"));
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

    private void WriteSrc(string rel, byte[] data)
    {
        string full = Path.Combine(_srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        // Guarantee mtime changes are observable regardless of FS granularity.
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow);
    }

    private void TouchSrc(string rel, DateTime mtime)
    {
        string full = Path.Combine(_srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(full, mtime);
    }

    private static void AssertDirectoriesEqual(string expected, string actual)
    {
        var expectedFiles = Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(expected, f)).OrderBy(p => p).ToArray();
        var actualFiles = Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(actual, f)).OrderBy(p => p).ToArray();
        Assert.Equal(expectedFiles, actualFiles);
        foreach (var rel in expectedFiles)
            Assert.Equal(File.ReadAllBytes(Path.Combine(expected, rel)),
                         File.ReadAllBytes(Path.Combine(actual, rel)));
    }

    private static FileEntry Entry(BackupManifest m, string path) =>
        m.Files.Single(f => f.Path == path);

    [Fact]
    public void Incremental_OnlyChangedFilesProduceNewChunks()
    {
        WriteSrc("big.bin", RandomBytes(ChunkSize * 3 + 50));   // multi-chunk
        WriteSrc("small.txt", "version one"u8.ToArray());
        WriteSrc("other.bin", RandomBytes(ChunkSize * 2));

        BackupManifest full;
        long chunksAfterFull;
        using (var repo = InitRepo())
        {
            full = repo.BackupDirectory(_srcDir);
            chunksAfterFull = repo.StoredChunkCount();
            Assert.Equal("full", full.Type);
            Assert.Null(full.ParentId);
        }

        // Change exactly one file; bump its mtime so the change is detectable.
        WriteSrc("small.txt", "version two!!"u8.ToArray());

        BackupManifest inc;
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            inc = repo.BackupIncremental(_srcDir, full.BackupId);
            Assert.Equal("inc", inc.Type);
            Assert.Equal(full.BackupId, inc.ParentId);
            Assert.True(inc.VerifyRootHash());
            Assert.Empty(repo.Verify(inc.BackupId));

            // Unchanged files reuse the parent's chunk lists verbatim.
            Assert.Equal(Entry(full, "big.bin").Chunks, Entry(inc, "big.bin").Chunks);
            Assert.Equal(Entry(full, "other.bin").Chunks, Entry(inc, "other.bin").Chunks);
            // Changed file got fresh chunks.
            Assert.NotEqual(Entry(full, "small.txt").Chunks, Entry(inc, "small.txt").Chunks);

            // Only the changed file's new chunks were stored.
            long newUnique = repo.StoredChunkCount() - chunksAfterFull;
            Assert.Equal(Entry(inc, "small.txt").Chunks.Distinct().Count(), newUnique);
        }
    }

    [Fact]
    public void Incremental_RestoreMatchesCurrentTree_AddModifyDelete()
    {
        WriteSrc("keep.bin", RandomBytes(ChunkSize + 10));
        WriteSrc("modify.txt", "before"u8.ToArray());
        WriteSrc("delete-me.txt", "doomed"u8.ToArray());

        string fullId;
        using (var repo = InitRepo())
            fullId = repo.BackupDirectory(_srcDir).BackupId;

        // Modify one, add one, delete one.
        WriteSrc("modify.txt", "after - longer content here"u8.ToArray());
        WriteSrc("added.txt", "brand new"u8.ToArray());
        File.Delete(Path.Combine(_srcDir, "delete-me.txt"));

        string incId;
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            var inc = repo.BackupIncremental(_srcDir, fullId);
            incId = inc.BackupId;
            Assert.DoesNotContain(inc.Files, f => f.Path == "delete-me.txt");
            Assert.Contains(inc.Files, f => f.Path == "added.txt");
        }

        // Restoring the incremental reproduces the CURRENT tree exactly.
        string destInc = Path.Combine(_workDir, "restored-inc");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            repo.Restore(incId, destInc);
        AssertDirectoriesEqual(_srcDir, destInc);

        // Restoring the parent still reproduces the ORIGINAL tree.
        string destFull = Path.Combine(_workDir, "restored-full");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            repo.Restore(fullId, destFull);
        Assert.Equal(3, Directory.EnumerateFiles(destFull, "*", SearchOption.AllDirectories).Count());
        Assert.Equal("before"u8.ToArray(), File.ReadAllBytes(Path.Combine(destFull, "modify.txt")));
    }

    [Fact]
    public void Incremental_ChainOfThree_RestoresLatest()
    {
        WriteSrc("data.txt", "v1"u8.ToArray());
        string id0, id1, id2;
        using (var repo = InitRepo())
        {
            id0 = repo.BackupDirectory(_srcDir).BackupId;
            WriteSrc("data.txt", "v2"u8.ToArray());
            id1 = repo.BackupIncremental(_srcDir, id0).BackupId;
            WriteSrc("data.txt", "v3"u8.ToArray());
            id2 = repo.BackupIncremental(_srcDir, id1).BackupId;

            var m2 = repo.LoadManifest(id2);
            Assert.Equal("inc", m2.Type);
            Assert.Equal(id1, m2.ParentId);
            Assert.Empty(repo.Verify(id0));
            Assert.Empty(repo.Verify(id1));
            Assert.Empty(repo.Verify(id2));
        }

        string dest = Path.Combine(_workDir, "restored");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            repo.Restore(id2, dest);
        Assert.Equal("v3"u8.ToArray(), File.ReadAllBytes(Path.Combine(dest, "data.txt")));
    }

    [Fact]
    public void Incremental_MtimeOnlyChange_RechunksButDedups()
    {
        // Conservative behavior: mtime change forces re-chunking, but
        // content addressing means no new chunk blobs are stored.
        WriteSrc("file.bin", RandomBytes(ChunkSize + 7));
        string fullId;
        long chunksAfterFull;
        using (var repo = InitRepo())
        {
            fullId = repo.BackupDirectory(_srcDir).BackupId;
            chunksAfterFull = repo.StoredChunkCount();
        }

        TouchSrc("file.bin", DateTime.UtcNow.AddHours(1)); // content identical

        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            var inc = repo.BackupIncremental(_srcDir, fullId);
            Assert.Equal(repo.StoredChunkCount(), chunksAfterFull); // nothing new stored
            Assert.Empty(repo.Verify(inc.BackupId));

            string dest = Path.Combine(_workDir, "restored");
            repo.Restore(inc.BackupId, dest);
            AssertDirectoriesEqual(_srcDir, dest);
        }
    }

    [Fact]
    public void Incremental_UnknownParent_Throws()
    {
        WriteSrc("a.txt", "x"u8.ToArray());
        using var repo = InitRepo();
        repo.BackupDirectory(_srcDir);
        Assert.Throws<FileNotFoundException>(() =>
            repo.BackupIncremental(_srcDir, "deadbeef"));
    }
}
