using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// <see cref="NntpConnectionPool"/> against the in-memory fake NNTP server:
/// pool creation, parallel acquire/release, and pooled blob store operations.
/// </summary>
public sealed class NntpConnectionPoolTests : IDisposable
{
    private const string RepoId = "0123456789abcdef";
    private const string Newsgroup = "alt.binaries.test";

    private readonly string _workDir;
    private readonly FakeNntpServer _server;

    public NntpConnectionPoolTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-pool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _server = new FakeNntpServer();
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private NntpClient Connect()
    {
        var (clientStream, serverStream) = InMemoryTransport.Create();
        _ = Task.Run(() =>
        {
            using (serverStream)
                _server.HandleConnection(serverStream, CancellationToken.None);
        });
        var client = new NntpClient(clientStream);
        client.Connect();
        return client;
    }

    private int _clientsCreated;

    /// <summary>Factory for the pool's test seam: counts creations so
    /// tests can observe replacement/eviction, and connects over the
    /// in-memory transport exactly like <see cref="Connect"/>.</summary>
    private NntpClient ConnectTracked()
    {
        Interlocked.Increment(ref _clientsCreated);
        return Connect();
    }

    private NntpConnectionPool CreatePool(int size) =>
        NntpConnectionPool.CreateWithClientFactory(size, ConnectTracked);

    [Fact]
    public void Pool_Acquire_Release_Works()
    {
        // Real behavioural test (C-H1): the old body only reflected on
        // the type. The pool must connect its clients upfront, hand
        // them out, and reuse a released one.
        using var pool = CreatePool(2);
        Assert.Equal(2, pool.Size);
        Assert.Equal(2, _clientsCreated); // all clients connected upfront

        var first = pool.Acquire();
        var second = pool.Acquire();
        Assert.NotSame(first, second);
        Assert.True(first.IsConnected);
        Assert.True(second.IsConnected);

        pool.Release(first);
        var again = pool.Acquire();
        Assert.Same(first, again); // released connection is reused
        pool.Release(again);
        pool.Release(second);
    }

    [Fact]
    public void Pool_Acquire_BlocksUntilRelease_WhenExhausted()
    {
        using var pool = CreatePool(1);
        var only = pool.Acquire();

        var pending = Task.Run(() => pool.Acquire());
        // The semaphore is held by `only`, so the acquire cannot
        // complete before the release — this direction cannot flake.
        Assert.False(pending.Wait(TimeSpan.FromMilliseconds(300)));
        pool.Release(only);
        Assert.True(pending.Wait(TimeSpan.FromSeconds(5)));
        Assert.Same(only, pending.Result);
        pool.Release(pending.Result);
    }

    [Fact]
    public void Pool_ReleasedDeadClient_IsReplacedOnAcquire()
    {
        using var pool = CreatePool(1);
        var dead = pool.Acquire();
        dead.Dispose(); // connection drops while checked out
        Assert.False(dead.IsConnected);
        pool.Release(dead);

        var replacement = pool.Acquire();
        Assert.NotSame(dead, replacement);
        Assert.True(replacement.IsConnected);
        Assert.Equal(2, _clientsCreated); // 1 upfront + 1 replacement
        pool.Release(replacement);
    }

    [Fact]
    public void Pool_Use_RunsActionAndReturnsClientToPool()
    {
        using var pool = CreatePool(1);
        NntpClient? seen = null;
        int result = pool.Use(client =>
        {
            seen = client;
            Assert.True(client.IsConnected);
            return 42;
        });
        Assert.Equal(42, result);
        // The client went back: the next acquire hands out the same one.
        var again = pool.Acquire();
        Assert.Same(seen, again);
        pool.Release(again);
    }

    [Fact]
    public void Pool_Use_NntpException_EvictsAndRetriesWithFreshClient()
    {
        using var pool = CreatePool(1);
        int attempts = 0;
        NntpClient? firstClient = null;
        var used = pool.Use(client =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstClient = client;
                throw new NntpException("connection died (simulated)");
            }
            return client;
        });
        Assert.Equal(2, attempts);
        Assert.NotSame(firstClient, used); // corpse evicted, fresh client retried
        Assert.Equal(2, _clientsCreated); // 1 upfront + 1 eviction replacement
    }

    [Fact]
    public void Pool_AcquireAfterDispose_Throws()
    {
        var pool = CreatePool(1);
        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pool.Acquire());
    }

    [Fact]
    public void Pool_SizeOutOfRange_Throws()
    {
        // Validation runs before any connection attempt.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NntpConnectionPool("unreachable.invalid", size: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NntpConnectionPool.CreateWithClientFactory(0, ConnectTracked));
    }

    [Fact]
    public void Pooled_BlobStore_Put_Get_RoundTrip()
    {
        // Through a REAL pool this time (C-H1): the old version used a
        // single client directly and never constructed the pool.
        using var pool = CreatePool(3);
        string catalogPath = Path.Combine(_workDir, "catalog.db");
        using var store = new NntpBlobStore(pool, Newsgroup, RepoId, catalogPath);
        Assert.True(store.IsPooled);
        Assert.Equal(3, store.ConnectionCount);

        string chunkId = new string('a', 64); // 64 hex chars
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };

        store.Put(chunkId, data);
        Assert.True(store.Exists(chunkId));
        byte[] retrieved = store.Get(chunkId);
        Assert.Equal(data, retrieved);
    }

    [Fact]
    public void NntpBlobStore_Reports_Pooled_Status()
    {
        using var client = Connect();
        string catalogPath = Path.Combine(_workDir, "catalog2.db");
        using var store = new NntpBlobStore(client, Newsgroup, RepoId, catalogPath);

        // Single-client mode
        Assert.False(store.IsPooled);
        Assert.Equal(1, store.ConnectionCount);
    }
}
