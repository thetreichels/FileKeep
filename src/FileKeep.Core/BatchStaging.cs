using FileKeep.Core.Nntp;

namespace FileKeep.Core;

/// <summary>
/// Batched staging for backups: stage one batch of chunk blobs locally,
/// upload the batch, confirm, prune the batch's local chunks, repeat.
/// Manifests, the catalog, and the upload journal are never pruned — only
/// chunk blobs under chunks/ are deleted, and only once the batch is
/// confirmed delivered to at least one destination (a run where no
/// destination confirms a batch fails and prunes nothing). A destination
/// that misses a batch is excluded for the rest of the run and gets
/// nothing later — no catch-up; the backup itself fails unless at least
/// one location receives it completely (DL-3/DL-5 policy).
/// </summary>
public static class BatchStaging
{
    /// <summary>
    /// Safety margin applied to the provider's probed article ceiling when
    /// deriving the staging batch size (yEnc inflation + headers).
    /// </summary>
    public const double ProbeMargin = 0.9;

    /// <summary>
    /// Resolves the staging batch size in bytes: 90% of the provider's
    /// probed max article size when known, else <paramref name="fallbackBytes"/>
    /// (the repo's 100 MB default volume size).
    /// </summary>
    public static long ResolveBatchSizeBytes(
        ProviderLimits limits, string host, int port, long fallbackBytes)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (fallbackBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(fallbackBytes));
        long? probed = limits.GetMaxArticleBytes(host, port);
        if (probed.HasValue && probed.Value > 0)
            return Math.Max(1, (long)(probed.Value * ProbeMargin));
        return fallbackBytes;
    }
}

/// <summary>
/// Abstraction over batched backup orchestration. Implemented by
/// <see cref="BatchedBackupContext"/> (synchronous flush) and
/// <see cref="PipelinedBackupContext"/> (overlapped staging/upload).
/// </summary>
public interface IBatchedBackupContext
{
    /// <summary>The tracking store chunks are staged through.</summary>
    BatchTrackingBlobStore Tracker { get; }

    /// <summary>
    /// Called after staged chunks. Flushes when the batch is full.
    /// Throws on abort or prior upload failure.
    /// </summary>
    void CheckFlush();

    /// <summary>
    /// Flushes the final partial batch and waits for completion.
    /// </summary>
    void FlushRemainder();
}

/// <summary>
/// Orchestrates flush boundaries during a batched backup. The backup loop
/// stages chunks through <see cref="Tracker"/>; after each chunk,
/// <see cref="CheckFlush"/> runs <see cref="OnFlushBatch"/> (upload →
/// confirm → prune) when the staged bytes reach <see cref="BatchSizeBytes"/>.
/// The callback returns false to abort the backup.
/// Thread-safe: multiple staging threads may call <see cref="CheckFlush"/>
/// concurrently; flushes are serialized.
/// </summary>
public sealed class BatchedBackupContext : IBatchedBackupContext
{
    private int _batchIndex;
    private readonly object _flushLock = new();

    public BatchedBackupContext(
        BatchTrackingBlobStore tracker,
        long batchSizeBytes,
        Func<IReadOnlyList<string>, int, bool> onFlushBatch)
    {
        Tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        if (batchSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSizeBytes));
        BatchSizeBytes = batchSizeBytes;
        OnFlushBatch = onFlushBatch ?? throw new ArgumentNullException(nameof(onFlushBatch));
    }

    public BatchTrackingBlobStore Tracker { get; }

    public long BatchSizeBytes { get; }

    public Func<IReadOnlyList<string>, int, bool> OnFlushBatch { get; }

    /// <summary>Index of the batch currently being staged (0-based).</summary>
    public int BatchIndex => _batchIndex;

    /// <summary>
    /// Called after each staged chunk. Flushes when the batch is full.
    /// Throws <see cref="OperationCanceledException"/> if the flush
    /// callback aborted the backup. Thread-safe.
    /// </summary>
    public void CheckFlush()
    {
        // Fast path: avoid lock when clearly under threshold.
        if (Tracker.StagedBytesThisBatch < BatchSizeBytes)
            return;
        lock (_flushLock)
        {
            if (Tracker.StagedBytesThisBatch < BatchSizeBytes)
                return;
            FlushUnderLock();
        }
    }

    /// <summary>
    /// Flushes the current batch even if it is under the batch size
    /// (end of backup). No-op when nothing was staged. Thread-safe.
    /// </summary>
    public void FlushRemainder()
    {
        lock (_flushLock)
        {
            if (Tracker.StagedIds.Count == 0)
                return;
            FlushUnderLock();
        }
    }

    private void Flush()
    {
        lock (_flushLock)
            FlushUnderLock();
    }

    private void FlushUnderLock()
    {
        // Claim the batch ATOMICALLY, before the callback. The old
        // order — snapshot StagedIds, run the (slow: verify + upload +
        // prune) callback, then ResetBatch — wiped any chunk the
        // parallel staging loop tracked DURING the callback: staged
        // and catalogued, referenced by the manifest, but never
        // flushed to a destination and never pruned. A silent hole in
        // the backup, visible only under load. Chunks tracked during
        // the callback now simply belong to the next batch.
        var ids = Tracker.CaptureAndReset();
        bool ok = OnFlushBatch(ids, _batchIndex);
        if (!ok)
            throw new OperationCanceledException("Batched backup aborted during flush.");
        _batchIndex++;
    }
}

/// <summary>
/// Pipelined variant of <see cref="BatchedBackupContext"/>: overlaps staging
/// with upload. When a batch fills, <see cref="CheckFlush"/> captures the
/// batch's chunk IDs, resets tracking, and queues the upload to a background
/// task — staging of the next batch continues immediately. Uploads run
/// serially in batch order (verify → upload → prune per batch).
/// <see cref="FlushRemainder"/> (or <see cref="WaitForCompletion"/>) blocks
/// until all queued uploads finish and propagates the first failure.
/// Memory/disk stay bounded: at most ~2 batches are staged locally (one
/// uploading, one staging).
/// </summary>
public sealed class PipelinedBackupContext : IBatchedBackupContext, IDisposable
{
    private readonly BatchedBackupContext _inner;
    private readonly Func<IReadOnlyList<string>, int, bool> _onFlushBatch;
    private readonly object _queueLock = new();
    private readonly Queue<Task> _pendingUploads = new();
    private Task _tail = Task.CompletedTask;
    private Exception? _firstError;
    private bool _disposed;

    public PipelinedBackupContext(
        BatchTrackingBlobStore tracker,
        long batchSizeBytes,
        Func<IReadOnlyList<string>, int, bool> onFlushBatch)
    {
        _onFlushBatch = onFlushBatch ?? throw new ArgumentNullException(nameof(onFlushBatch));
        // The inner context's OnFlushBatch captures+queues instead of running inline.
        _inner = new BatchedBackupContext(tracker, batchSizeBytes, QueueFlush);
    }

    public BatchTrackingBlobStore Tracker => _inner.Tracker;

    public long BatchSizeBytes => _inner.BatchSizeBytes;

    public int BatchIndex => _inner.BatchIndex;

    /// <summary>
    /// Called after each staged chunk. When the batch is full, hands it to
    /// the background uploader and returns immediately. Thread-safe.
    /// Throws if a prior upload failed.
    /// </summary>
    public void CheckFlush()
    {
        ThrowIfUploadFailed();
        _inner.CheckFlush();
    }

    /// <summary>
    /// Queues the final partial batch and waits for all uploads to complete.
    /// Throws the first upload failure, if any.
    /// </summary>
    public void FlushRemainder()
    {
        _inner.FlushRemainder();
        WaitForCompletion();
    }

    /// <summary>
    /// Blocks until all queued uploads finish. Throws the first failure.
    /// </summary>
    public void WaitForCompletion()
    {
        Task[] pending;
        lock (_queueLock)
            pending = _pendingUploads.ToArray();
        try
        {
            Task.WaitAll(pending);
        }
        catch
        {
            // Fall through to throw the first error below.
        }
        ThrowIfUploadFailed();
    }

    private bool QueueFlush(IReadOnlyList<string> chunkIds, int batchIndex)
    {
        // Capture IDs and reset tracking atomically so staging continues.
        // (The inner context already did this via FlushUnderLock; we just
        // need to queue the work.)
        var idsCopy = chunkIds.ToList();
        lock (_queueLock)
        {
            if (_firstError is not null)
                return false;
            // Chain onto the tail to preserve batch order.
            _tail = _tail.ContinueWith(_ =>
            {
                try
                {
                    return _onFlushBatch(idsCopy, batchIndex);
                }
                catch (Exception ex)
                {
                    lock (_queueLock)
                        _firstError ??= ex;
                    return false;
                }
            }, TaskScheduler.Default);
            _pendingUploads.Enqueue(_tail);
        }
        return true;
    }

    private void ThrowIfUploadFailed()
    {
        lock (_queueLock)
        {
            if (_firstError is not null)
                throw new InvalidOperationException(
                    "Batched backup failed during pipelined upload.", _firstError);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { WaitForCompletion(); }
        catch { /* Dispose must not throw; error already recorded */ }
    }
}

/// <summary>
/// <see cref="IBlobStore"/> decorator over <see cref="LocalBlobStore"/> that
/// records which chunk IDs (and how many bytes) were staged since the last
/// <see cref="ResetBatch"/>. The backup loop stages through this store; the
/// batch orchestrator flushes (upload → confirm → prune) when
/// <see cref="StagedBytesThisBatch"/> reaches the batch size.
///
/// Deduplication consults <paramref name="isDeliveredAtAllTargets"/>: a
/// chunk is skipped only when the caller can evidence that every
/// destination of this run already has it (flushed earlier in this run,
/// journal-recorded for that provider, or present at that share/drive).
/// Mere local presence or catalog knowledge is NOT delivery evidence
/// (DL-1): a chunk staged by a failed run, or delivered to a different
/// destination, must stage and flush again or the manifest ends up
/// referencing a chunk some destination never received.
/// </summary>
public sealed class BatchTrackingBlobStore : IBlobStore
{
    private readonly LocalBlobStore _inner;
    private readonly Func<string, bool> _isDeliveredAtAllTargets;
    private readonly List<string> _stagedIds = new();
    private readonly object _lock = new();
    private long _stagedBytes;
    // Bumped by every claim/reset. A Put snapshots it before its
    // (unlocked) write; if a flush claimed a batch while the write was
    // in flight, the Put re-checks delivery evidence before tracking —
    // otherwise it would resurrect a chunk the flush already delivered
    // into the next batch (double upload), the mirror image of the
    // wipe race CaptureAndReset fixed in FlushUnderLock.
    private int _claimEpoch;

    public BatchTrackingBlobStore(LocalBlobStore inner, Func<string, bool> isDeliveredAtAllTargets)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _isDeliveredAtAllTargets = isDeliveredAtAllTargets
            ?? throw new ArgumentNullException(nameof(isDeliveredAtAllTargets));
    }

    /// <summary>Chunk IDs staged since the last <see cref="ResetBatch"/>.</summary>
    public IReadOnlyList<string> StagedIds
    {
        get { lock (_lock) return _stagedIds.ToList(); }
    }

    /// <summary>Blob bytes staged since the last <see cref="ResetBatch"/>.</summary>
    public long StagedBytesThisBatch
    {
        get { lock (_lock) return _stagedBytes; }
    }

    /// <summary>
    /// For the tracking store, "exists" answers the staging question —
    /// may this chunk be skipped? — and the only sound yes is delivery
    /// evidence for every destination of this run (DL-1). Local presence
    /// alone does not skip: the blob still has to go through a flush.
    /// </summary>
    public bool Exists(string chunkIdHex) =>
        _isDeliveredAtAllTargets(chunkIdHex);

    public void Put(string chunkIdHex, byte[] blob)
    {
        // P2-C11: the delivery-evidence check does REMOTE IO (SMB
        // decrypt-verify per DL-1/DL-4) — it used to run while holding
        // _lock, serializing every staging thread behind one chunk's
        // network round-trips. The lock now guards only the staged-set
        // bookkeeping; the local write is idempotent (dedup Put), so
        // concurrent Puts of the same chunk are harmless.
        if (_isDeliveredAtAllTargets(chunkIdHex))
            return; // evidenced at every destination: nothing to stage or send
        int epoch = ClaimEpoch;
        if (!_inner.Exists(chunkIdHex))
            _inner.Put(chunkIdHex, blob);
        Track(chunkIdHex, blob.Length, epoch);
    }

    public void Put(string chunkIdHex, ReadOnlySpan<byte> blob)
    {
        if (_isDeliveredAtAllTargets(chunkIdHex))
            return; // evidenced at every destination: nothing to stage or send
        int epoch = ClaimEpoch;
        if (!_inner.Exists(chunkIdHex))
            _inner.Put(chunkIdHex, blob);
        Track(chunkIdHex, blob.Length, epoch);
    }

    private int ClaimEpoch
    {
        get { lock (_lock) return _claimEpoch; }
    }

    private void Track(string chunkIdHex, long blobLength, int epochAtWriteStart)
    {
        lock (_lock)
        {
            if (_stagedIds.Contains(chunkIdHex))
                return;
            // A flush claimed a batch while this Put's write was in
            // flight. If that flush DELIVERED this chunk (a twin file
            // staged it first), tracking it now would resurrect it
            // into the next batch and upload it twice. If it was not
            // delivered, it is a genuinely new chunk and must be
            // tracked — dropping it here was the wipe race.
            if (epochAtWriteStart != _claimEpoch &&
                _isDeliveredAtAllTargets(chunkIdHex))
                return;
            _stagedIds.Add(chunkIdHex);
            _stagedBytes += blobLength;
        }
    }

    public byte[] Get(string chunkIdHex) => _inner.Get(chunkIdHex);

    public long StoredCount => _inner.StoredCount;

    /// <summary>Clears the per-batch tracking after a flush (upload → prune).</summary>
    public void ResetBatch()
    {
        lock (_lock)
        {
            _stagedIds.Clear();
            _stagedBytes = 0;
            _claimEpoch++;
        }
    }

    /// <summary>
    /// Atomically captures the current batch's staged IDs and resets tracking.
    /// Used by pipelined flush to hand off a batch without blocking staging.
    /// </summary>
    internal List<string> CaptureAndReset()
    {
        lock (_lock)
        {
            var ids = new List<string>(_stagedIds);
            _stagedIds.Clear();
            _stagedBytes = 0;
            _claimEpoch++;
            return ids;
        }
    }
}
