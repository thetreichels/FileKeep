using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Milestone 1 acceptance tests. Every test uses a fresh repository with a
/// small chunk size and reduced KDF iterations to keep the suite fast;
/// the format fields record whatever was used, so the tests exercise the
/// real code paths.
/// </summary>
public sealed class RepositoryTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024; // 64 KiB: multi-chunk files without big fixtures
    private const int KdfIterations = 10_000; // fast for tests; production default is 600k

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public RepositoryTests()
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

    private void WriteSrc(string rel, byte[] data)
    {
        string full = Path.Combine(_srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
    }

    private static void AssertDirectoriesEqual(string expected, string actual)
    {
        var expectedFiles = Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(expected, f)).OrderBy(p => p).ToArray();
        var actualFiles = Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(actual, f)).OrderBy(p => p).ToArray();
        Assert.Equal(expectedFiles, actualFiles);
        foreach (var rel in expectedFiles)
        {
            byte[] e = File.ReadAllBytes(Path.Combine(expected, rel));
            byte[] a = File.ReadAllBytes(Path.Combine(actual, rel));
            Assert.Equal(e, a);
        }
    }

    [Fact]
    public void RoundTrip_RestoresIdenticalFiles()
    {
        WriteSrc("hello.txt", "hello world"u8.ToArray());
        WriteSrc("empty.bin", Array.Empty<byte>());
        WriteSrc("exact-chunk.bin", RandomBytes(ChunkSize));          // exactly one chunk
        WriteSrc("multi-chunk.bin", RandomBytes(ChunkSize * 3 + 123)); // spans chunks
        WriteSrc("nested/deep/file.dat", RandomBytes(1000));

        string backupId;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDirectory(_srcDir);
            backupId = manifest.BackupId;
            Assert.Empty(repo.Verify(backupId));
        }

        string dest = Path.Combine(_workDir, "restored");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            repo.Restore(backupId, dest);
        }
        AssertDirectoriesEqual(_srcDir, dest);
    }

    [Fact]
    public void Deduplication_StoresDuplicateChunksOnce()
    {
        byte[] dup = RandomBytes(ChunkSize * 2);
        WriteSrc("a.bin", dup);
        WriteSrc("sub/b.bin", dup); // identical content, different path

        using var repo = InitRepo();
        var m1 = repo.BackupDirectory(_srcDir);
        long afterFirst = repo.StoredChunkCount();

        // Backing up the identical tree again must not store any new chunks.
        var m2 = repo.BackupDirectory(_srcDir);
        Assert.Equal(afterFirst, repo.StoredChunkCount());

        // Two files share chunks: unique chunks == chunks of one file.
        Assert.Equal(m1.Files[0].Chunks, m1.Files[1].Chunks);
        Assert.NotEqual(m1.BackupId, m2.BackupId);
    }

    [Fact]
    public void Tamper_CorruptedChunkIsDetected()
    {
        WriteSrc("data.bin", RandomBytes(ChunkSize + 10));
        string backupId;
        string chunkPath;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDirectory(_srcDir);
            backupId = manifest.BackupId;
            Assert.Empty(repo.Verify(backupId));

            // Corrupt the first chunk blob on disk.
            string chunkId = manifest.Files[0].Chunks[0];
            chunkPath = Path.Combine(_repoDir, "chunks", chunkId[..2], chunkId[2..]);
        }

        byte[] blob = File.ReadAllBytes(chunkPath);
        blob[20] ^= 0xFF;
        File.WriteAllBytes(chunkPath, blob);

        using var repo2 = BackupRepository.Open(_repoDir, Passphrase);
        var errors = repo2.Verify(backupId);
        Assert.NotEmpty(errors); // GCM authentication must fail
        Assert.Throws<InvalidDataException>(() =>
            repo2.Restore(backupId, Path.Combine(_workDir, "restored")));
    }

    [Fact]
    public void Tamper_ModifiedManifestIsDetected()
    {
        WriteSrc("data.bin", RandomBytes(100));
        string backupId;
        using (var repo = InitRepo())
        {
            backupId = repo.BackupDirectory(_srcDir).BackupId;
        }

        string manifestPath = Path.Combine(_repoDir, "manifests", backupId + ".json");
        string json = File.ReadAllText(manifestPath);
        // Flip a byte in the middle of the JSON (not in root_sha256 itself).
        int idx = json.IndexOf("\"path\"", StringComparison.Ordinal);
        char[] chars = json.ToCharArray();
        chars[idx + 7] = chars[idx + 7] == 'a' ? 'b' : 'a';
        File.WriteAllText(manifestPath, new string(chars));

        using var repo2 = BackupRepository.Open(_repoDir, Passphrase);
        var errors = repo2.Verify(backupId);
        Assert.NotEmpty(errors); // root hash must fail
    }

    [Fact]
    public void WrongPassphrase_CannotDecrypt()
    {
        WriteSrc("secret.txt", "top secret"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
        {
            backupId = repo.BackupDirectory(_srcDir).BackupId;
        }

        using var repo2 = BackupRepository.Open(_repoDir, "wrong passphrase");
        // GCM authentication fails with the wrong key.
        Assert.NotEmpty(repo2.Verify(backupId));
    }

    [Fact]
    public void EmptyDirectory_BackupSucceeds()
    {
        using var repo = InitRepo();
        var manifest = repo.BackupDirectory(_srcDir);
        Assert.Empty(manifest.Files);
        Assert.Empty(repo.Verify(manifest.BackupId));
        Assert.True(manifest.VerifyRootHash());
    }
}
