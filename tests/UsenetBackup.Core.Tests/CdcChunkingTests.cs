using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// v3 #4 (incremental performance for big files), phase 1: content-defined
/// chunking via a Gear rolling hash. A small edit to a large file must only
/// disturb chunk boundaries near the edit — unlike fixed-size chunking,
/// where one inserted byte shifts every later boundary and re-uploads the
/// whole file. Every test uses small chunk sizes and reduced KDF iterations
/// to keep the suite fast.
/// </summary>
public sealed class CdcChunkingTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024; // 64 KiB avg -> min 16 KiB, max 256 KiB
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public CdcChunkingTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-cdc-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] SeededRandom(int n, int seed)
    {
        var rng = new Random(seed);
        var b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    /// <summary>Splits data into chunks, feeding the chunker in feedSplit-sized pieces.</summary>
    private static List<byte[]> ChunkAll(byte[] data, CdcChunker chunker, int feedSplit)
    {
        var lens = new List<int>();
        for (int i = 0; i < data.Length; i += feedSplit)
            chunker.Feed(data.AsSpan(i, Math.Min(feedSplit, data.Length - i)), lens);
        chunker.Finish(lens);
        var chunks = new List<byte[]>();
        int off = 0;
        foreach (int len in lens)
        {
            chunks.Add(data.AsSpan(off, len).ToArray());
            off += len;
        }
        Assert.Equal(data.Length, off); // boundaries tile the input exactly
        return chunks;
    }

    private static List<string> ChunkHashes(byte[] data, int min, int avg, int max, int feedSplit = int.MaxValue) =>
        ChunkAll(data, new CdcChunker(min, avg, max), feedSplit)
            .Select(c => Hashing.Sha256Hex(c)).ToList();

    private void WriteSrc(string rel, byte[] data)
    {
        string full = Path.Combine(_srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow);
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

    // ---------- chunker unit tests ----------

    [Fact]
    public void Chunker_IsDeterministicAcrossFeedSplits()
    {
        byte[] data = SeededRandom(300 * 1024, 42);
        var whole = ChunkHashes(data, 4096, 16384, 65536);
        var split1 = ChunkHashes(data, 4096, 16384, 65536, feedSplit: 1);
        var split2 = ChunkHashes(data, 4096, 16384, 65536, feedSplit: 997);
        Assert.Equal(whole, split1);
        Assert.Equal(whole, split2);
        Assert.True(whole.Count > 2, "expected multiple chunks for 300 KiB at 16 KiB avg");
    }

    [Fact]
    public void Chunker_InsertionOnlyDisturbsNearbyChunks()
    {
        byte[] data = SeededRandom(512 * 1024, 7);
        byte[] insert = SeededRandom(100, 99);
        var edited = new byte[data.Length + insert.Length];
        const int at = 200 * 1024;
        Buffer.BlockCopy(data, 0, edited, 0, at);
        Buffer.BlockCopy(insert, 0, edited, at, insert.Length);
        Buffer.BlockCopy(data, at, edited, at + insert.Length, data.Length - at);

        var before = ChunkHashes(data, 4096, 16384, 65536);
        var after = ChunkHashes(edited, 4096, 16384, 65536);

        // Align from both ends; only the chunks overlapping the edit may differ.
        int prefix = 0;
        while (prefix < Math.Min(before.Count, after.Count) && before[prefix] == after[prefix])
            prefix++;
        int suffix = 0;
        while (suffix < Math.Min(before.Count, after.Count) - prefix &&
               before[before.Count - 1 - suffix] == after[after.Count - 1 - suffix])
            suffix++;
        int differingBefore = before.Count - prefix - suffix;
        int differingAfter = after.Count - prefix - suffix;

        Assert.True(suffix >= 1, "tail chunks past the edit must be identical");
        Assert.True(differingBefore <= 6 && differingAfter <= 6,
            $"insertion disturbed {differingBefore}/{differingAfter} chunks; expected only nearby chunks to change");

        // Contrast: fixed-size chunking re-hashes every block past the insertion.
        static List<string> Fixed(byte[] d, int size)
        {
            var out_ = new List<string>();
            for (int i = 0; i < d.Length; i += size)
                out_.Add(Hashing.Sha256Hex(d.AsSpan(i, Math.Min(size, d.Length - i))));
            return out_;
        }
        var fixedBefore = Fixed(data, 16384);
        var fixedAfter = Fixed(edited, 16384);
        int fixedSame = fixedBefore.Zip(fixedAfter, (a, b) => a == b).Count(eq => eq);
        Assert.True(fixedSame < fixedBefore.Count / 2,
            $"fixed-size chunking kept {fixedSame}/{fixedBefore.Count} chunks; insertion should shift most of them");
    }

    [Fact]
    public void Chunker_RespectsMinMaxBounds()
    {
        const int min = 4096, avg = 16384, max = 65536;
        // Random content and adversarial constant content (worst case for
        // natural cut points — the max force-cut must still bound chunks).
        foreach (byte[] data in new[] { SeededRandom(2 * 1024 * 1024, 3), new byte[1024 * 1024] })
        {
            var chunks = ChunkAll(data, new CdcChunker(min, avg, max), 8192);
            Assert.True(chunks.Count >= 2, "expected multiple chunks");
            for (int i = 0; i < chunks.Count - 1; i++) // last chunk may be short
                Assert.InRange(chunks[i].Length, min, max);
        }
    }

    [Fact]
    public void Chunker_SmallInput_YieldsSingleChunk()
    {
        var chunks = ChunkAll(new byte[100], new CdcChunker(4096, 16384, 65536), 7);
        Assert.Single(chunks);
        Assert.Equal(100, chunks[0].Length);
    }

    [Fact]
    public void Chunker_EmptyInput_YieldsNoChunks()
    {
        var chunks = ChunkAll(Array.Empty<byte>(), new CdcChunker(4096, 16384, 65536), 7);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunker_RejectsBadParameters()
    {
        // (min, avg, max) — each row violates exactly one rule.
        var bad = new (int min, int avg, int max)[]
        {
            (1024, 16384, 65536),   // min < 4 KiB
            (16384, 16384, 65536),   // avg <= min
            (4096, 16384, 16384),    // max <= avg
            (4096, 20000, 65536),    // avg not a power of two
        };
        foreach (var (min, avg, max) in bad)
            Assert.Throws<ArgumentOutOfRangeException>(() => new CdcChunker(min, avg, max));
        // Sanity: a valid triple does not throw.
        _ = new CdcChunker(4096, 16384, 65536);
    }

    // ---------- repository integration ----------

    private string FreshDir(string name) =>
        Path.Combine(_workDir, name + "-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void Init_Cdc_ValidatesSchemeAndSize()
    {
        string d1 = FreshDir("r1"), d2 = FreshDir("r2"), d3 = FreshDir("r3"), d4 = FreshDir("r4");
        // CDC needs a power-of-two chunk size >= 64 KiB (it is the target average).
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BackupRepository.Init(d1, Passphrase, 100000, KdfIterations, chunkingScheme: "cdc"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BackupRepository.Init(d2, Passphrase, 32768, KdfIterations, chunkingScheme: "cdc"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BackupRepository.Init(d3, Passphrase, 65536, KdfIterations, chunkingScheme: "bogus"));
        using (BackupRepository.Init(d4, Passphrase, 65536, KdfIterations, chunkingScheme: "CDC"))
        { /* scheme names are case-insensitive */ }
        string json = File.ReadAllText(Path.Combine(d4, "repo.json"));
        Assert.Contains("\"chunking_scheme\": \"cdc\"", json);
    }

    [Fact]
    public void Backup_Cdc_RoundTrip_RestoreMatches()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations, chunkingScheme: "cdc");
        WriteSrc("big.bin", SeededRandom(300 * 1024, 11));
        WriteSrc("small.txt", "hello cdc"u8.ToArray());
        WriteSrc("empty.dat", Array.Empty<byte>());

        var manifest = repo.BackupDirectory(_srcDir);
        Assert.Equal("cdc", manifest.Chunking);
        Assert.True(manifest.Files.Count == 3, "expected all three files in the manifest");

        string dest = Path.Combine(_workDir, "restored");
        repo.Restore(manifest.BackupId, dest);
        AssertDirectoriesEqual(_srcDir, dest);
        repo.Verify(manifest.BackupId); // must not throw
    }

    [Fact]
    public void Incremental_Cdc_InsertionUploadsOnlyNearbyChunks()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations, chunkingScheme: "cdc");
        byte[] data = SeededRandom(1024 * 1024, 21);
        WriteSrc("big.bin", data);
        var parent = repo.BackupDirectory(_srcDir);
        long before = repo.StoredChunkCount();

        // Insert 100 bytes mid-file: the catastrophic case for fixed-size chunking.
        const int at = 512 * 1024;
        byte[] insert = SeededRandom(100, 22);
        var edited = new byte[data.Length + insert.Length];
        Buffer.BlockCopy(data, 0, edited, 0, at);
        Buffer.BlockCopy(insert, 0, edited, at, insert.Length);
        Buffer.BlockCopy(data, at, edited, at + insert.Length, data.Length - at);
        WriteSrc("big.bin", edited);
        // Deterministic change detection regardless of FS timestamp granularity.
        File.SetLastWriteTimeUtc(
            Path.Combine(_srcDir, "big.bin"),
            parent.Files.Single(f => f.Path == "big.bin").MtimeUtc + TimeSpan.FromHours(1));

        var child = repo.BackupIncremental(_srcDir, parent.BackupId);
        long newChunks = repo.StoredChunkCount() - before;
        Assert.True(newChunks <= 5,
            $"CDC incremental uploaded {newChunks} new chunks for a 100-byte insertion; expected only nearby chunks");

        var parentIds = parent.Files.Single(f => f.Path == "big.bin").Chunks.ToHashSet();
        var childIds = child.Files.Single(f => f.Path == "big.bin").Chunks;
        double shared = (double)childIds.Count(parentIds.Contains) / childIds.Count;
        Assert.True(shared >= 0.7, $"only {shared:P0} of chunks shared with parent; CDC should preserve most");
    }

    [Fact]
    public void Incremental_Fixed_InsertionReuploadsShiftedChunks()
    {
        // Contrast test: documents the v1 behavior CDC replaces.
        using var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations,
            chunkingScheme: BackupRepository.ChunkingSchemeFixed);
        byte[] data = SeededRandom(1024 * 1024, 21);
        WriteSrc("big.bin", data);
        var parent = repo.BackupDirectory(_srcDir);
        Assert.Equal("fixed", parent.Chunking);
        long before = repo.StoredChunkCount();

        const int at = 512 * 1024;
        byte[] insert = SeededRandom(100, 22);
        var edited = new byte[data.Length + insert.Length];
        Buffer.BlockCopy(data, 0, edited, 0, at);
        Buffer.BlockCopy(insert, 0, edited, at, insert.Length);
        Buffer.BlockCopy(data, at, edited, at + insert.Length, data.Length - at);
        WriteSrc("big.bin", edited);
        File.SetLastWriteTimeUtc(
            Path.Combine(_srcDir, "big.bin"),
            parent.Files.Single(f => f.Path == "big.bin").MtimeUtc + TimeSpan.FromHours(1));

        repo.BackupIncremental(_srcDir, parent.BackupId);
        long newChunks = repo.StoredChunkCount() - before;
        Assert.True(newChunks >= 6,
            $"fixed-size incremental uploaded only {newChunks} new chunks; insertion should shift most downstream chunks");
    }

    [Fact]
    public void Incremental_RejectsSchemeMismatch()
    {
        string dir = FreshDir("mismatch");
        using (var repo = BackupRepository.Init(dir, Passphrase, ChunkSize, KdfIterations, chunkingScheme: "cdc"))
        {
            WriteSrc("a.txt", "v1"u8.ToArray());
            var parent = repo.BackupDirectory(_srcDir);

            // Simulate a repo.json whose scheme no longer matches the manifests
            // (e.g. hand-edited): the incremental must refuse, not silently
            // mix chunking schemes within one backup chain.
            string cfg = Path.Combine(dir, "repo.json");
            File.WriteAllText(cfg, File.ReadAllText(cfg).Replace("\"chunking_scheme\": \"cdc\"", "\"chunking_scheme\": \"fixed\""));

            using var reopened = BackupRepository.Open(dir, Passphrase);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                reopened.BackupIncremental(_srcDir, parent.BackupId));
            Assert.Contains("chunking", ex.Message);
        }
    }
}
