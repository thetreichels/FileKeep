using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Tests for StreamingVolumeBlobStore: packs chunks into volumes and posts
/// them as they accumulate, without local staging.
/// </summary>
public sealed class StreamingVolumeBlobStoreTests : IDisposable
{
    private readonly string _workDir;

    public StreamingVolumeBlobStoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-svbs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public void Put_PacksIntoVolumes_WhenBufferFull()
    {
        var posted = new List<(string Id, byte[] Bytes)>();
        // Small volume size to force multiple volumes in the test.
        using var store = new StreamingVolumeBlobStore(
            (id, bytes) => posted.Add((id, bytes)),
            targetVolumeBytes: 1024);

        // Put 10 chunks of 100 bytes each = 1000 bytes + headers.
        // With 1KB target, this should create multiple volumes.
        for (int i = 0; i < 10; i++)
        {
            string chunkId = i.ToString("x64").PadLeft(64, '0')[^64..];
            store.Put(chunkId, new byte[100]);
        }
        store.Flush();

        // Should have posted at least 2 volumes (1000 bytes > 1024 with headers).
        Assert.True(posted.Count >= 2,
            $"Expected >= 2 volumes, got {posted.Count}");
        Assert.Equal(10, store.StoredCount);
    }

    [Fact]
    public void Put_SingleChunk_CreatesSingleVolume()
    {
        var posted = new List<(string Id, byte[] Bytes)>();
        using var store = new StreamingVolumeBlobStore(
            (id, bytes) => posted.Add((id, bytes)),
            targetVolumeBytes: 100 * 1024 * 1024);

        string chunkId = new string('b', 64);
        store.Put(chunkId, new byte[100]);
        store.Flush();

        Assert.Single(posted);
        Assert.Equal(1, store.StoredCount);
    }

    [Fact]
    public void Get_ThrowsNotSupported()
    {
        var posted = new List<(string Id, byte[] Bytes)>();
        using var store = new StreamingVolumeBlobStore(
            (id, bytes) => posted.Add((id, bytes)));

        Assert.Throws<NotSupportedException>(() => store.Get(new string('c', 64)));
    }

    [Fact]
    public void Flush_Idempotent_WhenEmpty()
    {
        var posted = new List<(string Id, byte[] Bytes)>();
        using var store = new StreamingVolumeBlobStore(
            (id, bytes) => posted.Add((id, bytes)));

        // Should not throw or post anything.
        store.Flush();
        store.Flush();

        Assert.Empty(posted);
    }
}
