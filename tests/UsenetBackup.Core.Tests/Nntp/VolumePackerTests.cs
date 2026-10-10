using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

public sealed class VolumePackerTests
{
    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    private static string ChunkIdOf(byte[] b) => Hashing.Sha256Hex(b);

    private static (List<string> Ids, Dictionary<string, byte[]> Blobs) MakeChunks(int n, int size)
    {
        var ids = new List<string>(n);
        var blobs = new Dictionary<string, byte[]>(n);
        for (int i = 0; i < n; i++)
        {
            byte[] blob = RandomBytes(size);
            string id = ChunkIdOf(blob);
            ids.Add(id);
            blobs[id] = blob;
        }
        ids.Sort(StringComparer.Ordinal);
        return (ids, blobs);
    }

    [Fact]
    public void RoundTrip_PreservesAllChunks()
    {
        var (ids, blobs) = MakeChunks(5, 1000);
        var volumes = VolumePacker.Pack(ids, id => blobs[id], targetVolumeBytes: 1_000_000);

        var recovered = new Dictionary<string, byte[]>();
        foreach (var v in volumes)
            foreach (var (chunkId, blob) in VolumePacker.Unpack(v.Bytes))
                recovered[chunkId] = blob;

        Assert.Equal(ids.Count, recovered.Count);
        foreach (string id in ids)
            Assert.Equal(blobs[id], recovered[id]);
    }

    [Fact]
    public void Pack_SplitsAcrossTargetSize()
    {
        var (ids, blobs) = MakeChunks(10, 10_000);
        // Each chunk is 10k; target 25k fits 2 chunks + header per volume.
        var volumes = VolumePacker.Pack(ids, id => blobs[id], targetVolumeBytes: 25_000);

        Assert.True(volumes.Count > 1);
        foreach (var v in volumes)
            Assert.True(v.Bytes.Length <= 25_000);
        Assert.Equal(ids.Count, volumes.Sum(v => v.ChunkIds.Count));
        // Every chunk appears in exactly one volume.
        var seen = volumes.SelectMany(v => v.ChunkIds).ToList();
        seen.Sort(StringComparer.Ordinal);
        Assert.Equal(ids, seen);
    }

    [Fact]
    public void Pack_OversizedChunkGetsOwnVolume()
    {
        var (ids, blobs) = MakeChunks(3, 100);
        string bigId = new('b', 64);
        byte[] bigBlob = RandomBytes(100_000);
        blobs[bigId] = bigBlob;
        var allIds = ids.Append(bigId).ToList();
        allIds.Sort(StringComparer.Ordinal);

        var volumes = VolumePacker.Pack(allIds, id => blobs[id], targetVolumeBytes: 1000);

        var bigVolume = volumes.Single(v => v.ChunkIds.Contains(bigId));
        Assert.Single(bigVolume.ChunkIds);
        var unpacked = VolumePacker.Unpack(bigVolume.Bytes);
        Assert.Single(unpacked);
        Assert.Equal(bigBlob, unpacked[0].Blob);
    }

    [Fact]
    public void Pack_EmptyInput_YieldsNoVolumes()
    {
        var volumes = VolumePacker.Pack([], _ => throw new InvalidOperationException("should not be called"), 1000);
        Assert.Empty(volumes);
    }

    [Fact]
    public void Pack_IsDeterministic()
    {
        var (ids, blobs) = MakeChunks(8, 5000);
        var a = VolumePacker.Pack(ids, id => blobs[id], 20_000);
        var b = VolumePacker.Pack(ids, id => blobs[id], 20_000);
        Assert.Equal(a.Select(v => v.Id), b.Select(v => v.Id));
        Assert.Equal(a.Select(v => Convert.ToHexString(v.Bytes)), b.Select(v => Convert.ToHexString(v.Bytes)));
    }

    [Fact]
    public void Pack_VolumeIdIsSha256OfBytes()
    {
        var (ids, blobs) = MakeChunks(2, 100);
        var volumes = VolumePacker.Pack(ids, id => blobs[id], 1_000_000);
        Assert.Single(volumes);
        Assert.Equal(Hashing.Sha256Hex(volumes[0].Bytes), volumes[0].Id);
    }

    [Fact]
    public void Pack_RejectsBadChunkId()
    {
        Assert.Throws<ArgumentException>(() =>
            VolumePacker.Pack(["not-a-chunk-id"], _ => new byte[10], 1000));
    }

    [Fact]
    public void Pack_RejectsNonPositiveTarget()
    {
        var (ids, blobs) = MakeChunks(1, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VolumePacker.Pack(ids, id => blobs[id], 0));
    }

    [Fact]
    public void Unpack_BadMagic_Throws()
    {
        var (ids, blobs) = MakeChunks(2, 100);
        var volumes = VolumePacker.Pack(ids, id => blobs[id], 1_000_000);
        byte[] bad = (byte[])volumes[0].Bytes.Clone();
        bad[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => VolumePacker.Unpack(bad));
    }

    [Fact]
    public void Unpack_ZeroCount_Throws()
    {
        byte[] bad = new byte[8];
        "VOL1"u8.CopyTo(bad);
        Assert.Throws<InvalidDataException>(() => VolumePacker.Unpack(bad));
    }

    [Fact]
    public void Unpack_TruncatedHeader_Throws()
    {
        var (ids, blobs) = MakeChunks(2, 100);
        var volumes = VolumePacker.Pack(ids, id => blobs[id], 1_000_000);
        byte[] truncated = volumes[0].Bytes[..20]; // cuts into the entry table
        Assert.Throws<InvalidDataException>(() => VolumePacker.Unpack(truncated));
    }

    [Fact]
    public void Unpack_OutOfBoundsOffset_Throws()
    {
        var (ids, blobs) = MakeChunks(2, 100);
        var volumes = VolumePacker.Pack(ids, id => blobs[id], 1_000_000);
        byte[] bad = (byte[])volumes[0].Bytes.Clone();
        // Entry 0 offset lives at byte 8+32=40; set it far past the payload.
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bad.AsSpan(40, 8), long.MaxValue / 2);
        Assert.Throws<InvalidDataException>(() => VolumePacker.Unpack(bad));
    }

    [Fact]
    public void Unpack_OverlappingEntries_Throws()
    {
        var (ids, blobs) = MakeChunks(2, 100);
        var volumes = VolumePacker.Pack(ids, id => blobs[id], 1_000_000);
        byte[] bad = (byte[])volumes[0].Bytes.Clone();
        // Entry 1 offset lives at byte 8+44+32=84; point it at entry 0's range.
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bad.AsSpan(84, 8), 0);
        Assert.Throws<InvalidDataException>(() => VolumePacker.Unpack(bad));
    }

    [Fact]
    public void Unpack_TooShort_Throws()
    {
        Assert.Throws<InvalidDataException>(() => VolumePacker.Unpack(new byte[7]));
    }
}

public sealed class VolumePackerPlanTests
{
    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    [Fact]
    public void Plan_MatchesPack_Grouping()
    {
        var blobs = new Dictionary<string, byte[]>();
        var ids = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            byte[] blob = RandomBytes(3000 + i * 500);
            string id = Hashing.Sha256Hex(blob);
            ids.Add(id);
            blobs[id] = blob;
        }
        ids.Sort(StringComparer.Ordinal);

        var plan = VolumePacker.Plan(ids, id => blobs[id].Length, targetVolumeBytes: 15000);
        var packed = VolumePacker.Pack(ids, id => blobs[id], targetVolumeBytes: 15000);

        Assert.Equal(packed.Count, plan.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            Assert.Equal(packed[i].ChunkIds, plan[i]);
            // Streaming build of the planned group yields the identical volume.
            var streamed = VolumePacker.BuildVolume(plan[i], id => blobs[id]);
            Assert.Equal(packed[i].Id, streamed.Id);
            Assert.Equal(packed[i].Bytes, streamed.Bytes);
        }
    }

    [Fact]
    public void BuildVolume_EmptyGroup_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VolumePacker.BuildVolume([], _ => new byte[10]));
    }

    [Fact]
    public void Plan_EmptyInput_YieldsNoGroups()
    {
        var plan = VolumePacker.Plan([], _ => 0L, 1000);
        Assert.Empty(plan);
    }
}
