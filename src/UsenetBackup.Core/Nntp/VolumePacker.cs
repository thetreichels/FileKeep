using System.Buffers.Binary;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// A packed volume: multiple encrypted chunk blobs concatenated into a single
/// binary blob that is posted to Usenet as ONE article. Chunking (dedup
/// granularity) and chunk crypto are untouched — packing happens at the
/// upload layer only, so one volume article replaces N chunk articles.
/// </summary>
public sealed record PackedVolume(
    /// <summary>SHA-256 hex of <see cref="Bytes"/>; doubles as the article identity.</summary>
    string Id,
    /// <summary>The complete volume bytes (header + payload).</summary>
    byte[] Bytes,
    /// <summary>Chunk IDs in pack order.</summary>
    IReadOnlyList<string> ChunkIds);

/// <summary>
/// Packs encrypted chunk blobs into volumes for upload, and unpacks them on
/// download. Volume format:
/// <code>
/// magic:   4 bytes "VOL1"
/// count:   int32 LE, number of chunk entries (>= 1)
/// per chunk:
///     id:     32 bytes raw (64-hex chunk ID decoded)
///     offset: int64 LE, offset into the payload
///     length: int32 LE, encrypted blob length
/// payload: concatenated encrypted chunk blobs
/// </code>
/// </summary>
public static class VolumePacker
{
    public static readonly byte[] Magic = [(byte)'V', (byte)'O', (byte)'L', (byte)'1'];

    private const int EntrySize = 32 + 8 + 4; // id + offset + length
    private const int HeaderFixedSize = 4 + 4; // magic + count
    private const int MaxEntries = 1_000_000;

    /// <summary>
    /// Greedily packs ordered chunk blobs into volumes of at most
    /// <paramref name="targetVolumeBytes"/> each. Every volume holds at least
    /// one chunk (an oversized chunk gets a volume of its own). Deterministic:
    /// same chunk IDs + same target size always yield the same volumes and IDs.
    /// Reads every blob; for large backups prefer <see cref="Plan"/> +
    /// <see cref="BuildVolume"/> to stream one volume at a time.
    /// </summary>
    public static IReadOnlyList<PackedVolume> Pack(
        IReadOnlyList<string> orderedChunkIds,
        Func<string, byte[]> getBlob,
        long targetVolumeBytes)
    {
        ArgumentNullException.ThrowIfNull(orderedChunkIds);
        ArgumentNullException.ThrowIfNull(getBlob);
        if (targetVolumeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetVolumeBytes), "Target volume size must be positive.");
        foreach (string chunkId in orderedChunkIds)
            ValidateChunkId(chunkId);

        // Read each blob once, then group and build.
        var blobs = new Dictionary<string, byte[]>(orderedChunkIds.Count);
        foreach (string chunkId in orderedChunkIds)
            blobs[chunkId] = getBlob(chunkId)
                ?? throw new InvalidDataException($"Chunk blob for {chunkId} was null.");

        return GroupIntoVolumes(orderedChunkIds, id => blobs[id].Length, targetVolumeBytes)
            .Select(group => BuildVolumeFromParts(
                group.ToList(),
                group.Select(id => blobs[id]).ToList()))
            .ToList();
    }

    /// <summary>
    /// Computes the greedy grouping of chunk IDs into volumes without reading
    /// any blob bytes — only sizes via <paramref name="getBlobSize"/>. The
    /// grouping is identical to what <see cref="Pack"/> would produce.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Plan(
        IReadOnlyList<string> orderedChunkIds,
        Func<string, long> getBlobSize,
        long targetVolumeBytes)
    {
        ArgumentNullException.ThrowIfNull(orderedChunkIds);
        ArgumentNullException.ThrowIfNull(getBlobSize);
        if (targetVolumeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetVolumeBytes), "Target volume size must be positive.");
        foreach (string chunkId in orderedChunkIds)
            ValidateChunkId(chunkId);

        return GroupIntoVolumes(orderedChunkIds, getBlobSize, targetVolumeBytes);
    }

    private static IReadOnlyList<IReadOnlyList<string>> GroupIntoVolumes(
        IReadOnlyList<string> orderedChunkIds,
        Func<string, long> getBlobSize,
        long targetVolumeBytes)
    {
        var groups = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        long payloadBytes = 0;

        void Flush()
        {
            if (current.Count == 0)
                return;
            groups.Add(current.ToArray());
            current.Clear();
            payloadBytes = 0;
        }

        foreach (string chunkId in orderedChunkIds)
        {
            long size = getBlobSize(chunkId);
            if (size < 0)
                throw new InvalidDataException($"Chunk blob size for {chunkId} was negative.");
            // Header grows with each entry; check whether this blob still fits.
            long headerIfAdded = HeaderFixedSize + ((long)current.Count + 1) * EntrySize;
            if (current.Count > 0 && headerIfAdded + payloadBytes + size > targetVolumeBytes)
                Flush();
            current.Add(chunkId);
            payloadBytes += size;
        }
        Flush();
        return groups;
    }

    /// <summary>
    /// Builds a single volume from a planned chunk group. The volume ID is
    /// the SHA-256 hex of the volume bytes (deterministic).
    /// </summary>
    public static PackedVolume BuildVolume(
        IReadOnlyList<string> chunkIds,
        Func<string, byte[]> getBlob)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        ArgumentNullException.ThrowIfNull(getBlob);
        if (chunkIds.Count == 0)
            throw new ArgumentException("Volume must hold at least one chunk.", nameof(chunkIds));
        foreach (string chunkId in chunkIds)
            ValidateChunkId(chunkId);

        var ids = chunkIds.ToList();
        var blobs = new List<byte[]>(ids.Count);
        foreach (string id in ids)
            blobs.Add(getBlob(id) ?? throw new InvalidDataException($"Chunk blob for {id} was null."));
        return BuildVolumeFromParts(ids, blobs);
    }

    /// <summary>
    /// Splits volume bytes back into (chunk ID, encrypted blob) pairs,
    /// validating the format fail-closed. Does NOT decrypt or hash-verify;
    /// the caller runs each blob through the normal chunk verification.
    /// </summary>
    public static IReadOnlyList<(string ChunkId, byte[] Blob)> Unpack(byte[] volumeBytes)
    {
        ArgumentNullException.ThrowIfNull(volumeBytes);
        if (volumeBytes.Length < HeaderFixedSize)
            throw new InvalidDataException("Volume too short for header.");
        for (int i = 0; i < Magic.Length; i++)
            if (volumeBytes[i] != Magic[i])
                throw new InvalidDataException("Volume has bad magic.");

        int count = BinaryPrimitives.ReadInt32LittleEndian(volumeBytes.AsSpan(4, 4));
        if (count < 1 || count > MaxEntries)
            throw new InvalidDataException($"Volume entry count {count} out of range.");

        long headerSize = HeaderFixedSize + (long)count * EntrySize;
        if (headerSize > volumeBytes.Length)
            throw new InvalidDataException("Volume header overruns volume bytes.");
        long payloadSize = volumeBytes.Length - headerSize;

        var entries = new List<(string Id, long Offset, int Length)>(count);
        for (int i = 0; i < count; i++)
        {
            int baseOff = HeaderFixedSize + i * EntrySize;
            string id = Convert.ToHexString(volumeBytes.AsSpan(baseOff, 32)).ToLowerInvariant();
            long offset = BinaryPrimitives.ReadInt64LittleEndian(volumeBytes.AsSpan(baseOff + 32, 8));
            int length = BinaryPrimitives.ReadInt32LittleEndian(volumeBytes.AsSpan(baseOff + 40, 4));
            if (length < 0 || offset < 0 || offset > payloadSize || length > payloadSize - offset)
                throw new InvalidDataException($"Volume entry {i} has out-of-bounds offset/length.");
            entries.Add((id, offset, length));
        }

        // Entries must not overlap (checked in offset order).
        var byOffset = entries.OrderBy(e => e.Offset).ToList();
        for (int i = 1; i < byOffset.Count; i++)
        {
            if (byOffset[i].Offset < byOffset[i - 1].Offset + byOffset[i - 1].Length)
                throw new InvalidDataException("Volume entries overlap.");
        }

        int payloadStart = (int)headerSize;
        var result = new List<(string ChunkId, byte[] Blob)>(count);
        foreach (var (id, offset, length) in entries)
            result.Add((id, volumeBytes.AsSpan(payloadStart + (int)offset, length).ToArray()));
        return result;
    }

    private static PackedVolume BuildVolumeFromParts(List<string> ids, List<byte[]> blobs)
    {
        long headerSize = HeaderFixedSize + (long)ids.Count * EntrySize;
        long total = headerSize;
        foreach (byte[] b in blobs)
            total += b.Length;
        if (total > int.MaxValue)
            throw new InvalidDataException("Volume exceeds 2 GiB.");

        var bytes = new byte[total];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), ids.Count);
        long payloadOffset = headerSize;
        for (int i = 0; i < ids.Count; i++)
        {
            int baseOff = HeaderFixedSize + i * EntrySize;
            Convert.FromHexString(ids[i]).CopyTo(bytes.AsSpan(baseOff, 32));
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(baseOff + 32, 8), payloadOffset - headerSize);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(baseOff + 40, 4), blobs[i].Length);
            blobs[i].CopyTo(bytes, payloadOffset);
            payloadOffset += blobs[i].Length;
        }
        string volumeId = Hashing.Sha256Hex(bytes);
        return new PackedVolume(volumeId, bytes, ids.ToArray());
    }

    private static void ValidateChunkId(string chunkId)
    {
        if (chunkId.Length != 64 || !chunkId.All(Uri.IsHexDigit))
            throw new ArgumentException($"Chunk ID must be 64 hex chars: {chunkId}", nameof(chunkId));
    }
}
