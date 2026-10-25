using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Phase 3 regression tests for B-H1 / B-M1: the versioned Usenet
/// indexes (chunk message-identity index, monthly manifest index) are
/// HMAC-authenticated with the repo key. A store carrying an
/// <see cref="IndexAuth"/> must treat an unsigned, forged, tampered, or
/// wrong-key index as NOT THERE — never adopt it, never persist it —
/// while authentic indexes keep round-tripping.
/// </summary>
public sealed class ChunkIndexAuthenticationTests : IDisposable
{
    private const string RepoId = "0123456789abcdef";
    private const string Newsgroup = "alt.binaries.test";

    private static readonly byte[] KeyA =
        Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
    private static readonly byte[] KeyB =
        Enumerable.Range(0, 32).Select(i => (byte)(0x50 + i)).ToArray();

    private readonly string _workDir;
    private readonly FakeNntpServer _server;

    public ChunkIndexAuthenticationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-idxauth-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _server = new FakeNntpServer();
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private NntpClient ConnectClient()
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

    private NntpBlobStore CreateStore(IndexAuth? auth, string dbName = "catalog.db") =>
        new(ConnectClient(), Newsgroup, RepoId,
            Path.Combine(_workDir, dbName), indexAuth: auth);

    [Fact]
    public void ForgedChunkIndex_HigherVersion_NeverAdopted()
    {
        // B-H1: the victim posts an authenticated v1; an attacker who
        // knows only the (public) repo ID posts a forged v2 through the
        // real protocol path. Highest-version-wins must not hand the
        // victim's sync a poisoned, persisted index.
        using var victim = CreateStore(IndexAuth.FromRepoKey(KeyA));
        string legitJson = """{"h/g":{"aaa":"<aaa@invalid>"}}""";
        Assert.Equal(1, victim.PostMessageIndex(legitJson));
        // The fetched form is the canonical re-serialization; compare
        // through the parser, not as raw strings.
        string? fetched = victim.GetLatestMessageIndex();
        Assert.NotNull(fetched);
        string probeDir = Path.Combine(_workDir, "probe");
        Directory.CreateDirectory(probeDir);
        var probe = new ChunkMessageIndex(probeDir);
        probe.LoadFromJson(fetched!);
        Assert.Equal("<aaa@invalid>", probe.GetMessageId("h/g", "aaa", RepoId));

        using var attacker = CreateStore(null, "attacker.db");
        string forgedJson = """{"h/g":{"aaa":"<evil@invalid>"}}""";
        Assert.Equal(2, attacker.PostMessageIndex(forgedJson));

        // The forged latest version reads as NOT THERE for the keyed store.
        Assert.Null(victim.GetLatestMessageIndex());

        // Sync no-ops: the local index keeps exactly its prior content...
        var local = new ChunkMessageIndex(_workDir);
        local.LoadFromJson(legitJson);
        victim.SyncMessageIndex(local); // must not throw
        Assert.Equal("<aaa@invalid>", local.GetMessageId("h/g", "aaa", RepoId));

        // ...and nothing forged was persisted: a fresh instance agrees.
        var reread = new ChunkMessageIndex(_workDir);
        Assert.Equal("<aaa@invalid>", reread.GetMessageId("h/g", "aaa", RepoId));
    }

    [Fact]
    public void TamperedChunkIndex_StaleHmac_Rejected()
    {
        // Same attack, one level up: fetch the authentic envelope,
        // flip one mapping inside it, and repost as the next version
        // with the ORIGINAL sig — now stale for the payload.
        using var victim = CreateStore(IndexAuth.FromRepoKey(KeyA));
        victim.PostMessageIndex("""{"h/g":{"aaa":"<aaa@invalid>"}}""");

        using var raw = ConnectClient();
        string msgId = ArticleCodec.MakeMessageIndexMessageId(RepoId, 1);
        string article = raw.GetArticle(msgId)!;
        string body = article[(article.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(body.Trim())!.AsObject();
        Assert.NotNull(envelope["sig"]);
        envelope["data"]!["h/g"]!["aaa"] = "<evil@invalid>";
        string tampered = envelope.ToJsonString();

        using var attacker = CreateStore(null, "attacker.db");
        Assert.Equal(2, attacker.PostMessageIndex(tampered));

        Assert.Null(victim.GetLatestMessageIndex());

        var local = new ChunkMessageIndex(_workDir);
        victim.SyncMessageIndex(local); // no-op, no throw
        // No record adopted: lookups fall back to the deterministic ID.
        Assert.Equal(ArticleCodec.MakeMessageId("aaa", RepoId),
            local.GetMessageId("h/g", "aaa", RepoId));
    }

    [Fact]
    public void AuthenticatedChunkIndex_RoundTripsBetweenStores()
    {
        // Legit path: one machine posts its index authenticated; a
        // fresh machine with the same key syncs the mappings in.
        string chunkId = new string('c', 64);
        var source = new ChunkMessageIndex(_workDir);
        source.RecordNewIdentity("host/group", chunkId, "<refreshed@invalid>");

        using var poster = CreateStore(IndexAuth.FromRepoKey(KeyA));
        poster.PostMessageIndex(source.ToJson());

        string workDir2 = Path.Combine(Path.GetTempPath(), "ub-idxauth-test2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir2);
        try
        {
            var recovered = new ChunkMessageIndex(workDir2);
            using var reader = CreateStore(IndexAuth.FromRepoKey(KeyA), "reader.db");
            reader.SyncMessageIndex(recovered);
            Assert.Equal("<refreshed@invalid>",
                recovered.GetMessageId("host/group", chunkId, RepoId));
        }
        finally
        {
            try { Directory.Delete(workDir2, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ChunkIndex_WrongKey_Ignored()
    {
        // An index authenticated under another repo's key is not ours:
        // the wrong-key store must behave as if nothing was published.
        using var poster = CreateStore(IndexAuth.FromRepoKey(KeyA));
        poster.PostMessageIndex("""{"h/g":{"aaa":"<aaa@invalid>"}}""");

        using var wrongKey = CreateStore(IndexAuth.FromRepoKey(KeyB), "wrong.db");
        Assert.Null(wrongKey.GetLatestMessageIndex());

        var local = new ChunkMessageIndex(_workDir);
        wrongKey.SyncMessageIndex(local); // no-op
        Assert.Equal(ArticleCodec.MakeMessageId("aaa", RepoId),
            local.GetMessageId("h/g", "aaa", RepoId));
    }

    [Fact]
    public void ManifestIndex_WrongKey_Ignored()
    {
        string yearMonth = "2026-10";
        string[] ids = { new string('a', 32), new string('b', 32) };
        using var poster = CreateStore(IndexAuth.FromRepoKey(KeyA));
        poster.PostManifestIndex(yearMonth, ids);
        Assert.Equal(ids, poster.GetLatestManifestIndex(yearMonth));

        using var wrongKey = CreateStore(IndexAuth.FromRepoKey(KeyB), "wrong.db");
        Assert.Null(wrongKey.GetLatestManifestIndex(yearMonth));
    }
}

/// <summary>
/// Repo-level Phase 3 tests: manifest-index forgery (B-M1) and
/// discovery resilience — one undecryptable manifest entry must be
/// skipped, never abort discovery of the genuine backups.
/// </summary>
public sealed class ManifestIndexAuthenticationTests : IDisposable
{
    private const string Passphrase = "manifest-index-test-passphrase";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;
    private const string Newsgroup = "alt.binaries.test";

    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly FakeNntpServer _server;

    public ManifestIndexAuthenticationTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "ub-mftidx-" + Guid.NewGuid().ToString("N"));
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

    private NntpBlobStore KeyedStore(BackupRepository repo, string dbName = "journal.db") =>
        new(Connect(), Newsgroup, repo.RepoId, Path.Combine(_repoDir, dbName),
            providerKey: ChunkMessageIndex.MakeProviderKey("news.example.com", Newsgroup),
            indexAuth: repo.IndexAuth);

    private void WipeLocalManifests()
    {
        foreach (string f in Directory.GetFiles(Path.Combine(_repoDir, "manifests")))
            File.Delete(f);
    }

    [Fact]
    public void ForgedManifestIndex_HigherVersion_NeverAdopted()
    {
        // B-M1: the victim's authentic index lists the real backup. An
        // attacker posts a forged higher version naming only a backup
        // of their own. The keyed reader must not adopt it.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var manifest = repo.BackupDirectory(_srcDir);

        using var store = KeyedStore(repo);
        repo.UploadManifest(manifest.BackupId, store);

        string yearMonth = DateTime.UtcNow.ToString("yyyy-MM");
        Assert.Equal(new[] { manifest.BackupId }, store.GetLatestManifestIndex(yearMonth));

        using var attacker = AttackerStoreFor(repo);
        string forgedId = new string('f', 32);
        int v = attacker.PostManifestIndex(yearMonth, new[] { forgedId });
        Assert.Equal(2, v);

        // Forged latest version = not there. The forged ID is never
        // adopted, so discovery pursues nothing and invents nothing.
        Assert.Null(store.GetLatestManifestIndex(yearMonth));
        WipeLocalManifests();
        var found = repo.DiscoverRemoteManifestsViaIndex(store);
        Assert.Empty(found);
    }

    private NntpBlobStore AttackerStoreFor(BackupRepository repo) =>
        new(Connect(), Newsgroup, repo.RepoId, Path.Combine(_repoDir, "attacker.db"));

    [Fact]
    public void Discover_SkipsSquattedManifest_FindsGenuine()
    {
        // Group-scan path: an attacker squats the second backup's
        // deterministic manifest message ID with an undecryptable
        // article before the victim uploads. Discovery must skip the
        // squatted entry and still find the genuine backup (before
        // Phase 3 the first decrypt failure aborted the whole scan).
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var first = repo.BackupDirectory(_srcDir);
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello again");
        var second = repo.BackupDirectory(_srcDir);

        using var store = KeyedStore(repo);
        using var attacker = AttackerStoreFor(repo);
        attacker.PostManifest(second.BackupId, new byte[] { 1, 2, 3, 4 });

        repo.UploadManifest(first.BackupId, store);
        repo.UploadManifest(second.BackupId, store);

        WipeLocalManifests();
        var found = repo.DiscoverRemoteManifests(store);
        Assert.Single(found);
        Assert.Equal(first.BackupId, found[0].BackupId);
    }

    [Fact]
    public void DiscoverViaIndex_SkipsSquattedManifest_FindsGenuine()
    {
        // Index path, same squat: the AUTHENTIC index legitimately
        // lists both backups (the squatted article was adopted into
        // the upload journal by STAT), so the skip has to happen per
        // entry during discovery, not by rejecting the index.
        using var repo = BackupRepository.Open(_repoDir, Passphrase);
        var first = repo.BackupDirectory(_srcDir);
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "hello again");
        var second = repo.BackupDirectory(_srcDir);

        using var store = KeyedStore(repo);
        using var attacker = AttackerStoreFor(repo);
        attacker.PostManifest(second.BackupId, new byte[] { 1, 2, 3, 4 });

        repo.UploadManifest(first.BackupId, store);
        repo.UploadManifest(second.BackupId, store);

        string yearMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var listed = store.GetLatestManifestIndex(yearMonth);
        Assert.NotNull(listed);
        Assert.Equal(2, listed!.Count);

        WipeLocalManifests();
        var found = repo.DiscoverRemoteManifestsViaIndex(store);
        Assert.Single(found);
        Assert.Equal(first.BackupId, found[0].BackupId);
    }
}
