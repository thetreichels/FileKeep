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
        Assert.StartsWith("<manifest.", msgId);
        Assert.Equal(backupId, ArticleCodec.TryParseManifestMessageId(msgId, repoId));
        // Wrong repo: not ours.
        Assert.Null(ArticleCodec.TryParseManifestMessageId(msgId, "ffffffffffffffff"));
        // Chunk IDs are not manifest IDs.
        Assert.Null(ArticleCodec.TryParseManifestMessageId(
            ArticleCodec.MakeMessageId(new string('a', 64), repoId), repoId));
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
    public void Discover_WrongPassphrase_Throws()
    {
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
        var ex = Assert.Throws<InvalidDataException>(() =>
            wrongRepo.DiscoverRemoteManifests(wrongStore));
        Assert.Contains("authentication", ex.Message);
    }

    [Fact]
    public void Discover_TamperedManifest_Throws()
    {
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
        // yEnc CRC catches it at parse time (fails closed before decrypt).
        Assert.Throws<InvalidDataException>(() => repo.DiscoverRemoteManifests(store));
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
}
