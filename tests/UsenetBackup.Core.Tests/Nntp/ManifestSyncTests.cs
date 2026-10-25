using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Encrypted manifest upload/discovery: the USB recovery wizard's way to
/// find backups newer than the stick. Tests round-trip, wrong-passphrase
/// rejection, tamper rejection, and idempotent upload.
/// </summary>
public sealed class ManifestSyncTests : IDisposable
{
    private const string Passphrase = "manifest-test-passphrase";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;
    private const string Newsgroup = "alt.binaries.test";

    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly FakeNntpServer _server;

    public ManifestSyncTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "ub-manifest-" + Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(_repoDir, "src");
        Directory.CreateDirectory(_srcDir);
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello");
        BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations).Dispose();
        _server = new FakeNntpServer();
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
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

    private NntpBlobStore OpenStore(NntpClient client, BackupRepository repo, string? dbName = null) =>
        new(client, Newsgroup, repo.RepoId,
            Path.Combine(_repoDir, dbName ?? "journal.db"));

    [Fact]
    public void ManifestMessageId_RoundTrips()
    {
        string backupId = Guid.NewGuid().ToString("N");
        string repoId = "0123456789abcdef";
        string msgId = ArticleCodec.MakeManifestMessageId(backupId, repoId);
        Assert.StartsWith("<set.", msgId);
        Assert.Equal(backupId, ArticleCodec.TryParseManifestMessageId(msgId, repoId));
        // Wrong repo: not ours.
        Assert.Null(ArticleCodec.TryParseManifestMessageId(msgId, "ffffffffffffffff"));
        // Chunk IDs are not manifest IDs.
        Assert.Null(ArticleCodec.TryParseManifestMessageId(
            ArticleCodec.MakeMessageId(new string('a', 64), repoId), repoId));
    }

    [Fact]
    public void ManifestIndex_ListsOnlyBackupsDeliveredToThisProvider()
    {
        // SF-15: the monthly index used to list every LOCAL manifest,
        // so a provider advertised backups whose manifest article it
        // never received; discovery showed them, then silently skipped
        // them when selected.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var first = repo.BackupDirectory(_srcDir);
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello again");
        var second = repo.BackupDirectory(_srcDir);

        using var client = Connect();
        using var store = new NntpBlobStore(client, Newsgroup, repo.RepoId,
            Path.Combine(_repoDir, "journal.db"),
            providerKey: ChunkMessageIndex.MakeProviderKey("news.example.com", Newsgroup));

        string yearMonth = DateTime.UtcNow.ToString("yyyy-MM");
        // Upload only the FIRST backup's manifest: the index must name
        // it alone, even though the second manifest exists locally.
        repo.UploadManifest(first.BackupId, store);
        var index = store.GetLatestManifestIndex(yearMonth);
        Assert.NotNull(index);
        Assert.Equal(new[] { first.BackupId }, index);

        // Once the second is delivered too, the refreshed index lists both.
        repo.UploadManifest(second.BackupId, store);
        index = store.GetLatestManifestIndex(yearMonth);
        Assert.NotNull(index);
        Assert.Equal(2, index!.Count);
        Assert.Contains(first.BackupId, index);
        Assert.Contains(second.BackupId, index);
    }

    [Fact]
    public void UploadDiscover_RoundTrip()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        using var client = Connect();
        using var store = OpenStore(client, repo);
        repo.UploadManifest(manifest.BackupId, store);

        // Simulate a fresh USB: wipe local manifests, keep repo.json.
        foreach (string f in Directory.GetFiles(Path.Combine(_repoDir, "manifests")))
            File.Delete(f);

        var found = repo.DiscoverRemoteManifests(store);
        Assert.Single(found);
        Assert.Equal(manifest.BackupId, found[0].BackupId);
        Assert.Equal(manifest.RootSha256, found[0].Manifest.RootSha256);
    }

    [Fact]
    public void Upload_IsIdempotent()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        using var client = Connect();
        using var store = OpenStore(client, repo);
        repo.UploadManifest(manifest.BackupId, store);
        int afterFirst = _server.PostCount;
        repo.UploadManifest(manifest.BackupId, store);
        Assert.Equal(afterFirst, _server.PostCount); // STAT hit, no re-post
    }

    [Fact]
    public void Discover_WrongPassphrase_SkipsEverything()
    {
        // Behavior changed in Phase 3 (B-M1): discovery used to throw on
        // the first undecryptable manifest, which meant ONE forged or
        // squatted article aborted discovery for everyone. Per-entry
        // failures now skip (and log), so a wrong passphrase — every
        // entry failing authentication — yields an empty result instead
        // of an exception. Bad manifests are still never accepted.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        using var client = Connect();
        using var store = OpenStore(client, repo);
        repo.UploadManifest(manifest.BackupId, store);

        // Wipe local manifests; open with wrong passphrase.
        foreach (string f in Directory.GetFiles(Path.Combine(_repoDir, "manifests")))
            File.Delete(f);
        using var wrongRepo = BackupRepository.Open(_repoDir, "wrong-passphrase");
        using var wrongStore = OpenStore(client, wrongRepo, "journal2.db");
        var found = wrongRepo.DiscoverRemoteManifests(wrongStore);
        Assert.Empty(found);
    }

    [Fact]
    public void Discover_TamperedManifest_Skipped()
    {
        // Behavior changed in Phase 3 (B-M1): the tampered entry is
        // skipped (yEnc CRC catches it at parse time, before decrypt)
        // instead of aborting the whole discovery. IndexAuthenticationTests
        // covers the companion case: a genuine second backup alongside
        // the bad entry is still discovered.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        using var client = Connect();
        using var store = OpenStore(client, repo);
        repo.UploadManifest(manifest.BackupId, store);

        // Tamper with the stored article: corrupt the yEnc body significantly.
        string msgId = ArticleCodec.MakeManifestMessageId(manifest.BackupId, repo.RepoId);
        string article = _server.Articles[msgId];
        int bodyStart = article.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        // Replace 100 bytes of the body with 'X' — guarantees CRC failure.
        char[] chars = article.ToCharArray();
        for (int i = 0; i < 100 && bodyStart + i < chars.Length; i++)
            chars[bodyStart + i] = 'X';
        _server.Articles[msgId] = new string(chars);

        foreach (string f in Directory.GetFiles(Path.Combine(_repoDir, "manifests")))
            File.Delete(f);
        var found = repo.DiscoverRemoteManifests(store);
        Assert.Empty(found);
    }

    [Fact]
    public void Discover_SkipsLocalManifests()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        using var client = Connect();
        using var store = OpenStore(client, repo);
        repo.UploadManifest(manifest.BackupId, store);

        // Local manifest still present: nothing to discover.
        var found = repo.DiscoverRemoteManifests(store);
        Assert.Empty(found);
    }

    [Fact]
    public void PostManifest_JournalsAndSkipsRepost()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        using var client = Connect();
        using var store = OpenStore(client, repo);
        string msgId = ArticleCodec.MakeManifestMessageId(manifest.BackupId, repo.RepoId);

        byte[] fakeEncrypted = new byte[] { 1, 2, 3, 4 };
        store.PostManifest(manifest.BackupId, fakeEncrypted);
        int afterFirst = _server.PostCount;

        // Second post: journal fast path, no STAT, no re-post.
        store.PostManifest(manifest.BackupId, fakeEncrypted);
        Assert.Equal(afterFirst, _server.PostCount);
        Assert.True(_server.Articles.ContainsKey(msgId));
    }

    [Fact]
    public void PostManifest_AdoptsServerArticleIntoJournal()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        // Post with one store (and journal)...
        using var client = Connect();
        using var store = OpenStore(client, repo);
        byte[] fakeEncrypted = new byte[] { 5, 6, 7, 8 };
        store.PostManifest(manifest.BackupId, fakeEncrypted);
        int afterFirst = _server.PostCount;

        // ...then a fresh store with an empty journal sees the server's
        // article via STAT, adopts it into the journal, and doesn't re-post.
        using var store2 = OpenStore(client, repo, "journal-fresh.db");
        store2.PostManifest(manifest.BackupId, fakeEncrypted);
        Assert.Equal(afterFirst, _server.PostCount);
        // Third call now hits the journal fast path too.
        store2.PostManifest(manifest.BackupId, fakeEncrypted);
        Assert.Equal(afterFirst, _server.PostCount);
    }

    [Fact]
    public void UploadManifest_PreservesVolumesForStreamedRestore()
    {
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        // Simulate what the streaming scheduler path does: attach the
        // posted volumes to the manifest, then save + upload.
        manifest.Volumes = new List<VolumeEntry>
        {
            new() { Id = new string('a', 64), ChunkIds = new List<string> { new('b', 64) }, SizeBytes = 0 },
            new() { Id = new string('c', 64), ChunkIds = new List<string> { new('d', 64) }, SizeBytes = 0 },
        };
        manifest.VolumeSize = repo.VolumeSizeBytes;
        repo.SaveManifest(manifest);

        using var client = Connect();
        using var store = OpenStore(client, repo);
        repo.UploadManifest(manifest.BackupId, store);

        // Simulate a fresh machine: wipe local manifests, discover from Usenet.
        foreach (string f in Directory.GetFiles(Path.Combine(_repoDir, "manifests")))
            File.Delete(f);
        var found = repo.DiscoverRemoteManifests(store);
        Assert.Single(found);
        Assert.Equal(manifest.BackupId, found[0].BackupId);
        Assert.NotNull(found[0].Manifest.Volumes);
        Assert.Equal(2, found[0].Manifest.Volumes!.Count);
        Assert.Equal(new string('a', 64), found[0].Manifest.Volumes![0].Id);
        Assert.Equal(repo.VolumeSizeBytes, found[0].Manifest.VolumeSize);
    }
}
