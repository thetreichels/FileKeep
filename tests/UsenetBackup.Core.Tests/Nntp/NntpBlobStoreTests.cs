using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// <see cref="NntpBlobStore"/> against the in-memory fake NNTP server:
/// round-trip, idempotent put, resume (journal hit and server-adopt),
/// missing-article and tamper failures.
/// </summary>
public sealed class NntpBlobStoreTests : IDisposable
{
    private const string RepoId = "0123456789abcdef";
    private const string Newsgroup = "alt.binaries.test";

    private readonly string _workDir;
    private readonly FakeNntpServer _server;

    public NntpBlobStoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-nntp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _server = new FakeNntpServer();
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
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

    private NntpBlobStore OpenStore(NntpClient client, string? dbName = null) =>
        new(client, Newsgroup, RepoId,
            Path.Combine(_workDir, dbName ?? "journal.db"));

    private static (string ChunkId, byte[] Blob) RandomChunk(int size = 5000)
    {
        byte[] blob = RandomBytes(size);
        return (Hashing.Sha256Hex(blob), blob);
    }

    [Fact]
    public void Put_Get_RoundTrip()
    {
        var (chunkId, blob) = RandomChunk();
        using var client = Connect();
        using var store = OpenStore(client);

        Assert.False(store.Exists(chunkId));
        store.Put(chunkId, blob);
        Assert.Equal(1, _server.PostCount);

        Assert.True(store.Exists(chunkId));
        Assert.Equal(blob, store.Get(chunkId));
        Assert.Equal(1, store.StoredCount);
    }

    [Fact]
    public void Put_Twice_PostsOnce()
    {
        var (chunkId, blob) = RandomChunk();
        using var client = Connect();
        using var store = OpenStore(client);

        store.Put(chunkId, blob);
        store.Put(chunkId, blob);
        Assert.Equal(1, _server.PostCount);
    }

    [Fact]
    public void Resume_JournalHit_SkipsPostAndStat()
    {
        var (chunkId, blob) = RandomChunk();
        using var client = Connect();
        using (var store = OpenStore(client, "resume.db"))
            store.Put(chunkId, blob);
        Assert.Equal(1, _server.PostCount);

        // New store instance, same journal: must not touch the server at all.
        int statsBefore = _server.StatCount;
        using var store2 = OpenStore(client, "resume.db");
        store2.Put(chunkId, blob);
        Assert.Equal(1, _server.PostCount);
        Assert.Equal(statsBefore, _server.StatCount);
    }

    [Fact]
    public void Resume_JournalMiss_ServerHit_AdoptsWithoutRepost()
    {
        var (chunkId, blob) = RandomChunk();
        using var client = Connect();

        // Post directly through the client, bypassing the journal.
        client.Post(ArticleCodec.BuildArticle(chunkId, RepoId, blob, Newsgroup, "test"));
        Assert.Equal(1, _server.PostCount);

        // Store with a fresh (empty) journal: STAT finds it, no re-POST.
        using var store = OpenStore(client, "fresh.db");
        store.Put(chunkId, blob);
        Assert.Equal(1, _server.PostCount);
        Assert.True(store.Exists(chunkId)); // now adopted into the journal
    }

    [Fact]
    public void Get_Missing_Throws()
    {
        var (chunkId, _) = RandomChunk();
        using var client = Connect();
        using var store = OpenStore(client);
        Assert.Throws<InvalidDataException>(() => store.Get(chunkId));
    }

    [Fact]
    public void Get_CorruptedArticle_Throws()
    {
        var (chunkId, blob) = RandomChunk();
        using var client = Connect();
        using var store = OpenStore(client);
        store.Put(chunkId, blob);

        // Corrupt a byte of actual yEnc data on the server (first data line
        // after =ybegin), so the CRC-32 trailer no longer matches.
        string msgId = ArticleCodec.MakeMessageId(chunkId, RepoId);
        string article = _server.Articles[msgId];
        string[] lines = article.Split(new[] { "\r\n" }, StringSplitOptions.None);
        int begin = Array.FindIndex(lines, l => l.StartsWith("=ybegin", StringComparison.Ordinal));
        char[] chars = lines[begin + 1].ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        lines[begin + 1] = new string(chars);
        _server.Articles[msgId] = string.Join("\r\n", lines);

        Assert.Throws<InvalidDataException>(() => store.Get(chunkId));
    }

    [Fact]
    public void BlobStore_Conformance_LocalAndNntp()
    {
        var (chunkId, blob) = RandomChunk(1000);

        var local = new LocalBlobStore(Path.Combine(_workDir, "localrepo"));
        AssertConformance(local, chunkId, blob);

        using var client = Connect();
        using var remote = OpenStore(client, "conf.db");
        AssertConformance(remote, chunkId, blob);
    }

    private static void AssertConformance(IBlobStore store, string chunkId, byte[] blob)
    {
        string other = Hashing.Sha256Hex("other"u8.ToArray());
        Assert.False(store.Exists(other));
        Assert.Throws<InvalidDataException>(() => store.Get(other));

        store.Put(chunkId, blob);
        Assert.True(store.Exists(chunkId));
        Assert.Equal(blob, store.Get(chunkId));

        store.Put(chunkId, blob); // idempotent
        Assert.Equal(blob, store.Get(chunkId));

        Assert.Throws<ArgumentException>(() => store.Put("not-a-chunk-id", blob));
    }
}
