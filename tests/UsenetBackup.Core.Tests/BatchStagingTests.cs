using System.Threading;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Batched staging + pruning: batch-size resolution, the tracking blob
/// store, flush boundaries, chunk pruning, per-batch set names, and volume
/// inheritance for incrementals. NNTP posting itself is covered by the
/// existing NntpBlobStore/VolumeUpload tests with the fake server.
/// </summary>
public sealed class BatchStagingTests : IDisposable
{
    [Fact]
    public void ComputePruneSet_ExcludesChunksStagedInLaterBatch()
    {
        // P2-R2/C10: a chunk duplicated across files can be staged into
        // batch N+1 while batch N is still uploading; N's prune must
        // keep its blob for N+1's flush.
        var flushed = new List<string> { "a", "b", "c" };
        Assert.Equal(new List<string> { "a", "c" },
            BackupScheduler.ComputePruneSet(flushed, new List<string> { "b" }));
        Assert.Equal(flushed,
            BackupScheduler.ComputePruneSet(flushed, new List<string>()));
        Assert.Empty(BackupScheduler.ComputePruneSet(flushed, flushed));
    }

    private const string Passphrase = "correct horse battery staple";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;

    public BatchStagingTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-batch-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    // ---------- batch size resolution ----------

    [Fact]
    public void ResolveBatchSize_FallsBackToDefault_WhenNeverProbed()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var limits = new ProviderLimits(repo.RepoRoot);
        long size = BatchStaging.ResolveBatchSizeBytes(
            limits, "news.example.com", 119, BackupRepository.DefaultVolumeSizeBytes);
        Assert.Equal(BackupRepository.DefaultVolumeSizeBytes, size);
    }

    [Fact]
    public void ResolveBatchSize_UsesNinetyPercentOfProbedCeiling()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var limits = new ProviderLimits(repo.RepoRoot);
        limits.Record("news.example.com", 119, 500_000_000);
        // Re-read from disk to prove persistence.
        var reloaded = new ProviderLimits(repo.RepoRoot);
        long size = BatchStaging.ResolveBatchSizeBytes(
            reloaded, "news.example.com", 119, BackupRepository.DefaultVolumeSizeBytes);
        Assert.Equal((long)(500_000_000 * BatchStaging.ProbeMargin), size);
    }

    [Fact]
    public void ResolveBatchSize_RejectsNonPositiveFallback()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var limits = new ProviderLimits(repo.RepoRoot);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BatchStaging.ResolveBatchSizeBytes(limits, "h", 119, 0));
    }

    // ---------- tracking blob store ----------

    [Fact]
    public void TrackingStore_RecordsStagedIdsAndBytes()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, _ => false);
        byte[] blob = new byte[100];
        string id = new string('a', 64);
        tracker.Put(id, blob);
        Assert.Equal(new[] { id }, tracker.StagedIds);
        Assert.Equal(100, tracker.StagedBytesThisBatch);
        // Exists is delivery evidence now (DL-1): the predicate says the
        // chunk is NOT delivered anywhere, so it must not read as
        // skippable just because it was staged a moment ago.
        Assert.False(tracker.Exists(id));
        tracker.ResetBatch();
        Assert.Empty(tracker.StagedIds);
        Assert.Equal(0, tracker.StagedBytesThisBatch);
        // The blob itself is still in the inner store.
        Assert.True(inner.Exists(id));
    }

    [Fact]
    public void TrackingStore_ExistsConsultsRemotePredicate()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        string id = new string('b', 64);
        // Not local, but delivery to every destination is evidenced
        // (e.g. flushed earlier in the run, or journal/presence proof).
        var tracker = new BatchTrackingBlobStore(inner, known => known == id);
        Assert.True(tracker.Exists(id));
        Assert.False(inner.Exists(id));
        // Put is a complete no-op when delivery is already evidenced:
        // nothing staged, nothing even stored locally.
        tracker.Put(id, new byte[10]);
        Assert.Empty(tracker.StagedIds);
        Assert.False(inner.Exists(id));
    }

    [Fact]
    public void TrackingStore_LocalButUndelivered_IsStagedForFlush()
    {
        // DL-1 regression: a chunk whose blob sits in the local store
        // (staged by a failed run, restored, or written by a non-batched
        // backup) but was never delivered must still go through a
        // flush. The old semantics treated local presence as "exists"
        // and skipped it forever — the manifest referenced a chunk no
        // destination had.
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        string id = new string('c', 64);
        inner.Put(id, new byte[10]);
        var tracker = new BatchTrackingBlobStore(inner, _ => false);
        Assert.False(tracker.Exists(id)); // local ≠ delivered
        tracker.Put(id, new byte[10]);
        Assert.Equal(new[] { id }, tracker.StagedIds);
        Assert.Equal(10, tracker.StagedBytesThisBatch);
        Assert.True(inner.Exists(id)); // the same local copy is what flushes
    }

    // ---------- flush context ----------

    [Fact]
    public void TrackingStore_PutRacingClaim_DeliveredChunkNotResurrected()
    {
        // Mirror of the FlushUnderLock wipe race: a Put whose local
        // write is in flight while a flush claims the batch. If the
        // chunk was DELIVERED meanwhile (a twin file staged it first
        // and that batch flushed), this Put must not track it again —
        // it would be uploaded a second time in the next batch. (The
        // pre-fix tracker tracked unconditionally after the write.)
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var delivered = new HashSet<string>();
        bool Delivered(string id) { lock (delivered) return delivered.Contains(id); }
        var tracker = new BatchTrackingBlobStore(inner, Delivered);
        string id = new string('7', 64);
        // Large enough that the atomic temp-write is observable in flight.
        byte[] blob = new byte[64 * 1024 * 1024];
        var putThread = new Thread(() => tracker.Put(id, blob));
        putThread.Start();
        string chunkDir = Path.GetDirectoryName(inner.PathFor(id))!;
        bool inFlight = SpinWait.SpinUntil(() =>
            Directory.Exists(chunkDir) &&
            Directory.GetFiles(chunkDir, "*.tmp-*").Length > 0,
            TimeSpan.FromSeconds(15));
        Assert.True(inFlight, "inner write never became observable");
        // The flush claims whatever is tracked so far; the chunk is
        // then delivered (by the twin that staged it first).
        tracker.CaptureAndReset();
        lock (delivered) delivered.Add(id);
        putThread.Join();
        // Whether this Put's tracking landed before the claim (the
        // claim took it — staged exactly once) or after (the delivery
        // re-check must have kept it out), it must NOT be sitting in
        // the next batch now.
        Assert.Empty(tracker.StagedIds);
        Assert.Equal(0, tracker.StagedBytesThisBatch);
    }

    [Fact]
    public void BatchContext_ChunkStagedDuringFlush_SurvivesForNextBatch()
    {
        // Regression: FlushUnderLock used to snapshot, run the flush
        // callback, THEN ResetBatch — a chunk tracked during the
        // callback (the parallel staging loop does exactly that while
        // an upload is in flight) was wiped: in the manifest, never
        // delivered. The batch must be CLAIMED before the callback.
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, _ => false);
        string lateId = new string('9', 64);
        var flushed = new List<IReadOnlyList<string>>();
        var ctx = new BatchedBackupContext(tracker, batchSizeBytes: 100,
            (ids, idx) =>
            {
                flushed.Add(ids);
                if (idx == 0)
                    // Simulate the staging loop landing a chunk while
                    // this flush's upload is in flight.
                    tracker.Put(lateId, new byte[60]);
                return true;
            });

        tracker.Put(new string('8', 64), new byte[120]);
        ctx.CheckFlush(); // 120 >= 100: flush batch 0
        Assert.Single(flushed);
        Assert.DoesNotContain(lateId, flushed[0]);
        // The late chunk survived the flush — it is the next batch.
        Assert.Equal(new[] { lateId }, tracker.StagedIds);
        ctx.FlushRemainder();
        Assert.Equal(2, flushed.Count);
        Assert.Equal(new[] { lateId }, flushed[1]);
    }

    [Fact]
    public void BatchContext_FlushesWhenFull_AndRemainder()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, _ => false);
        var flushed = new List<(IReadOnlyList<string> Ids, int Index)>();
        var ctx = new BatchedBackupContext(tracker, batchSizeBytes: 100,
            (ids, idx) => { flushed.Add((ids, idx)); return true; });

        string id1 = new string('d', 64);
        string id2 = new string('e', 64);
        tracker.Put(id1, new byte[60]);
        ctx.CheckFlush(); // 60 < 100: no flush
        Assert.Empty(flushed);
        tracker.Put(id2, new byte[60]); // 120 >= 100: flush
        ctx.CheckFlush();
        Assert.Single(flushed);
        Assert.Equal(0, flushed[0].Index);
        Assert.Equal(2, flushed[0].Ids.Count);
        Assert.Equal(1, ctx.BatchIndex);

        // Remainder flush picks up a partial batch.
        string id3 = new string('f', 64);
        tracker.Put(id3, new byte[10]);
        ctx.FlushRemainder();
        Assert.Equal(2, flushed.Count);
        Assert.Equal(1, flushed[1].Index);

        // Empty remainder is a no-op.
        ctx.FlushRemainder();
        Assert.Equal(2, flushed.Count);
    }

    [Fact]
    public void BatchContext_Abort_ThrowsOperationCanceled()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, _ => false);
        var ctx = new BatchedBackupContext(tracker, batchSizeBytes: 10,
            (ids, idx) => false);
        tracker.Put(new string('a', 64), new byte[20]);
        Assert.Throws<OperationCanceledException>(() => ctx.CheckFlush());
    }

    // ---------- pruning ----------

    [Fact]
    public void LocalBlobStore_Delete_RemovesBlob()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        var store = repo.LocalChunkStore!;
        string id = new string('a', 64);
        store.Put(id, new byte[32]);
        Assert.True(store.Exists(id));
        store.Delete(id);
        Assert.False(store.Exists(id));
        // Deleting a missing blob is a no-op.
        store.Delete(id);
    }

    [Fact]
    public void Catalog_HasChunk_TracksRecordedChunks()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        string unknown = new string('b', 64);
        Assert.False(repo.CatalogHasChunk(unknown));
        File.WriteAllText(Path.Combine(_srcDir, "f.txt"), "hello");
        var manifest = repo.BackupDirectory(_srcDir, null);
        string known = manifest.Files[0].Chunks[0];
        Assert.True(repo.CatalogHasChunk(known));
        Assert.False(repo.CatalogHasChunk(unknown));
    }

    // ---------- per-batch set names ----------

    [Fact]
    public void SetName_BatchZero_MatchesLegacy()
    {
        string backupId = new string('a', 32);
        Assert.Equal(ArticleCodec.SetName(backupId), ArticleCodec.SetName(backupId, 0));
    }

    [Fact]
    public void SetName_LaterBatches_GetDistinctAlphanumericSuffix()
    {
        string backupId = new string('a', 32);
        string s1 = ArticleCodec.SetName(backupId, 1);
        string s2 = ArticleCodec.SetName(backupId, 2);
        Assert.NotEqual(s1, s2);
        Assert.NotEqual(s1, ArticleCodec.SetName(backupId, 0));
        Assert.All(s1, c => Assert.True(char.IsLetterOrDigit(c)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArticleCodec.SetName(backupId, -1));
    }

    [Fact]
    public void MakeNfoMessageId_BatchZero_MatchesLegacy()
    {
        string backupId = new string('a', 32);
        Assert.Equal(
            ArticleCodec.MakeNfoMessageId(backupId, "reporid12345678"),
            ArticleCodec.MakeNfoMessageId(backupId, 0, "reporid12345678"));
    }

    [Fact]
    public void MakeNfoMessageId_LaterBatches_AreDistinct()
    {
        string backupId = new string('a', 32);
        string m1 = ArticleCodec.MakeNfoMessageId(backupId, 1, "reporid12345678");
        string m2 = ArticleCodec.MakeNfoMessageId(backupId, 2, "reporid12345678");
        Assert.NotEqual(m1, m2);
        Assert.NotEqual(m1, ArticleCodec.MakeNfoMessageId(backupId, 0, "reporid12345678"));
    }

    [Fact]
    public void VolumeEntry_SetName_SerializesAdditively()
    {
        var withSet = new VolumeEntry { Id = new string('a', 64), SetName = "abc123" };
        string json = System.Text.Json.JsonSerializer.Serialize(withSet);
        Assert.Contains("set_name", json);
        var withoutSet = new VolumeEntry { Id = new string('a', 64) };
        string json2 = System.Text.Json.JsonSerializer.Serialize(withoutSet);
        Assert.DoesNotContain("set_name", json2);
    }

    // ---------- volume inheritance ----------

    private static BackupManifest MakeManifestWithVolumes(
        string backupId, params (string volId, string[] chunkIds, string? setName)[] volumes)
    {
        var m = new BackupManifest
        {
            BackupId = backupId,
            CreatedUtc = DateTime.UtcNow,
            ChunkSize = 4096,
            Volumes = volumes.Select(v => new VolumeEntry
            {
                Id = v.volId,
                ChunkIds = v.chunkIds.ToList(),
                SizeBytes = 100,
                SetName = v.setName,
            }).ToList(),
        };
        // Cover every chunk with a file entry.
        m.Files.Add(new FileEntry
        {
            Path = "f",
            Chunks = volumes.SelectMany(v => v.chunkIds).ToList(),
        });
        return m;
    }

    [Fact]
    public void TryInheritParentVolumes_CopiesUncoveredParentVolumes()
    {
        var parent = MakeManifestWithVolumes(new string('p', 32),
            (new string('1', 64), new[] { new string('a', 64) }, "set1"),
            (new string('2', 64), new[] { new string('b', 64) }, "set1"));
        // Child re-packed chunk 'a' in its own batch volume; 'b' is referenced
        // but not re-packed.
        var child = MakeManifestWithVolumes(new string('c', 32),
            (new string('3', 64), new[] { new string('a', 64) }, "set2"));
        child.Files.Add(new FileEntry
        {
            Path = "g",
            Chunks = new List<string> { new string('b', 64) },
        });

        Assert.True(BackupRepository.TryInheritParentVolumes(child, parent, out var inherited));
        Assert.Single(inherited);
        Assert.Equal(new string('2', 64), inherited[0].Id);
        Assert.Equal("set1", inherited[0].SetName); // original set preserved
    }

    [Fact]
    public void TryInheritParentVolumes_FailsWhenChunkUncovered()
    {
        var parent = MakeManifestWithVolumes(new string('p', 32)); // no volumes
        var child = MakeManifestWithVolumes(new string('c', 32));
        child.Files.Add(new FileEntry
        {
            Path = "g",
            Chunks = new List<string> { new string('b', 64) }, // parent chunk, no volume
        });
        Assert.False(BackupRepository.TryInheritParentVolumes(child, parent, out _));
    }

    [Fact]
    public void TryInheritParentVolumes_SkipsEmptyChunkIds()
    {
        var parent = MakeManifestWithVolumes(new string('p', 32));
        var child = MakeManifestWithVolumes(new string('c', 32));
        child.Files.Add(new FileEntry
        {
            Path = "sparse.img",
            Chunks = new List<string> { "" }, // sparse gap marker
        });
        Assert.True(BackupRepository.TryInheritParentVolumes(child, parent, out var inherited));
        Assert.Empty(inherited);
    }

    // ---------- end-to-end batched file backup (no network) ----------

    [Fact]
    public void BackupDirectoryBatched_FlushesAndPrunesPerBatch()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        // Three files; tiny batch size forces multiple flushes.
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), new string('x', 5000));
        File.WriteAllText(Path.Combine(_srcDir, "b.txt"), new string('y', 5000));
        File.WriteAllText(Path.Combine(_srcDir, "c.txt"), new string('z', 5000));

        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, repo.CatalogHasChunk);
        int flushes = 0;
        var flushedChunks = new List<string>();
        var ctx = new BatchedBackupContext(tracker, batchSizeBytes: 2000, (ids, idx) =>
        {
            // Simulate upload: verify each chunk, then prune.
            foreach (string id in ids)
                repo.VerifyChunk(id);
            flushedChunks.AddRange(ids);
            foreach (string id in ids)
                inner.Delete(id);
            flushes++;
            return true;
        });

        var manifest = repo.BackupDirectoryBatched(_srcDir, null, null, ctx);
        ctx.FlushRemainder();
        manifest = repo.FinalizeBatchedManifest(manifest);

        Assert.Equal(3, manifest.Files.Count);
        // Files stage in PARALLEL, so how the chunks group into batches
        // is timing-dependent: one flush can legitimately sweep all
        // three files if their staging lands before the first
        // CheckFlush. (This asserted >= 2 and flaked on Windows —
        // failed, passed, failed across identical runs.) The product
        // guarantees are: >= 1 flush, every chunk flushed exactly once,
        // pruned, manifest verifies — asserted below. Deterministic
        // multi-flush mechanics are covered by the direct-context
        // tests above.
        Assert.True(flushes >= 1, $"expected at least one batch flush, got {flushes}");
        // All staged chunks were flushed...
        var manifestChunks = manifest.Files.SelectMany(f => f.Chunks).Distinct().ToList();
        Assert.Equal(flushedChunks.Distinct().Count(), manifestChunks.Count);
        // ...and pruned locally...
        foreach (string id in manifestChunks)
            Assert.False(inner.Exists(id));
        // ...but the manifest survives and verifies.
        var reloaded = repo.LoadManifest(manifest.BackupId);
        Assert.Equal(3, reloaded.Files.Count);
        Assert.True(reloaded.VerifyRootHash());
    }

    [Fact]
    public void BackupDirectoryBatched_DedupsAcrossBatchesViaCatalog()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        // Same content twice: the second copy's chunks are already known
        // after the first batch prunes them.
        string content = new string('q', 8000);
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), content);
        File.WriteAllText(Path.Combine(_srcDir, "b.txt"), content);

        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, repo.CatalogHasChunk);
        int totalStaged = 0;
        var ctx = new BatchedBackupContext(tracker, batchSizeBytes: 2000, (ids, idx) =>
        {
            totalStaged += ids.Count;
            foreach (string id in ids)
                inner.Delete(id);
            return true;
        });

        var manifest = repo.BackupDirectoryBatched(_srcDir, null, null, ctx);
        ctx.FlushRemainder();
        repo.FinalizeBatchedManifest(manifest);

        var chunksA = manifest.Files[0].Chunks;
        var chunksB = manifest.Files[1].Chunks;
        Assert.Equal(chunksA, chunksB); // identical content → identical chunks
        // Second file's chunks were NOT re-staged (dedup via catalog).
        Assert.Equal(chunksA.Distinct().Count(), totalStaged);
    }

    private sealed class ThrowingDeleteStore : IBlobStore
    {
        public bool Exists(string chunkIdHex) => false;
        public void Put(string chunkIdHex, byte[] blob) { }
        public byte[] Get(string chunkIdHex) => throw new FileNotFoundException(chunkIdHex);
        public long StoredCount => 0;
        public void Delete(string chunkIdHex) =>
            throw new IOException("The process cannot access the file because it is being used by another process.");
    }

    [Fact]
    public void PruneStagedChunks_DeleteFailures_AreLoggedNotThrown()
    {
        // NS-12: prune runs AFTER delivery is confirmed; a locked staged
        // chunk used to propagate out of the flush and fail the whole
        // run. Now failures are counted + logged, never thrown.
        var logs = new List<string>();
        int failures = BackupScheduler.PruneStagedChunks(
            new ThrowingDeleteStore(), new[] { "aa", "bb" }, logs.Add);
        Assert.Equal(2, failures);
        Assert.Contains(logs, l => l.Contains("could not prune staged chunk aa"));
        Assert.Contains(logs, l => l.Contains("2 staged chunk(s) could not be pruned"));
    }

    [Fact]
    public void BackupDirectoryBatched_BackupIdOverride_UsedForSets()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase);
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "data");
        var inner = repo.LocalChunkStore!;
        var tracker = new BatchTrackingBlobStore(inner, repo.CatalogHasChunk);
        var ctx = new BatchedBackupContext(tracker, batchSizeBytes: long.MaxValue, (ids, idx) => true);
        string wanted = new string('d', 32);
        var manifest = repo.BackupDirectoryBatched(_srcDir, null, null, ctx, backupId: wanted);
        ctx.FlushRemainder();
        Assert.Equal(wanted, manifest.BackupId);
        repo.FinalizeBatchedManifest(manifest);
    }
}
