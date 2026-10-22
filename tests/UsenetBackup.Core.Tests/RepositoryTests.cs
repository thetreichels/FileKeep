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
    public void Restore_PartialFailure_ExceptionCarriesTheCounts()
    {
        // NS-11: the failure used to discard the RestoreResult — the
        // caller couldn't tell which files DID restore. The exception
        // now leads with the outcome counts.
        WriteSrc("good.txt", "good"u8.ToArray());
        WriteSrc("bad.txt", "bad!"u8.ToArray());
        string backupId, badChunkPath;
        using (var repo = InitRepo())
        {
            var manifest = repo.BackupDirectory(_srcDir);
            backupId = manifest.BackupId;
            var bad = manifest.Files.Single(f => f.Path.EndsWith("bad.txt"));
            string chunkId = bad.Chunks[0];
            badChunkPath = Path.Combine(_repoDir, "chunks", chunkId[..2], chunkId[2..]);
        }
        File.Delete(badChunkPath); // one file unrecoverable, one intact

        using var repo2 = BackupRepository.Open(_repoDir, Passphrase);
        string dest = Path.Combine(_workDir, "restored");
        var ex = Assert.Throws<InvalidDataException>(() => repo2.Restore(backupId, dest));
        Assert.Contains("1 file(s) restored", ex.Message);
        Assert.Contains("1 error(s)", ex.Message);
        // And the good file really is in place (restores are per-file).
        Assert.Equal("good", File.ReadAllText(Path.Combine(dest, "good.txt")));
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

    private static void CopyTree(string src, string dst, params string[] skipFiles)
    {
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            if (skipFiles.Contains(Path.GetFileName(file)))
                continue;
            string target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public void Backup_NfdFileName_RecordedAsNfc_AndMatchesParent()
    {
        // P2-D13: a file spelled in Unicode NFD (the macOS form) must be
        // recorded in NFC, and a second backup must match it against the
        // parent instead of treating the two spellings as two files.
        string nfd = "cafe\u0301.txt";   // café, decomposed
        string nfc = "caf\u00e9.txt";    // café, composed
        Assert.NotEqual(nfd, nfc);
        WriteSrc(nfd, "alpha"u8.ToArray());
        string id1, id2;
        using (var repo = InitRepo())
        {
            var m1 = repo.BackupDirectory(_srcDir);
            id1 = m1.BackupId;
            Assert.Equal(nfc, Assert.Single(m1.Files).Path);
            var m2 = repo.BackupDirectory(_srcDir);
            id2 = m2.BackupId;
            Assert.Equal(nfc, Assert.Single(m2.Files).Path);
        }
        Assert.NotEqual(id1, id2);
        Assert.Equal(nfc, BackupRepository.NormalizeManifestPath(nfd));
    }

    [Fact]
    public void ListBackups_ManifestDeletedExternally_RowNotListed()
    {
        // P2-D4: a catalog row whose manifest file is gone is a phantom
        // — nothing can load or restore it — and must not be listed.
        WriteSrc("a.txt", "alpha"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDirectory(_srcDir).BackupId;
        File.Delete(Path.Combine(_repoDir, "manifests", backupId + ".json"));
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
            Assert.DoesNotContain(repo.ListBackups(), b => b.BackupId == backupId);
    }

    [Fact]
    public void Open_RepoWithoutCatalog_RebuildsListingFromManifests()
    {
        // P2-D1: destination repos never receive catalog.db, so they
        // listed zero backups despite intact manifests and chunks. The
        // catalog is a rebuildable index — opening such a repo must
        // reconstruct the listing (and with it restores + incrementals'
        // parent lookup).
        WriteSrc("a.txt", "alpha-content"u8.ToArray());
        string id1, id2;
        using (var repo = InitRepo())
        {
            id1 = repo.BackupDirectory(_srcDir).BackupId;
            WriteSrc("b.txt", "beta-content"u8.ToArray());
            id2 = repo.BackupDirectory(_srcDir).BackupId;
        }

        string dest = Path.Combine(_workDir, "destination");
        CopyTree(_repoDir, dest, "catalog.db", "catalog.db-wal", "catalog.db-shm");
        Assert.False(File.Exists(Path.Combine(dest, "catalog.db")));

        using (var repo = BackupRepository.Open(dest, Passphrase))
        {
            var listed = repo.ListBackups();
            Assert.Equal(2, listed.Count);
            Assert.Contains(listed, b => b.BackupId == id1);
            Assert.Contains(listed, b => b.BackupId == id2);
            Assert.Empty(repo.Verify(id2));
            string outDir = Path.Combine(_workDir, "out");
            var result = repo.Restore(id2, outDir, new RestoreOptions());
            Assert.Equal(2, result.RestoredFiles);
            Assert.Equal("beta-content",
                File.ReadAllText(Path.Combine(outDir, "b.txt")));
        }
    }

    [Fact]
    public void Open_CorruptCatalog_QuarantinedAndRebuilt()
    {
        // P2-D2: a torn catalog.db used to brick every operation with a
        // raw SqliteException. It must be preserved as .corrupt-* and
        // rebuilt, like the sibling stores' corruption handling.
        WriteSrc("a.txt", "alpha-content"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDirectory(_srcDir).BackupId;

        string dbPath = Path.Combine(_repoDir, "catalog.db");
        File.Delete(dbPath + "-wal");
        File.Delete(dbPath + "-shm");
        File.WriteAllBytes(dbPath, "this is not a sqlite database"u8.ToArray());

        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            Assert.Contains(repo.ListBackups(), b => b.BackupId == backupId);
            Assert.Empty(repo.Verify(backupId));
        }
        Assert.NotEmpty(Directory.GetFiles(_repoDir, "catalog.db.corrupt-*"));
    }

    [Fact]
    public void ValidateWindowsPathSegments_RejectsHostileNames()
    {
        // P2-D11: reserved device stems, ADS colons, and trailing
        // dot/space segments are refused; ordinary names pass.
        Assert.Throws<InvalidDataException>(() =>
            BackupRepository.ValidateWindowsPathSegments("docs/CON.txt"));
        Assert.Throws<InvalidDataException>(() =>
            BackupRepository.ValidateWindowsPathSegments("nul"));
        Assert.Throws<InvalidDataException>(() =>
            BackupRepository.ValidateWindowsPathSegments("a/notes:draft"));
        Assert.Throws<InvalidDataException>(() =>
            BackupRepository.ValidateWindowsPathSegments("a/file."));
        Assert.Throws<InvalidDataException>(() =>
            BackupRepository.ValidateWindowsPathSegments("a/file /x.txt"));
        Assert.Throws<InvalidDataException>(() =>
            BackupRepository.ValidateWindowsPathSegments("COM7/readme.md"));
        // Clean paths pass (also: dots INSIDE names, long extensions).
        BackupRepository.ValidateWindowsPathSegments("docs/report.final.docx");
        BackupRepository.ValidateWindowsPathSegments("a b/c-d_e.txt");
        BackupRepository.ValidateWindowsPathSegments("conman/file.txt");
    }

    [Fact]
    public void ClaimRestorePath_SecondCaseTwinClaim_Throws()
    {
        // P2-D10: the claim set a Windows restore uses — the second
        // case-twin must be refused, not silently overwritten.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BackupRepository.ClaimRestorePath(claimed, @"C:\out\README.txt", "README.txt");
        var ex = Assert.Throws<InvalidDataException>(() =>
            BackupRepository.ClaimRestorePath(claimed, @"C:\out\Readme.txt", "Readme.txt"));
        Assert.Contains("differ only by case", ex.Message);
        // A null set (verify mode / case-sensitive filesystem) never claims.
        BackupRepository.ClaimRestorePath(null, @"C:\out\Readme.txt", "Readme.txt");
    }

    [Fact]
    public void Restore_CaseDistinctFiles_OnCaseSensitiveFs_BothRestored()
    {
        // The other half of P2-D10: where the filesystem IS case-
        // sensitive, README.txt and Readme.txt are two legitimate
        // files and both must restore (the claim is Windows-gated).
        if (OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("case-distinct files cannot be created on Windows");
        WriteSrc("README.txt", "upper"u8.ToArray());
        WriteSrc("Readme.txt", "mixed"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDirectory(_srcDir).BackupId;
        string outDir = Path.Combine(_workDir, "out-case");
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            var result = repo.Restore(backupId, outDir, new RestoreOptions());
            Assert.Equal(2, result.RestoredFiles);
        }
        Assert.Equal("upper", File.ReadAllText(Path.Combine(outDir, "README.txt")));
        Assert.Equal("mixed", File.ReadAllText(Path.Combine(outDir, "Readme.txt")));
    }

    [Fact]
    public void NonBatchedBackup_CatalogKnownButBlobPruned_ReStoresChunk()
    {
        // P2-D5: batched runs prune chunk BLOBS but never catalog rows.
        // The non-batched stager used to skip storing any chunk the
        // catalog knew — finalizing a manifest that references a chunk
        // existing nowhere. The store's own Exists is the only skip test.
        WriteSrc("a.txt", "chunk-content"u8.ToArray());
        string chunkPath;
        using (var repo = InitRepo())
        {
            var m1 = repo.BackupDirectory(_srcDir);
            string chunkId = m1.Files[0].Chunks[0];
            chunkPath = Path.Combine(_repoDir, "chunks", chunkId[..2], chunkId[2..]);
            Assert.True(File.Exists(chunkPath));
            // Simulate the batched prune: blob gone, catalog row stays.
            File.Delete(chunkPath);
        }

        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            var m2 = repo.BackupDirectory(_srcDir);
            Assert.True(File.Exists(chunkPath),
                "The second backup skipped re-storing a pruned chunk it only knew from the catalog.");
            Assert.Empty(repo.Verify(m2.BackupId));
        }
    }

    [Fact]
    public void BackupAndRestore_HonorCancellationToken()
    {
        // NS-3/NS-5: the token used to stop at the scheduler's phase
        // boundaries — BackupDirectory took no token at all and restores
        // none either. Both now abort promptly when cancelled.
        WriteSrc("a.txt", "data"u8.ToArray());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string backupId;
        using (var repo = InitRepo())
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                repo.BackupDirectory(_srcDir, snapshotProvider: null,
                    cancellationToken: cts.Token));
            backupId = repo.BackupDirectory(_srcDir).BackupId;
        }
        using (var repo = BackupRepository.Open(_repoDir, Passphrase))
        {
            string dest = Path.Combine(_workDir, "cancelled-restore");
            Assert.ThrowsAny<OperationCanceledException>(() =>
                repo.Restore(backupId, dest, new RestoreOptions(),
                    cancellationToken: cts.Token));
            Assert.False(File.Exists(Path.Combine(dest, "a.txt")),
                "A cancelled restore must not produce restored files.");
        }
    }

    [Fact]
    public void StagingSpaceError_FiresOnlyWhenFreeIsShort()
    {
        // NS-13: the preflight verdict — enough space passes silently,
        // a shortfall produces the plain-language refusal.
        Assert.Null(BackupRepository.StagingSpaceError(needed: 100, free: 100, root: "C:\\"));
        Assert.Null(BackupRepository.StagingSpaceError(needed: 100, free: 101, root: "C:\\"));
        string? error = BackupRepository.StagingSpaceError(
            needed: 3L * 1024 * 1024 * 1024, free: 512L * 1024 * 1024, root: "C:\\");
        Assert.NotNull(error);
        Assert.Contains("Not enough free space", error);
        Assert.Contains("3 GB", error);
        Assert.Contains("512 MB", error);
    }

    [Fact]
    public void WrongPassphrase_VerifyReportsOnePlainError_NotRawCryptoSpam()
    {
        // NS-1: every file used to fail with the raw AES-GCM message.
        // Verify must collapse crypto failures into one plain entry that
        // names the likely cause.
        WriteSrc("a.txt", "one"u8.ToArray());
        WriteSrc("b.txt", "two"u8.ToArray());
        WriteSrc("c.txt", "three"u8.ToArray());
        string backupId;
        using (var repo = InitRepo())
            backupId = repo.BackupDirectory(_srcDir).BackupId;

        using var repo2 = BackupRepository.Open(_repoDir, "wrong passphrase");
        var errors = repo2.Verify(backupId);
        Assert.Single(errors);
        Assert.Contains("passphrase is wrong", errors[0]);
        Assert.DoesNotContain("authentication tag", errors[0]);
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
