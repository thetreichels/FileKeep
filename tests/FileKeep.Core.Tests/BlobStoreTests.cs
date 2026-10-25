using FileKeep.Core;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// DL-9: blob <c>Put</c> durability (temp-write + fsync + rename + directory
/// fsync). The fsync itself cannot be unit-tested — proving it needs a
/// power cut mid-write — so these tests pin the observable contract the
/// durability change must not break: Put→Get returns the exact bytes for
/// both overloads and both stores, and a repeated Put of the same chunk ID
/// keeps the first blob (content-addressed dedup).
/// </summary>
public sealed class BlobStoreTests : IDisposable
{
    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(), "ub-blobstore-test-" + Guid.NewGuid().ToString("N"));

    public BlobStoreTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static string ChunkId(byte fill) =>
        Hashing.Sha256Hex(new byte[] { fill, 1, 2, 3 });

    private static byte[] RandomBytes(int seed, int length)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void LocalPutGet_RoundTripsExactBytes()
    {
        var store = new LocalBlobStore(Path.Combine(_workDir, "repo"));
        byte[] blob = RandomBytes(42, 10_000);
        string id = ChunkId(9);

        store.Put(id, blob);

        Assert.True(store.Exists(id));
        Assert.Equal(blob, store.Get(id));
    }

    [Fact]
    public void LocalPutSpan_RoundTripsExactBytes()
    {
        var store = new LocalBlobStore(Path.Combine(_workDir, "repo"));
        byte[] blob = RandomBytes(7, 5_000);
        string id = ChunkId(5);

        store.Put(id, blob.AsSpan());

        Assert.True(store.Exists(id));
        Assert.Equal(blob, store.Get(id));
    }

    [Fact]
    public void LocalPut_SameIdTwice_KeepsFirstBlob()
    {
        // Content addressing: the ID names the content, so a second Put
        // for an existing ID is a dedup hit even if handed other bytes —
        // the durability change must not alter that.
        var store = new LocalBlobStore(Path.Combine(_workDir, "repo"));
        string id = ChunkId(3);
        byte[] first = { 1, 2, 3, 4 };
        byte[] other = { 9, 9, 9, 9, 9, 9 };

        store.Put(id, first);
        store.Put(id, other);

        Assert.Equal(first, store.Get(id));
    }

    [Fact]
    public void SmbPutGet_RoundTripsExactBytes_BothOverloads()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        using var share = SmbShare.Connect(ToUncOrLocal(shareDir), null, null);
        var store = new SmbBlobStore(share);

        byte[] blob = RandomBytes(11, 8_000);
        string id = ChunkId(21);
        store.Put(id, blob);
        Assert.Equal(blob, store.Get(id));

        byte[] spanBlob = RandomBytes(13, 4_000);
        string spanId = ChunkId(33);
        store.Put(spanId, spanBlob.AsSpan());
        Assert.Equal(spanBlob, store.Get(spanId));

        // Dedup semantics unchanged: first blob wins for an existing ID.
        store.Put(id, new byte[] { 1, 1, 1 });
        Assert.Equal(blob, store.Get(id));
    }

    /// <summary>
    /// On Windows returns a UNC path; elsewhere returns the local path as-is
    /// (SmbShare.Connect without creds accepts local directories).
    /// </summary>
    private static string ToUncOrLocal(string localDir)
    {
        if (OperatingSystem.IsWindows())
        {
            // Administrative share to the local drive: \\localhost\C$\...
            string full = Path.GetFullPath(localDir);
            string drive = Path.GetPathRoot(full)!.TrimEnd('\\', ':');
            string rest = full.Substring(Path.GetPathRoot(full)!.Length);
            return $@"\\localhost\{drive}$\{rest}";
        }
        return localDir;
    }
}
