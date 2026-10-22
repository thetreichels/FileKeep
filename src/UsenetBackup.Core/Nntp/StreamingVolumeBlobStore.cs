using UsenetBackup.Core.Nntp;

namespace UsenetBackup.Core;

/// <summary>
/// An <see cref="IBlobStore"/> that packs chunks into volumes (like
/// <see cref="VolumePacker"/>) and posts each volume via a callback.
/// Chunks stream in via <see cref="Put"/>; volumes are posted when
/// the buffer reaches <see cref="TargetVolumeBytes"/>. Call <see cref="Flush"/>
/// at the end to post the final partial volume.
/// 
/// This enables disk-image backups to stream directly to Usenet without
/// local staging: chunks flow in, volumes flow out, nothing accumulates.
/// </summary>
public sealed class StreamingVolumeBlobStore : IBlobStore, IDisposable
{
    private readonly Action<string, byte[]> _postVolume;
    private readonly long _targetVolumeBytes;
    private readonly Action<Exception>? _onFlushError;
    private readonly List<string> _bufferIds = new();
    private readonly List<byte[]> _bufferBlobs = new();
    private long _bufferBytes = 0;
    private bool _disposed = false;

    /// <summary>
    /// Maps volume ID (hex) to the chunk IDs it contains, in post order.
    /// Used to build the manifest/NZB after streaming completes.
    /// </summary>
    public IReadOnlyList<(string VolumeId, IReadOnlyList<string> ChunkIds)> PostedVolumes { get; }
        = new List<(string, IReadOnlyList<string>)>();

    /// <summary>Total chunks accepted via Put (including buffered, not yet posted).</summary>
    public long StoredCount { get; private set; }

    /// <summary>
    /// Creates a streaming store. <paramref name="postVolume"/> is called
    /// with (volumeIdHex, volumeBytes) for each completed volume.
    /// </summary>
    public StreamingVolumeBlobStore(Action<string, byte[]> postVolume, long targetVolumeBytes = 100 * 1024 * 1024,
        Action<Exception>? onFlushError = null)
    {
        ArgumentNullException.ThrowIfNull(postVolume);
        if (targetVolumeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetVolumeBytes));
        _postVolume = postVolume;
        _targetVolumeBytes = targetVolumeBytes;
        _onFlushError = onFlushError;
    }

    /// <summary>
    /// The failure of the final flush attempted by <see cref="Dispose"/>,
    /// if it failed (CX-2). Dispose must not throw, but the failure is
    /// never silent anymore: it lands here and in the onFlushError
    /// callback. A caller that flushes explicitly (the normal path)
    /// sees the exception from Flush itself and this stays null.
    /// </summary>
    public Exception? DisposeFlushError { get; private set; }

    /// <summary>
    /// Creates a streaming store that posts volumes to an NNTP blob store.
    /// </summary>
    public StreamingVolumeBlobStore(NntpBlobStore inner, long targetVolumeBytes = 100 * 1024 * 1024,
        Action<Exception>? onFlushError = null)
        : this((id, bytes) => inner.PutVolume(id, bytes), targetVolumeBytes, onFlushError)
    {
    }

    public void Put(string chunkIdHex, byte[] blob)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkIdHex);
        ArgumentNullException.ThrowIfNull(blob);

        // If adding this chunk would exceed the target, flush first.
        // (Header size estimate: 8 bytes fixed + 44 per entry.)
        long headerIfAdded = 8 + ((long)_bufferIds.Count + 1) * 44;
        if (_bufferIds.Count > 0 && headerIfAdded + _bufferBytes + blob.Length > _targetVolumeBytes)
            FlushBuffer();

        _bufferIds.Add(chunkIdHex);
        _bufferBlobs.Add(blob);
        _bufferBytes += blob.Length;
        StoredCount++;
    }

    public byte[] Get(string chunkIdHex)
    {
        // Write-only for the streaming backup path. Restores use NntpBlobStore
        // directly (with volume unpacking).
        throw new NotSupportedException(
            "StreamingVolumeBlobStore is write-only; use NntpBlobStore for reads.");
    }

    public bool Exists(string chunkIdHex)
    {
        throw new NotSupportedException(
            "StreamingVolumeBlobStore is write-only; use NntpBlobStore for reads.");
    }

    /// <summary>
    /// Posts any buffered chunks as a final volume. Idempotent.
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FlushBuffer();
    }

    private void FlushBuffer()
    {
        if (_bufferIds.Count == 0)
            return;

        var ids = _bufferIds.ToArray();
        // Build a lookup for the getBlob function.
        var blobById = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ids.Length; i++)
            blobById[ids[i]] = _bufferBlobs[i];

        var volume = VolumePacker.BuildVolume(ids, id => blobById[id]);
        
        _postVolume(volume.Id, volume.Bytes);
        ((List<(string, IReadOnlyList<string>)>)PostedVolumes).Add((volume.Id, ids));

        _bufferIds.Clear();
        _bufferBlobs.Clear();
        _bufferBytes = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try { FlushBuffer(); }
        catch (Exception ex)
        {
            // Best effort on dispose — but recorded, not swallowed (CX-2):
            // a Dispose-only caller would otherwise lose the last volume
            // without a trace.
            DisposeFlushError = ex;
            try { _onFlushError?.Invoke(ex); } catch { /* the callback must not break Dispose */ }
        }
        _disposed = true;
    }
}
