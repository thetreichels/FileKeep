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

    private NntpClient Connect() => ConnectTo(_server);

    private static NntpClient ConnectTo(FakeNntpServer server)
    {
        var (clientStream, serverStream) = InMemoryTransport.Create();
        _ = Task.Run(() =>
        {
            using (serverStream)
                server.HandleConnection(serverStream, CancellationToken.None);
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
                messageIndex: MessageIndex,
                providerKey: ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup));
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
        // A missing sampled article triggers a FULL refresh: every chunk
        // gets a fresh identity so the retention-clock reset is honest.
        Assert.Equal(fx.ChunkIds.Count, report.ArticlesRepublished);
        // +1 for the message-index publication (published before the
        // retention clock is advanced).
        Assert.Equal(postsBefore + fx.ChunkIds.Count + 1, _server.PostCount);

        // New identity recorded in the index (per-provider)
        string providerKey = ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);
        Assert.True(fx.MessageIndex.HasNewIdentity(providerKey, lostChunk));
        string newId = fx.MessageIndex.GetMessageId(providerKey, lostChunk, RepoId);
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
        // +1 for the message-index publication.
        Assert.Equal(postsBefore + fx.ChunkIds.Count + 1, _server.PostCount);

        // Every chunk now has a new identity (per-provider)
        string providerKey = ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);
        foreach (string chunkId in fx.ChunkIds)
        {
            Assert.True(fx.MessageIndex.HasNewIdentity(providerKey, chunkId));
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
        string dryRunKey = ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);
        foreach (string chunkId in fx.ChunkIds)
            Assert.False(fx.MessageIndex.HasNewIdentity(dryRunKey, chunkId));

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
    public void IndexPublishFailure_TimestampNotAdvanced_RetryOnNextRun()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();

        // Expiring backup that WILL be refreshed...
        var uploaded = DateTime.UtcNow.AddDays(-1070);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        // ...but the message-identity index post is rejected by the server.
        _server.FailIndexPosts = true;

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        // Chunks WERE republished (server accepted those posts)...
        Assert.Equal(fx.ChunkIds.Count, report.ArticlesRepublished);
        // ...but the refresh is reported as FAILED because the refreshed
        // identities are not discoverable by other machines.
        Assert.Equal(0, report.BackupsRefreshed);
        Assert.NotEmpty(report.Errors);
        Assert.Contains(report.Errors, e => e.Contains("Index publish"));

        // The retention clock must NOT advance: the old timestamp stands so
        // the next run retries the full refresh including index publication.
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.Equal(uploaded, record.UploadedUtc, TimeSpan.FromSeconds(5));

        // Next run with a healthy server completes the refresh and advances
        // the clock (proving the retry path works end to end).
        _server.FailIndexPosts = false;
        var log2 = new List<string>();
        var report2 = CreateManager(log2).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);
        Assert.Equal(1, report2.BackupsRefreshed);
        var record2 = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.True(record2.UploadedUtc > uploaded);
    }

    [Fact]
    public void CheckAndRepost_UsesMatchingStorePerProvider()
    {
        using var serverB = new FakeNntpServer();
        using var repo = BackupRepository.Init(_repoDir, Passphrase, 64 * 1024, 10_000);
        var manifest = repo.BackupDirectory(_srcDir);
        var chunkIds = manifest.Files.SelectMany(f => f.Chunks).Distinct().ToList();

        const string hostB = "news.other.example";
        // Share the repo's index instance with the stores, exactly like the
        // scheduler does: republication records into it and CheckBackup
        // publishes repo.MessageIndex, so they must be the same object.
        var messageIndex = repo.MessageIndex;

        using var clientA = ConnectTo(_server);
        using var clientB = ConnectTo(serverB);
        using var storeA = new NntpBlobStore(clientA, Newsgroup, RepoId,
            Path.Combine(_workDir, "journal-a.db"),
            messageIndex: messageIndex,
            providerKey: ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup));
        using var storeB = new NntpBlobStore(clientB, Newsgroup, RepoId,
            Path.Combine(_workDir, "journal-b.db"),
            messageIndex: messageIndex,
            providerKey: ChunkMessageIndex.MakeProviderKey(hostB, Newsgroup));

        // Original upload to BOTH providers.
        foreach (string chunkId in chunkIds)
        {
            byte[] blob = repo.GetChunkBlob(chunkId);
            storeA.Put(chunkId, blob);
            storeB.Put(chunkId, blob);
        }
        int postsBeforeA = _server.PostCount;
        int postsBeforeB = serverB.PostCount;

        // Both records expiring (25 days left) so both get refreshed.
        var uploaded = DateTime.UtcNow.AddDays(-1070);
        var tracker = new UsenetUploadTracker(_repoDir);
        tracker.RecordUpload(manifest.BackupId, ProviderHost, Newsgroup, uploaded);
        tracker.RecordUpload(manifest.BackupId, hostB, Newsgroup, uploaded);

        var requestedHosts = new List<string>();
        NntpBlobStore Resolve(UsenetUploadTracker.UploadRecord record)
        {
            requestedHosts.Add(record.ProviderHost);
            return string.Equals(record.ProviderHost, ProviderHost, StringComparison.OrdinalIgnoreCase)
                ? storeA
                : string.Equals(record.ProviderHost, hostB, StringComparison.OrdinalIgnoreCase)
                    ? storeB
                    : throw new InvalidOperationException($"unexpected host {record.ProviderHost}");
        }

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            repo, (Func<UsenetUploadTracker.UploadRecord, NntpBlobStore>)Resolve,
            warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(2, report.BackupsChecked);
        Assert.Equal(2, report.BackupsRefreshed);
        Assert.Empty(report.Errors);
        Assert.Contains(ProviderHost, requestedHosts);
        Assert.Contains(hostB, requestedHosts);

        // Each provider refreshed through its OWN connection: all chunks
        // republished plus one message-index post per provider.
        Assert.Equal(chunkIds.Count + 1, _server.PostCount - postsBeforeA);
        Assert.Equal(chunkIds.Count + 1, serverB.PostCount - postsBeforeB);

        // New identities recorded under each provider's own key...
        string keyA = ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);
        string keyB = ChunkMessageIndex.MakeProviderKey(hostB, Newsgroup);
        foreach (string chunkId in chunkIds)
        {
            Assert.True(messageIndex.HasNewIdentity(keyA, chunkId));
            Assert.True(messageIndex.HasNewIdentity(keyB, chunkId));
            // ...and the identities differ per provider.
            Assert.NotEqual(
                messageIndex.GetMessageId(keyA, chunkId, RepoId),
                messageIndex.GetMessageId(keyB, chunkId, RepoId));
        }

        // Each provider's timestamp advanced only after its own refresh.
        var fresh = new UsenetUploadTracker(_repoDir);
        Assert.True(fresh.GetAll().Single(r => r.ProviderHost == ProviderHost).UploadedUtc > uploaded);
        Assert.True(fresh.GetAll().Single(r => r.ProviderHost == hostB).UploadedUtc > uploaded);

        // The index published during B's refresh must contain BOTH
        // providers' refreshed identities — guards against a stale
        // repo.MessageIndex when several providers refresh in one run.
        string? publishedJson = storeB.GetLatestMessageIndex();
        Assert.NotNull(publishedJson);
        string publishedDir = Path.Combine(_workDir, "published-check");
        Directory.CreateDirectory(publishedDir);
        var published = new ChunkMessageIndex(publishedDir);
        published.LoadFromJson(publishedJson);
        foreach (string chunkId in chunkIds)
        {
            Assert.True(published.HasNewIdentity(keyA, chunkId));
            Assert.True(published.HasNewIdentity(keyB, chunkId));
        }
    }

    [Fact]
    public void CheckAndRepost_UnknownProviderRecord_RecordedAsError()
    {
        using var repo = BackupRepository.Init(_repoDir, Passphrase, 64 * 1024, 10_000);
        var manifest = repo.BackupDirectory(_srcDir);
        var chunkIds = manifest.Files.SelectMany(f => f.Chunks).Distinct().ToList();

        var messageIndex = new ChunkMessageIndex(_repoDir);
        using var client = Connect();
        using var store = new NntpBlobStore(client, Newsgroup, RepoId,
            Path.Combine(_workDir, "journal.db"),
            messageIndex: messageIndex,
            providerKey: ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup));
        foreach (string chunkId in chunkIds)
            store.Put(chunkId, repo.GetChunkBlob(chunkId));
        int postsBefore = _server.PostCount;

        var uploaded = DateTime.UtcNow.AddDays(-1070);
        var tracker = new UsenetUploadTracker(_repoDir);
        tracker.RecordUpload(manifest.BackupId, ProviderHost, Newsgroup, uploaded);
        tracker.RecordUpload(manifest.BackupId, "gone.example.com", Newsgroup, uploaded);

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            repo,
            record => string.Equals(record.ProviderHost, ProviderHost, StringComparison.OrdinalIgnoreCase)
                ? store
                : throw new InvalidOperationException(
                    $"No NNTP provider configured for host '{record.ProviderHost}'."),
            warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(2, report.BackupsChecked);
        Assert.Equal(1, report.BackupsRefreshed); // known provider still refreshed
        Assert.Single(report.Errors);
        Assert.Contains("gone.example.com", report.Errors[0]);
        Assert.Equal(chunkIds.Count + 1, _server.PostCount - postsBefore);

        // Known provider's timestamp advanced after its own refresh...
        var fresh = new UsenetUploadTracker(_repoDir);
        Assert.True(fresh.GetAll().Single(r => r.ProviderHost == ProviderHost).UploadedUtc > uploaded);
        // ...unknown provider's timestamp untouched (never refreshed through
        // another provider's connection).
        Assert.Equal(uploaded,
            fresh.GetAll().Single(r => r.ProviderHost == "gone.example.com").UploadedUtc,
            TimeSpan.FromSeconds(5));
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
