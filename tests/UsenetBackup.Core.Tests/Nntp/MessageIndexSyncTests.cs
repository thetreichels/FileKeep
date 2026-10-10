using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Verifies the versioned remote chunk-message-identity index:
/// PostMessageIndex / GetLatestMessageIndex round-trip, idempotent
/// re-post, and ChunkMessageIndex JSON serialization.
/// </summary>
public sealed class MessageIndexSyncTests : IDisposable
{
    private const string RepoId = "0123456789abcdef";
    private const string Newsgroup = "alt.binaries.test";

    private readonly string _workDir;
    private readonly FakeNntpServer _server;

    public MessageIndexSyncTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-msgidx-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _server = new FakeNntpServer();
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private NntpBlobStore CreateStore()
    {
        var (clientStream, serverStream) = InMemoryTransport.Create();
        _ = Task.Run(() =>
        {
            using (serverStream)
                _server.HandleConnection(serverStream, CancellationToken.None);
        });
        var client = new NntpClient(clientStream);
        client.Connect();
        return new NntpBlobStore(client, Newsgroup, RepoId, Path.Combine(_workDir, "catalog.db"));
    }

    [Fact]
    public void PostAndGetLatest_RoundTrips()
    {
        using var store = CreateStore();
        string json = """{"news.example.com/alt.binaries.test":{"abc123":"<abc123.0123456789abcdef@usenet-backup>"}}""";

        int v1 = store.PostMessageIndex(json);
        Assert.Equal(1, v1);

        string? fetched = store.GetLatestMessageIndex();
        Assert.Equal(json, fetched);
    }

    [Fact]
    public void PostMessageIndex_IdempotentWhenUnchanged()
    {
        using var store = CreateStore();
        string json = """{"host/group":{"aa":"<aa.id@usenet-backup>"}}""";

        int v1 = store.PostMessageIndex(json);
        int v2 = store.PostMessageIndex(json);
        Assert.Equal(v1, v2); // no new version for identical content
    }

    [Fact]
    public void PostMessageIndex_NewVersionWhenChanged()
    {
        using var store = CreateStore();

        int v1 = store.PostMessageIndex("""{"h/g":{"a":"<a@x>"}}""");
        int v2 = store.PostMessageIndex("""{"h/g":{"a":"<b@x>"}}""");
        Assert.Equal(v1 + 1, v2);

        string? latest = store.GetLatestMessageIndex();
        Assert.Contains("<b@x>", latest);
    }

    [Fact]
    public void GetLatestMessageIndex_NullWhenNone()
    {
        using var store = CreateStore();
        Assert.Null(store.GetLatestMessageIndex());
    }

    [Fact]
    public void ChunkMessageIndex_JsonRoundTrip()
    {
        var index = new ChunkMessageIndex(_workDir);
        index.RecordNewIdentity("host/group", "abc123", "<new-id@usenet-backup>");

        string json = index.ToJson();
        Assert.Contains("abc123", json);
        Assert.Contains("new-id@usenet-backup", json);

        // Load into a fresh index (simulating a different machine)
        string workDir2 = Path.Combine(Path.GetTempPath(), "ub-msgidx-test2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir2);
        try
        {
            var index2 = new ChunkMessageIndex(workDir2);
            index2.LoadFromJson(json);
            string msgId = index2.GetMessageId("host/group", "abc123", RepoId);
            Assert.Equal("<new-id@usenet-backup>", msgId);
        }
        finally
        {
            try { Directory.Delete(workDir2, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GetLatestMessageIndex_ReturnsNullWhenNeverPublished()
    {
        using var store = CreateStore();
        Assert.Null(store.GetLatestMessageIndex());
    }

    [Fact]
    public void GetLatestMessageIndex_ToleratesExpiredOlderVersions()
    {
        using var store = CreateStore();
        int v1 = store.PostMessageIndex("{\"v\":1}");
        int v2 = store.PostMessageIndex("{\"v\":2}");
        int v3 = store.PostMessageIndex("{\"v\":3}");
        Assert.Equal(3, v3);

        // Simulate expiry of the older versions: their articles are gone
        // while the newest survives. Discovery must not stop at the gap.
        Assert.True(_server.Articles.TryRemove(ArticleCodec.MakeMessageIndexMessageId(RepoId, v1), out _));
        Assert.True(_server.Articles.TryRemove(ArticleCodec.MakeMessageIndexMessageId(RepoId, v2), out _));

        Assert.Equal("{\"v\":3}", store.GetLatestMessageIndex());
    }

    [Fact]
    public void PostMessageIndex_AfterGap_ContinuesFromSurvivingVersion()
    {
        using var store = CreateStore();
        int v1 = store.PostMessageIndex("{\"v\":1}");
        int v2 = store.PostMessageIndex("{\"v\":2}");
        Assert.True(_server.Articles.TryRemove(ArticleCodec.MakeMessageIndexMessageId(RepoId, v1), out _));

        // Latest surviving version is v2, so the next post must be v3 —
        // not v2 (which would collide with the surviving article).
        int v3 = store.PostMessageIndex("{\"v\":3}");
        Assert.Equal(3, v3);
        Assert.Equal("{\"v\":3}", store.GetLatestMessageIndex());
    }

    [Fact]
    public void GetLatestMessageIndex_ThrowsWhenArticleUnfetchable()
    {
        using var store = CreateStore();
        store.PostMessageIndex("{\"v\":1}");
        // STAT succeeds but ARTICLE fails: expected-but-broken.
        _server.FailArticleFetch.Add(ArticleCodec.MakeMessageIndexMessageId(RepoId, 1));

        var ex = Assert.Throws<MessageIndexFetchException>(() => store.GetLatestMessageIndex());
        Assert.Contains("v1", ex.Message);
    }

    [Fact]
    public void GetLatestMessageIndex_ThrowsWhenArticleUnparseable()
    {
        using var store = CreateStore();
        store.PostMessageIndex("{\"v\":1}");
        // Corrupt the article so it has no header/body separator.
        _server.Articles[ArticleCodec.MakeMessageIndexMessageId(RepoId, 1)] =
            "this is not a valid article, no headers at all";

        var ex = Assert.Throws<MessageIndexFetchException>(() => store.GetLatestMessageIndex());
        Assert.Contains("parsed", ex.Message);
    }

    [Fact]
    public void SyncMessageIndex_LoadsRemoteIndex()
    {
        using var store = CreateStore();
        store.PostMessageIndex("{\"h/g\":{\"a\":\"<a@x>\"}}");

        var index = new ChunkMessageIndex(_workDir);
        store.SyncMessageIndex(index);

        Assert.Equal("<a@x>", index.GetMessageId("h/g", "a", RepoId));
    }

    [Fact]
    public void SyncMessageIndex_NoOpWhenNeverPublished()
    {
        using var store = CreateStore();
        var index = new ChunkMessageIndex(_workDir);
        store.SyncMessageIndex(index); // must not throw
        Assert.Equal(ArticleCodec.MakeMessageId("a", RepoId),
            index.GetMessageId("h/g", "a", RepoId)); // deterministic fallback intact
    }

    [Fact]
    public void SyncMessageIndex_ThrowsExplicitErrorWhenFetchFails()
    {
        using var store = CreateStore();
        store.PostMessageIndex("{\"v\":1}");
        _server.FailArticleFetch.Add(ArticleCodec.MakeMessageIndexMessageId(RepoId, 1));

        var index = new ChunkMessageIndex(_workDir);
        var ex = Assert.Throws<MessageIndexFetchException>(() => store.SyncMessageIndex(index));
        Assert.Contains("could not be retrieved", ex.Message);
        Assert.IsType<MessageIndexFetchException>(ex.InnerException);
    }

    [Fact]
    public void SyncMessageIndex_ThrowsExplicitErrorWhenUnparseable()
    {
        using var store = CreateStore();
        store.PostMessageIndex("{\"v\":1}");
        _server.Articles[ArticleCodec.MakeMessageIndexMessageId(RepoId, 1)] = "not json at all";

        var index = new ChunkMessageIndex(_workDir);
        // "not json at all" has no header/body separator -> fetch-level
        // parse failure, wrapped as MessageIndexFetchException.
        var ex = Assert.Throws<MessageIndexFetchException>(() => store.SyncMessageIndex(index));
        Assert.IsType<MessageIndexFetchException>(ex.InnerException);
    }

    [Fact]
    public void MakeMessageIndexMessageId_Deterministic()
    {
        string id1 = ArticleCodec.MakeMessageIndexMessageId(RepoId, 1);
        string id2 = ArticleCodec.MakeMessageIndexMessageId(RepoId, 1);
        string id3 = ArticleCodec.MakeMessageIndexMessageId(RepoId, 2);
        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.Contains("msgindex", id1);
    }
}
