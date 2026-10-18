using UsenetBackup.Core.Nntp;

namespace UsenetBackup.Core;

/// <summary>
/// Batched staging for backups: stage one batch of chunk blobs locally,
/// upload the batch, confirm, prune the batch's local chunks, repeat.
/// Manifests, the catalog, and the upload journal are never pruned — only
/// chunk blobs under chunks/ are deleted, and only after the batch's upload
/// is confirmed at every destination.
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
/// Orchestrates flush boundaries during a batched backup. The backup loop
/// stages chunks through <see cref="Tracker"/>; after each chunk,
/// <see cref="CheckFlush"/> runs <see cref="OnFlushBatch"/> (upload →
/// confirm → prune) when the staged bytes reach <see cref="BatchSizeBytes"/>.
/// The callback returns false to abort the backup.
/// </summary>
public sealed class BatchedBackupContext
{
    private int _batchIndex;

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
    /// callback aborted the backup.
    /// </summary>
    public void CheckFlush()
    {
        if (Tracker.StagedBytesThisBatch < BatchSizeBytes)
            return;
        Flush();
    }

    /// <summary>
    /// Flushes the current batch even if it is under the batch size
    /// (end of backup). No-op when nothing was staged.
    /// </summary>
    public void FlushRemainder()
    {
        if (Tracker.StagedIds.Count == 0)
            return;
        Flush();
    }

    private void Flush()
    {
        var ids = Tracker.StagedIds.ToList();
        bool ok = OnFlushBatch(ids, _batchIndex);
        Tracker.ResetBatch();
        if (!ok)
            throw new OperationCanceledException("Batched backup aborted during flush.");
        _batchIndex++;
    }
}

/// <summary>
/// <see cref="IBlobStore"/> decorator over <see cref="LocalBlobStore"/> that
/// records which chunk IDs (and how many bytes) were staged since the last
/// <see cref="ResetBatch"/>. The backup loop stages through this store; the
/// batch orchestrator flushes (upload → confirm → prune) when
/// <see cref="StagedBytesThisBatch"/> reaches the batch size.
///
/// Deduplication consults <paramref name="isKnownRemotely"/> (the catalog)
/// in addition to the local store, so chunks uploaded and pruned by an
/// earlier batch are not re-staged: a known chunk was either staged in the
/// current batch or confirmed-uploaded earlier.
/// </summary>
public sealed class BatchTrackingBlobStore : IBlobStore
{
    private readonly LocalBlobStore _inner;
    private readonly Func<string, bool> _isKnownRemotely;
    private readonly List<string> _stagedIds = new();
    private long _stagedBytes;

    public BatchTrackingBlobStore(LocalBlobStore inner, Func<string, bool> isKnownRemotely)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _isKnownRemotely = isKnownRemotely ?? throw new ArgumentNullException(nameof(isKnownRemotely));
    }

    /// <summary>Chunk IDs staged since the last <see cref="ResetBatch"/>.</summary>
    public IReadOnlyList<string> StagedIds => _stagedIds;

    /// <summary>Blob bytes staged since the last <see cref="ResetBatch"/>.</summary>
    public long StagedBytesThisBatch => _stagedBytes;

    public bool Exists(string chunkIdHex) =>
        _inner.Exists(chunkIdHex) || _isKnownRemotely(chunkIdHex);

    public void Put(string chunkIdHex, byte[] blob)
    {
        bool isNew = !_inner.Exists(chunkIdHex);
        _inner.Put(chunkIdHex, blob);
        if (isNew)
        {
            _stagedIds.Add(chunkIdHex);
            _stagedBytes += blob.Length;
        }
    }

    public void Put(string chunkIdHex, ReadOnlySpan<byte> blob)
    {
        bool isNew = !_inner.Exists(chunkIdHex);
        _inner.Put(chunkIdHex, blob);
        if (isNew)
        {
            _stagedIds.Add(chunkIdHex);
            _stagedBytes += blob.Length;
        }
    }

    public byte[] Get(string chunkIdHex) => _inner.Get(chunkIdHex);

    public long StoredCount => _inner.StoredCount;

    /// <summary>Clears the per-batch tracking after a flush (upload → prune).</summary>
    public void ResetBatch()
    {
        _stagedIds.Clear();
        _stagedBytes = 0;
    }
}
