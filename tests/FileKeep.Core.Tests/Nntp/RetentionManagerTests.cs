using FileKeep.Core;
using FileKeep.Core.Nntp;
using FileKeep.Core.Redundancy;
using Xunit;

namespace FileKeep.Core.Tests.Nntp;

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

        /// <summary>
        /// Simulates the original upload: posts all chunks AND the
        /// manifest article (+ monthly index), as every real upload
        /// path does. (SF-10: retention now health-checks the manifest
        /// article, so a fixture without one reads as a backup whose
        /// manifest already expired.)
        /// </summary>
        public void UploadAll()
        {
            foreach (string chunkId in ChunkIds)
                Store.Put(chunkId, Repo.GetChunkBlob(chunkId));
            Repo.UploadManifest(Manifest.BackupId, Store);
        }

        public void Dispose()
        {
            Store.Dispose();
            _client.Dispose();
            Repo.Dispose();
        }
    }

    /// <summary>
    /// Volume-mode fixture: same repo shape as <see cref="Fixture"/>, but
    /// the backup is packed into ONE volume (all chunks), the volume is
    /// posted to the server, and the manifest is stamped with the volume
    /// entry — the state a volume-packed upload leaves behind.
    /// </summary>
    private sealed class VolumeFixture : IDisposable
    {
        public BackupRepository Repo { get; }
        public NntpBlobStore Store { get; }
        public ChunkMessageIndex MessageIndex { get; }
        public UsenetUploadTracker Tracker { get; }
        public BackupManifest Manifest { get; }
        public List<VolumeEntry> Volumes { get; }
        public List<string> ChunkIds { get; }
        private readonly NntpClient _client;

        public VolumeFixture(RetentionManagerTests outer)
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

            // One volume holding every chunk: a single corrupt chunk then
            // decides the whole volume's republication fate.
            var groups = VolumePacker.Plan(ChunkIds, Repo.GetChunkBlobSize, 16 * 1024 * 1024);
            Assert.Single(groups);
            var entries = new List<VolumeEntry>();
            foreach (var group in groups)
            {
                var volume = VolumePacker.BuildVolume(group, Repo.GetChunkBlob);
                Store.PutVolume(volume.Id, volume.Bytes);
                entries.Add(new VolumeEntry
                {
                    Id = volume.Id,
                    ChunkIds = volume.ChunkIds.ToList(),
                    SizeBytes = volume.Bytes.Length,
                });
            }
            Volumes = entries;
            Manifest.Volumes = entries;
            Repo.SaveManifest(Manifest);

            // Simulate the rest of a real volume upload (SF-10 fixture
            // fidelity): the single-set NFO article and the manifest
            // article (+ monthly index) are posted too.
            string nfoText = ArticleCodec.BuildNfoText(
                Manifest.BackupId, Manifest.CreatedUtc.ToString("yyyy-MM-dd"),
                entries.Count, Repo.GetOrCreatePoster());
            Store.PostNfo(Manifest.BackupId, nfoText,
                ArticleCodec.SetName(Manifest.BackupId), entries.Count + 1);
            Repo.UploadManifest(Manifest.BackupId, Store);
        }

        public void Dispose()
        {
            Store.Dispose();
            _client.Dispose();
            Repo.Dispose();
        }
    }

    private static void CorruptChunkBlob(string repoDir, string chunkId)
    {
        string path = Path.Combine(repoDir, "chunks", chunkId[..2], chunkId[2..]);
        byte[] blob = File.ReadAllBytes(path);
        blob[blob.Length / 2] ^= 0xFF; // flip one ciphertext byte: GCM must reject it
        File.WriteAllBytes(path, blob);
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
        // retention clock is advanced) and +1 for the monthly
        // manifest-index republication (SF-10 discovery refresh).
        Assert.Equal(postsBefore + fx.ChunkIds.Count + 2, _server.PostCount);

        // New identity recorded in the index (per-provider)
        string providerKey = ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);
        Assert.True(fx.MessageIndex.HasNewIdentity(providerKey, lostChunk));
        string newId = fx.MessageIndex.GetMessageId(providerKey, lostChunk, RepoId);
        Assert.NotEqual(oldMessageId, newId);
        Assert.Contains(".re.", newId);

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
        // +1 for the message-index publication, +1 for the monthly
        // manifest-index republication (SF-10).
        Assert.Equal(postsBefore + fx.ChunkIds.Count + 2, _server.PostCount);

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
        repo.UploadManifest(manifest.BackupId, storeA);
        repo.UploadManifest(manifest.BackupId, storeB);
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
        // republished plus one message-index post and one monthly
        // manifest-index post (SF-10) per provider.
        Assert.Equal(chunkIds.Count + 2, _server.PostCount - postsBeforeA);
        Assert.Equal(chunkIds.Count + 2, serverB.PostCount - postsBeforeB);

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
        repo.UploadManifest(manifest.BackupId, store);
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
        Assert.Equal(chunkIds.Count + 2, _server.PostCount - postsBefore);

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
    public void CorruptLocalBlob_VolumeRepublish_Fails_ServerArticleStaysAuthoritative()
    {
        // DL-7: a locally corrupted chunk blob must NOT be packed and
        // republished. Before the fix, BuildVolume packed raw blobs with
        // no integrity check, so the corrupt volume was posted, the
        // message index repointed to it, and the retention timestamp
        // advanced — orphaning the healthy server article.
        using var fx = new VolumeFixture(this);
        string volumeId = fx.Volumes.Single().Id;
        byte[] serverVolumeBytes = fx.Store.GetVolume(volumeId);

        // Expiring (25 days left) so a full republish is attempted.
        var uploaded = DateTime.UtcNow.AddDays(-1070);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        CorruptChunkBlob(_repoDir, fx.ChunkIds[0]);
        int postsBefore = _server.PostCount;

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        // The refresh failed and the failure is visible in the report.
        Assert.Equal(0, report.BackupsRefreshed);
        Assert.Equal(0, report.ArticlesRepublished);
        Assert.NotEmpty(report.Errors);
        Assert.Contains(report.Errors, e => e.Contains("Republish"));

        // Nothing was posted: no republished volume, no index publication.
        Assert.Equal(postsBefore, _server.PostCount);

        // The message index was NOT repointed for the volume.
        string providerKey = ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);
        Assert.False(fx.MessageIndex.HasNewIdentity(providerKey, volumeId));

        // The retention timestamp did NOT advance.
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.Equal(uploaded, record.UploadedUtc, TimeSpan.FromSeconds(5));

        // The healthy server article is untouched and still authoritative.
        Assert.True(fx.Store.ExistsOnServer(volumeId));
        Assert.Equal(serverVolumeBytes, fx.Store.GetVolume(volumeId));
    }

    [Fact]
    public void CorruptLocalChunk_ChunkRepublish_Fails_TimestampUnchanged()
    {
        // DL-7, per-chunk path: same guard as the volume path — a corrupt
        // local blob fails the refresh instead of being republished under
        // a new identity over the healthy server copy.
        using var fx = new Fixture(this);
        fx.UploadAll();

        var uploaded = DateTime.UtcNow.AddDays(-1070);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        string corruptChunk = fx.ChunkIds[0];
        CorruptChunkBlob(_repoDir, corruptChunk);

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(0, report.BackupsRefreshed);
        Assert.NotEmpty(report.Errors);
        Assert.Contains(report.Errors, e => e.Contains("Republish"));

        // The corrupted chunk was NOT repointed; its original server
        // article remains the authoritative copy.
        string providerKey = ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);
        Assert.False(fx.MessageIndex.HasNewIdentity(providerKey, corruptChunk));
        Assert.True(fx.Store.ExistsOnServer(corruptChunk));

        // The retention timestamp did NOT advance.
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.Equal(uploaded, record.UploadedUtc, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MissingLocalChunks_VolumeRepublish_FallsBackToServerDownload()
    {
        // DL-7 guard rail: chunks pruned locally after upload are NOT
        // corruption — the volume must still refresh from the server
        // copy (byte-identical), exactly as before the fix.
        using var fx = new VolumeFixture(this);
        string volumeId = fx.Volumes.Single().Id;

        var uploaded = DateTime.UtcNow.AddDays(-1070);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        foreach (string chunkId in fx.ChunkIds)
            File.Delete(Path.Combine(_repoDir, "chunks", chunkId[..2], chunkId[2..]));
        int postsBefore = _server.PostCount;

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsRefreshed);
        Assert.Equal(1, report.ArticlesRepublished);
        Assert.Empty(report.Errors);
        // One volume republish + one message-index publication + one
        // monthly manifest-index republication (SF-10).
        Assert.Equal(postsBefore + 3, _server.PostCount);

        string providerKey = ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);
        Assert.True(fx.MessageIndex.HasNewIdentity(providerKey, volumeId));
        Assert.True(fx.Store.ExistsOnServer(volumeId));

        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.True(record.UploadedUtc > uploaded);
    }

    [Fact]
    public void MakeRefreshMessageId_Unique()
    {
        string chunkId = new string('a', 64);
        string id1 = ArticleCodec.MakeRefreshMessageId(chunkId, RepoId);
        string id2 = ArticleCodec.MakeRefreshMessageId(chunkId, RepoId);
        Assert.NotEqual(id1, id2);
        Assert.Contains(chunkId, id1);
        Assert.Contains(".re.", id1);
    }

    [Fact]
    public void MissingManifestArticle_TriggersRefresh_AndIsReposted()
    {
        // SF-10: the manifest article was invisible to retention — it
        // could expire while every data article stayed fresh, leaving
        // the backup undiscoverable. Now it is health-checked like a
        // data article, and a refresh reposts it.
        using var fx = new Fixture(this);
        fx.UploadAll();
        Assert.NotNull(fx.Store.GetManifest(fx.Manifest.BackupId));

        // The manifest article expires on the server.
        string manifestId = ArticleCodec.MakeManifestMessageId(fx.Manifest.BackupId, RepoId);
        Assert.True(_server.Articles.TryRemove(manifestId, out _));
        Assert.Null(fx.Store.GetManifest(fx.Manifest.BackupId));

        // Otherwise healthy: 65 days left (inside the check window,
        // above the repost threshold), every data article present.
        // Only the missing manifest can trigger the refresh.
        var uploaded = DateTime.UtcNow.AddDays(-1030);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsRefreshed);
        Assert.Equal(1, report.ArticlesMissing); // the manifest counts as missing
        Assert.Contains(log, l => l.Contains("manifest article MISSING"));
        // The manifest is fetchable again, and the clock advanced.
        Assert.NotNull(fx.Store.GetManifest(fx.Manifest.BackupId));
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.True(record.UploadedUtc > uploaded);
    }

    [Fact]
    public void PrunedLocalChunk_ChunkMode_FallsBackToServerDownload()
    {
        // SF-10: chunk mode had no server-download fallback (volume
        // mode did), so a pruned chunk-mode backup threw on every
        // republish and aged to expiry. The local blob is gone here;
        // the refresh must download it back and republish it.
        using var fx = new Fixture(this);
        fx.UploadAll();

        var uploaded = DateTime.UtcNow.AddDays(-1070); // 25 days left: expiring
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        string prunedChunk = fx.ChunkIds[0];
        File.Delete(Path.Combine(_repoDir, "chunks", prunedChunk[..2], prunedChunk[2..]));

        var log = new List<string>();
        var report = CreateManager(log).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsRefreshed);
        Assert.Equal(fx.ChunkIds.Count, report.ArticlesRepublished);
        Assert.Empty(report.Errors);
        Assert.Contains(log, l => l.Contains("downloading from server for republish"));

        // The pruned chunk was republished under a new identity, and
        // its bytes resolve through the index.
        string providerKey = ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);
        Assert.True(fx.MessageIndex.HasNewIdentity(providerKey, prunedChunk));
        Assert.NotEmpty(fx.Store.Get(prunedChunk));
    }

    private static string ProviderKey =>
        ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup);

    /// <summary>
    /// Simulates a completed parity upload for the chunk-mode Fixture:
    /// generates the backup's parity blocks over the canonical chunk
    /// order, posts them, and stamps the manifest's parity record
    /// exactly as the upload paths now do (P2-V2a). Returns the parity
    /// block IDs.
    /// </summary>
    private List<string> SimulateParityUpload(Fixture fx, string mode)
    {
        string[] ordered = ChunkOrdering.GetOrderedChunkIds(fx.Manifest);
        List<List<string>> groups;
        IReadOnlyDictionary<string, byte[]> blocks;
        if (mode == "xor")
        {
            groups = XorParity.GroupsFor(ordered, 10);
            blocks = XorParity.GenerateParity(ordered, fx.Repo.GetChunkBlob, null, 10);
        }
        else
        {
            groups = Par2Redundancy.GroupsFor(ordered);
            blocks = Par2Redundancy.GenerateParity(ordered, fx.Repo.GetChunkBlob);
        }
        foreach (var (id, bytes) in blocks)
            fx.Store.Put(id, bytes);
        fx.Manifest.ParityByProvider = new Dictionary<string, ManifestParityRecord>
        {
            [ProviderKey] = new ManifestParityRecord
            {
                Mode = mode,
                GroupSize = 10,
                Groups = groups,
            },
        };
        fx.Repo.SaveManifest(fx.Manifest);
        return blocks.Keys.ToList();
    }

    [Fact]
    public void MissingParity_DataHealthy_RepairedOnly_TimestampUnchanged()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();
        var parityIds = SimulateParityUpload(fx, "xor");
        string parityId = Assert.Single(parityIds);

        // Parity article lost early; the data is untouched.
        Assert.True(_server.Articles.TryRemove(
            ArticleCodec.MakeMessageId(parityId, RepoId), out _));

        // 65 days left: checked, but inside the healthy window.
        var uploaded = DateTime.UtcNow.AddDays(-1030);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);
        int postsBefore = _server.PostCount;

        var report = CreateManager(new List<string>()).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsRefreshed);
        Assert.Equal(1, report.ArticlesRepublished); // just the parity block
        // +1 parity post, +1 message-identity index publication.
        Assert.Equal(postsBefore + 2, _server.PostCount);

        // Parity is fetchable again, under a NEW identity; the data
        // articles were not touched.
        Assert.True(fx.MessageIndex.HasNewIdentity(ProviderKey, parityId));
        Assert.True(fx.Store.ExistsOnServer(parityId));
        Assert.NotEmpty(fx.Store.Get(parityId));
        foreach (string chunkId in fx.ChunkIds)
            Assert.False(fx.MessageIndex.HasNewIdentity(ProviderKey, chunkId));

        // The retention clock tracks DATA freshness: unmoved by a
        // parity-only repair.
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.Equal(uploaded, record.UploadedUtc, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ExpiringBackup_ParityRepublishedWithNewIdentity()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();
        var parityIds = SimulateParityUpload(fx, "xor");

        // 25 days left <= threshold: full refresh.
        var uploaded = DateTime.UtcNow.AddDays(-1070);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);
        int postsBefore = _server.PostCount;

        var report = CreateManager(new List<string>()).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsRefreshed);
        // Every chunk AND every parity block republished.
        Assert.Equal(fx.ChunkIds.Count + parityIds.Count, report.ArticlesRepublished);
        // + index publication + monthly manifest-index republication.
        Assert.Equal(postsBefore + fx.ChunkIds.Count + parityIds.Count + 2,
            _server.PostCount);
        foreach (string parityId in parityIds)
        {
            Assert.True(fx.MessageIndex.HasNewIdentity(ProviderKey, parityId));
            Assert.True(fx.Store.ExistsOnServer(parityId));
        }

        // The clock advanced — parity rode the same transaction.
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.True(record.UploadedUtc > uploaded);
    }

    [Fact]
    public void MissingParity_Par2Blocks_Repaired()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();
        var parityIds = SimulateParityUpload(fx, "par2");
        Assert.Equal(3, parityIds.Count);

        // Lose ONE of the three PAR2 blocks.
        Assert.True(_server.Articles.TryRemove(
            ArticleCodec.MakeMessageId(parityIds[1], RepoId), out _));

        var uploaded = DateTime.UtcNow.AddDays(-1030);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        var report = CreateManager(new List<string>()).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsRefreshed);
        // A sampled miss regenerates ALL parity (the sample is a window).
        Assert.Equal(3, report.ArticlesRepublished);
        foreach (string parityId in parityIds)
        {
            Assert.True(fx.MessageIndex.HasNewIdentity(ProviderKey, parityId));
            Assert.True(fx.Store.ExistsOnServer(parityId));
            Assert.NotEmpty(fx.Store.Get(parityId));
        }
        var record = new UsenetUploadTracker(fx.Repo.RepoRoot).GetAll().Single();
        Assert.Equal(uploaded, record.UploadedUtc, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ParityRecordNone_HealthyBackup_NoAction()
    {
        using var fx = new Fixture(this);
        fx.UploadAll();
        fx.Manifest.ParityByProvider = new Dictionary<string, ManifestParityRecord>
        {
            [ProviderKey] = new ManifestParityRecord { Mode = "none", GroupSize = 10 },
        };
        fx.Repo.SaveManifest(fx.Manifest);
        int postsBefore = _server.PostCount;

        var uploaded = DateTime.UtcNow.AddDays(-1030);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        var report = CreateManager(new List<string>()).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsHealthy);
        Assert.Equal(0, report.ArticlesRepublished);
        Assert.Equal(postsBefore, _server.PostCount);
    }

    /// <summary>
    /// Volume-mode fixture with TWO post sets (batches) of one volume
    /// each, per-batch NFOs posted with recorded dates that differ from
    /// the backup's creation date — the state a batched upload leaves
    /// behind (P2-V2b).
    /// </summary>
    private sealed class BatchedVolumeFixture : IDisposable
    {
        public BackupRepository Repo { get; }
        public NntpBlobStore Store { get; }
        public UsenetUploadTracker Tracker { get; }
        public BackupManifest Manifest { get; }
        public List<VolumeEntry> Volumes { get; }
        private readonly NntpClient _client;

        public const string Batch0Date = "2026-01-15";
        public const string Batch1Date = "2026-01-16";

        public BatchedVolumeFixture(RetentionManagerTests outer)
        {
            Repo = BackupRepository.Init(outer._repoDir, Passphrase, 64 * 1024, 10_000);
            Manifest = Repo.BackupDirectory(outer._srcDir);
            var chunkIds = Manifest.Files.SelectMany(f => f.Chunks).Distinct().ToList();
            Assert.Equal(2, chunkIds.Count);

            _client = outer.Connect();
            var messageIndex = new ChunkMessageIndex(outer._repoDir);
            Store = new NntpBlobStore(_client, Newsgroup, RepoId,
                Path.Combine(outer._workDir, "journal.db"),
                messageIndex: messageIndex,
                providerKey: ChunkMessageIndex.MakeProviderKey(ProviderHost, Newsgroup));
            Tracker = new UsenetUploadTracker(outer._repoDir);

            var entries = new List<VolumeEntry>();
            for (int batch = 0; batch < 2; batch++)
            {
                var volume = VolumePacker.BuildVolume(
                    new[] { chunkIds[batch] }, Repo.GetChunkBlob);
                string setName = ArticleCodec.SetName(Manifest.BackupId, batch);
                Store.PutVolume(volume.Id, volume.Bytes, setName, 2, 2);
                entries.Add(new VolumeEntry
                {
                    Id = volume.Id,
                    ChunkIds = volume.ChunkIds.ToList(),
                    SizeBytes = volume.Bytes.Length,
                    SetName = setName,
                });
                string nfoText = ArticleCodec.BuildNfoText(
                    Manifest.BackupId, batch == 0 ? Batch0Date : Batch1Date,
                    1, Repo.GetOrCreatePoster());
                Store.PostNfo(Manifest.BackupId, batch, nfoText, setName, 2);
            }
            Volumes = entries;
            Manifest.Volumes = entries;
            Manifest.NfoDates = new List<string?> { Batch0Date, Batch1Date };
            Repo.SaveManifest(Manifest);
            Repo.UploadManifest(Manifest.BackupId, Store);
        }

        public void Dispose()
        {
            Store.Dispose();
            _client.Dispose();
            Repo.Dispose();
        }
    }

    [Fact]
    public void BatchedNfo_MissingBatchNfo_RepostedIdentical()
    {
        using var fx = new BatchedVolumeFixture(this);

        string nfo0Id = ArticleCodec.MakeNfoMessageId(fx.Manifest.BackupId, 0, RepoId);
        string nfo1Id = ArticleCodec.MakeNfoMessageId(fx.Manifest.BackupId, 1, RepoId);
        Assert.True(_server.Articles.TryGetValue(nfo0Id, out string? originalNfo0));
        Assert.True(_server.Articles.TryGetValue(nfo1Id, out string? originalNfo1));
        string originalText1 = ArticleCodec.ParseNfoArticle(originalNfo1!, fx.Manifest.BackupId);
        Assert.Contains(BatchedVolumeFixture.Batch1Date, originalText1);

        // Lose batch 1's NFO, and one data volume to trigger a refresh.
        Assert.True(_server.Articles.TryRemove(nfo1Id, out _));
        Assert.True(_server.Articles.TryRemove(
            ArticleCodec.MakeVolumeMessageId(fx.Volumes[0].Id, RepoId), out _));

        var uploaded = DateTime.UtcNow.AddDays(-1030);
        fx.Tracker.RecordUpload(fx.Manifest.BackupId, ProviderHost, Newsgroup, uploaded);

        var report = CreateManager(new List<string>()).CheckAndRepost(
            fx.Repo, fx.Store, warnDays: 90, repostThresholdDays: 30, sampleSize: 100);

        Assert.Equal(1, report.BackupsRefreshed);
        // Batch 1's NFO is back with identical text — the recorded
        // posting date was used, not the creation-date fallback
        // (which would carry today's date).
        Assert.True(_server.Articles.TryGetValue(nfo1Id, out string? repostedNfo1));
        string repostedText1 = ArticleCodec.ParseNfoArticle(repostedNfo1!, fx.Manifest.BackupId);
        Assert.Equal(originalText1, repostedText1);
        // Batch 0's NFO was never lost and is untouched.
        Assert.True(_server.Articles.TryGetValue(nfo0Id, out string? currentNfo0));
        Assert.Equal(originalNfo0, currentNfo0);
    }

    [Fact]
    public void SampleChunks_RotationSweepsTheWholeList()
    {
        // SF-11: a fixed sample window checked the same articles
        // forever. Different rotation seeds must give different
        // windows, and the windows together must cover the whole list.
        var ids = Enumerable.Range(0, 100).Select(i => $"id-{i:D3}").ToList();

        var first = RetentionManager.SampleChunks(ids, 10, rotationSeed: 0);
        Assert.Equal(10, first.Count);
        Assert.Equal(first.Count, first.Distinct().Count());
        // Deterministic for a given seed.
        Assert.Equal(first, RetentionManager.SampleChunks(ids, 10, rotationSeed: 0));
        // A different seed gives a different window.
        Assert.NotEqual(first, RetentionManager.SampleChunks(ids, 10, rotationSeed: 1));

        var covered = new HashSet<string>();
        for (int seed = 0; seed < 100; seed++)
            covered.UnionWith(RetentionManager.SampleChunks(ids, 10, seed));
        Assert.Equal(ids.Count, covered.Count);
    }
}
