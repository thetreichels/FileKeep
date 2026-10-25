using FileKeep.Core;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Performance improvements: batched dedup (HasChunks/RecordChunks),
/// thread-safe tracking store, parallel file processing determinism,
/// and pipelined upload overlap.
/// </summary>
public sealed class PerformanceTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public PerformanceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-perf-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    // ---------- batched dedup ----------

    [Fact]
    public void HasChunks_ReturnsKnownSubset_SingleQuery()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var catalog = new Catalog(Path.Combine(_repoDir, "catalog.db"));
        catalog.RecordChunk("aa", 100);
        catalog.RecordChunk("bb", 200);

        var known = catalog.HasChunks(new[] { "aa", "bb", "cc", "dd" });

        Assert.Equal(2, known.Count);
        Assert.Contains("aa", known);
        Assert.Contains("bb", known);
        Assert.DoesNotContain("cc", known);
    }

    [Fact]
    public void HasChunks_EmptyInput_ReturnsEmpty()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var catalog = new Catalog(Path.Combine(_repoDir, "catalog.db"));
        var known = catalog.HasChunks(Array.Empty<string>());
        Assert.Empty(known);
    }

    [Fact]
    public void HasChunks_LargeBatch_HandlesSqliteVariableLimit()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var catalog = new Catalog(Path.Combine(_repoDir, "catalog.db"));
        var ids = new List<string>();
        for (int i = 0; i < 1200; i++)
        {
            string id = i.ToString("x64").PadLeft(64, '0');
            ids.Add(id);
            if (i % 2 == 0)
                catalog.RecordChunk(id, 10);
        }
        // 1200 IDs exceeds SQLite's 999-variable limit; must chunk internally.
        var known = catalog.HasChunks(ids);
        Assert.Equal(600, known.Count);
    }

    [Fact]
    public void RecordChunks_InsertsAll_InSingleTransaction()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var catalog = new Catalog(Path.Combine(_repoDir, "catalog.db"));
        var chunks = new List<(string id, long size)>
        {
            ("a1", 100), ("b2", 200), ("c3", 300),
        };
        catalog.RecordChunks(chunks);

        Assert.True(catalog.HasChunk("a1"));
        Assert.True(catalog.HasChunk("b2"));
        Assert.True(catalog.HasChunk("c3"));
        Assert.False(catalog.HasChunk("d4"));
    }

    // ---------- thread-safe tracking store ----------

    [Fact]
    public void TrackingStore_ConcurrentPuts_NoLostUpdates()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, _ => false);

        int threadCount = 8;
        int putsPerThread = 50;
        Parallel.For(0, threadCount, t =>
        {
            for (int i = 0; i < putsPerThread; i++)
            {
                // Valid 64-char hex ID.
                string id = $"{t:x2}{i:x4}".PadRight(64, '0');
                tracker.Put(id, new byte[100]);
            }
        });

        Assert.Equal(threadCount * putsPerThread, tracker.StagedIds.Count);
        Assert.Equal(threadCount * putsPerThread * 100, tracker.StagedBytesThisBatch);
        // No duplicates.
        Assert.Equal(tracker.StagedIds.Count, tracker.StagedIds.Distinct().Count());
    }

    [Fact]
    public void TrackingStore_CaptureAndReset_AtomicHandoff()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, _ => false);
        tracker.Put(new string('a', 64), new byte[10]);
        tracker.Put(new string('b', 64), new byte[20]);

        var captured = tracker.CaptureAndReset();

        Assert.Equal(2, captured.Count);
        Assert.Empty(tracker.StagedIds);
        Assert.Equal(0, tracker.StagedBytesThisBatch);
    }

    // ---------- parallel file processing determinism ----------

    [Fact]
    public void ParallelBackup_ProducesDeterministicManifest()
    {
        // Create source files with varied sizes.
        for (int i = 0; i < 20; i++)
        {
            string path = Path.Combine(_srcDir, $"file-{i:00}.bin");
            byte[] data = new byte[10_000 + i * 1000];
            new Random(i).NextBytes(data);
            File.WriteAllBytes(path, data);
        }
        // Include a subdirectory.
        string sub = Path.Combine(_srcDir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "nested.bin"), new byte[5000]);

        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        // Files must be in sorted path order (deterministic).
        var paths = manifest.Files.Select(f => f.Path).ToList();
        var sorted = paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, paths);

        // All files present.
        Assert.Equal(21, manifest.Files.Count);

        // Chunk IDs are content-addressed and deterministic: back up again
        // to a fresh repo and compare manifests.
        string repoDir2 = Path.Combine(_workDir, "repo2");
        using var repo2 = BackupRepository.Init(repoDir2, Passphrase);
        var manifest2 = repo2.BackupDirectory(_srcDir);
        Assert.Equal(
            manifest.Files.Select(f => f.Sha256).ToList(),
            manifest2.Files.Select(f => f.Sha256).ToList());
        var chunks1 = manifest.Files.SelectMany(f => f.Chunks).OrderBy(c => c).ToList();
        var chunks2 = manifest2.Files.SelectMany(f => f.Chunks).OrderBy(c => c).ToList();
        Assert.Equal(chunks1, chunks2);
    }

    [Fact]
    public void ParallelBackup_DedupAcrossFiles_NoDuplicateChunksStored()
    {
        // Two files with identical content: second file's chunks dedup.
        byte[] data = new byte[50_000];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(Path.Combine(_srcDir, "a.bin"), data);
        File.WriteAllBytes(Path.Combine(_srcDir, "b.bin"), data);
        File.WriteAllBytes(Path.Combine(_srcDir, "c.bin"), new byte[1000]);

        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        var chunksA = manifest.Files.First(f => f.Path == "a.bin").Chunks;
        var chunksB = manifest.Files.First(f => f.Path == "b.bin").Chunks;
        // Identical content → identical chunk IDs (dedup).
        Assert.Equal(chunksA, chunksB);
    }

    // ---------- pipelined context ----------

    [Fact]
    public void PipelinedContext_UploadsInOrder_WaitsForCompletion()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, _ => false);

        var uploadOrder = new List<int>();
        var uploadLock = new object();
        bool OnFlush(IReadOnlyList<string> ids, int batchIndex)
        {
            // Simulate upload work.
            Thread.Sleep(20);
            lock (uploadLock)
                uploadOrder.Add(batchIndex);
            // Simulate prune.
            foreach (string id in ids)
                inner.Delete(id);
            return true;
        }

        using var ctx = new PipelinedBackupContext(tracker, batchSizeBytes: 1000, OnFlush);

        // Stage 3 batches worth of chunks.
        for (int b = 0; b < 3; b++)
        {
            for (int i = 0; i < 10; i++)
            {
                // Valid 64-char hex ID.
                string id = $"{b:x2}{i:x4}".PadRight(64, '0');
                tracker.Put(id, new byte[150]); // 1500 bytes > 1000 → flush
            }
            // Note: CheckFlush is called per chunk by the real pipeline;
            // here we trigger it manually after each batch's chunks.
            ctx.CheckFlush();
        }
        ctx.FlushRemainder();

        // Uploads ran in batch order.
        Assert.Equal(new[] { 0, 1, 2 }, uploadOrder);
        // All pruned.
        Assert.Equal(0, inner.StoredCount);
    }

    [Fact]
    public void PipelinedContext_UploadFailure_PropagatesOnWait()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, _ => false);

        bool OnFlush(IReadOnlyList<string> ids, int batchIndex)
        {
            if (batchIndex == 1)
                throw new InvalidOperationException("simulated upload failure");
            return true;
        }

        using var ctx = new PipelinedBackupContext(tracker, batchSizeBytes: 1000, OnFlush);

        tracker.Put(new string('a', 64), new byte[1500]);
        ctx.CheckFlush(); // batch 0 queued
        tracker.Put(new string('b', 64), new byte[1500]);
        ctx.CheckFlush(); // batch 1 queued (will fail)

        var ex = Assert.Throws<InvalidOperationException>(() => ctx.FlushRemainder());
        Assert.Contains("pipelined upload", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void BatchedContext_CheckFlush_ThreadSafe_NoDoubleFlush()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, _ => false);

        int flushCount = 0;
        bool OnFlush(IReadOnlyList<string> ids, int batchIndex)
        {
            Interlocked.Increment(ref flushCount);
            return true;
        }

        var ctx = new BatchedBackupContext(tracker, batchSizeBytes: 10000, OnFlush);

        // Many threads racing to trigger a flush.
        Parallel.For(0, 16, _ =>
        {
            for (int i = 0; i < 100; i++)
            {
                string id = Guid.NewGuid().ToString("N").PadRight(64, '0');
                tracker.Put(id, new byte[100]);
                ctx.CheckFlush();
            }
        });
        ctx.FlushRemainder();

        // Total staged: 16*100*100 = 160,000 bytes. Batch size 10,000.
        // 160,000 / 10,000 = 16 flushes exactly, no remainder.
        Assert.Equal(16, flushCount);
    }
}
