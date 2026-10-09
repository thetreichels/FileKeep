using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// <see cref="RetentionManager"/> three-outcome design against the fake NNTP server:
/// healthy (no action), missing (republish missing with new IDs), expiring
/// (republish all with new IDs). Verifies that the retention timestamp only
/// advances after STAT-verified republication, and that dry-run mutates nothing.
/// </summary>
public sealed class RetentionManagerTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const string RepoId = "0123456789abcdef";
    private const string Newsgroup = "alt.binaries.test";
    private const string ProviderHost = "news.example.com";
    private const int RetentionDays = 1095;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly FakeNntpServer _server;

    public RetentionManagerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-retention-test-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
        _server = new FakeNntpServer();

        // Small source tree -> a few chunks
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), new string('a', 5000));
        File.WriteAllText(Path.Combine(_srcDir, "b.txt"), new string('b', 5000));
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

    private sealed class Fixture : IDisposable
    {
        public BackupRepository Repo { get; }
        public NntpBlobStore Store { get; }
        public ChunkMessageIndex MessageIndex { get; }
        public UsenetUploadTracker Tracker { get; }
        public BackupManifest Manifest { get; }
        public List<string> ChunkIds { get; }
        private readonly NntpClient _client;

        public Fixture(RetentionManagerTests outer)
        {
            Repo = BackupRepository.Init(outer._repoDir, Passphrase, 64 * 1024, 10_000);
            Manifest = Repo.BackupDirectory(outer._srcDir);
            ChunkIds = Manifest.Files.SelectMany(f => f.Chunks).Distinct().ToList();

            _client = outer.Connect();
            MessageIndex = new ChunkMessageIndex(outer._repoDir);
            Store = new NntpBlobStore(_client, Newsgroup, RepoId,
                Path.Combine(outer._workDir, "journal.db"),
                messageIndex: MessageIndex);
            Tracker = new UsenetUploadTracker(outer._repoDir);
        }

        /// <summary>Simulates the original upload: posts all chunks.</summary>
        public void UploadAll()
        {
            foreach (string chunkId in ChunkIds)
                Store.Put(chunkId, Repo.GetChunkBlob(chunkId));
        }

        public void Dispose()
        {
            Store.Dispose();
            _client.Dispose();
            Repo.Dispose();
        }
    }

    private static RetentionManager CreateManager(List<string> log) =>
        new(msg => log.Add(msg), _ => RetentionDays);

    [Fact]
    public void HealthyBackup_NoAction_TimestampUnchanged()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();
        int postsBefore = _server.PostCount;

        // Uploaded 1030 days ago: 65 days left <= 90-day check window (so it
        // IS checked), but > 30-day threshold (so no action) -> healthy.
        var uploaded = DateTime.UtcNow.AddDays(-1030);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30);

        Assert.Equal(1, report.BackupsChecked);
        Assert.Equal(1, report.BackupsHealthy);
        Assert.Equal(0, report.BackupsRefreshed);
        Assert.Equal(0, report.ArticlesRepublished);
        Assert.Equal(postsBefore, _server.PostCount); // nothing posted

        // Timestamp must be unchanged
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.Equal(uploaded, record.UploadedUtc, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MissingArticles_RepublishedWithNewIdentity()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();

        // Simulate article loss: remove one chunk's article from the server
        string lostChunk = fx.ChunkIds[0];
        string oldMessageId = ArticleCodec.MakeMessageId(lostChunk, RepoId);
        Assert.True(_server.Articles.TryRemove(oldMessageId, out _));

        // In the check window (65 days left) so the missing article is found.
        var uploaded = DateTime.UtcNow.AddDays(-1030);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);
        int postsBefore = _server.PostCount;

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsRefreshed);
        Assert.Equal(1, report.ArticlesMissing);
        Assert.Equal(1, report.ArticlesRepublished);
        Assert.Equal(postsBefore + 1, _server.PostCount);

        // New identity recorded in the index
        Assert.True(fx.MessageIndex.HasNewIdentity(lostChunk));
        string newId = fx.MessageIndex.GetMessageId(lostChunk, RepoId);
        Assert.NotEqual(oldMessageId, newId);
        Assert.Contains(".refresh.", newId);

        // New article is actually on the server
        Assert.True(fx.Store.ExistsOnServer(lostChunk));

        // Timestamp advanced (only after verification).
        // Reload the tracker: CheckAndRepost uses its own instance.
        var freshTracker = new UsenetUploadTracker(fx.Repo.RepoRoot);
        var record = freshTracker.GetAll().Single();
        Assert.True(record.UploadedUtc > uploaded);
    }

    [Fact]
    public void ExpiringBackup_AllChunksRepublishedWithNewIdentity()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();

        // Uploaded 1070 days ago; 25 days left <= 30-day threshold -> expiring
        var uploaded = DateTime.UtcNow.AddDays(-1070);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);
        int postsBefore = _server.PostCount;

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsRefreshed);
        Assert.Equal(0, report.ArticlesMissing); // nothing was missing...
        Assert.Equal(fx.ChunkIds.Count, report.ArticlesRepublished); // ...but all refreshed
        Assert.Equal(postsBefore + fx.ChunkIds.Count, _server.PostCount);

        // Every chunk now has a new identity
        foreach (string chunkId in fx.ChunkIds)
        {
            Assert.True(fx.MessageIndex.HasNewIdentity(chunkId));
            Assert.True(fx.Store.ExistsOnServer(chunkId));
        }

        // Timestamp advanced
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.True(record.UploadedUtc > uploaded);
    }

    [Fact]
    public void DryRun_MutatesNothing()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();

        // Expiring backup that WOULD be refreshed
        var uploaded = DateTime.UtcNow.AddDays(-1070);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);
        int postsBefore = _server.PostCount;

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30,
            sampleSize: 100, dryRun: true);

        Assert.True(report.DryRun);
        Assert.Equal(1, report.BackupsRefreshed); // reports what WOULD happen
        Assert.Equal(postsBefore, _server.PostCount); // but nothing posted
        Assert.Equal(0, report.ArticlesRepublished);

        // Index untouched
        foreach (string chunkId in fx.ChunkIds)
            Assert.False(fx.MessageIndex.HasNewIdentity(chunkId));

        // Timestamp untouched
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.Equal(uploaded, record.UploadedUtc, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ExistsOnServer_IgnoresJournal()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();

        string chunkId = fx.ChunkIds[0];

        // Journal-backed Exists returns true...
        Assert.True(fx.Store.Exists(chunkId));

        // ...but after the article vanishes from the server, the live check
        // must report it missing (the journal cannot prove availability).
        string messageId = ArticleCodec.MakeMessageId(chunkId, RepoId);
        Assert.True(_server.Articles.TryRemove(messageId, out _));

        Assert.False(fx.Store.ExistsOnServer(chunkId));
    }

    [Fact]
    public void RepublishWithNewIdentity_GetFindsNewArticle()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();

        string chunkId = fx.ChunkIds[0];
        byte[] blob = fx.Repo.GetChunkBlob(chunkId);

        // Lose the original
        string oldId = ArticleCodec.MakeMessageId(chunkId, RepoId);
        _server.Articles.TryRemove(oldId, out _);

        string newId = fx.Store.RepublishWithNewIdentity(chunkId, blob);
        Assert.NotEqual(oldId, newId);

        // Get resolves through the index to the new article
        Assert.Equal(blob, fx.Store.Get(chunkId));
    }

    [Fact]
    public void MakeRefreshMessageId_Unique()
    {
        string chunkId = new string('a', 64);
        string id1 = ArticleCodec.MakeRefreshMessageId(chunkId, RepoId);
        string id2 = ArticleCodec.MakeRefreshMessageId(chunkId, RepoId);
        Assert.NotEqual(id1, id2);
        Assert.Contains(chunkId, id1);
        Assert.Contains(".refresh.", id1);
    }
}
