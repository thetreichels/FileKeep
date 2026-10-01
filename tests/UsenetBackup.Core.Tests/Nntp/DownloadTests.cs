using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Milestone 5: NZB parsing and the download/recovery pipeline —
/// NZB → article fetch → validation → local chunk store, resumable.
/// </summary>
public sealed class DownloadTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 32 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly FakeNntpServer _server = new();

    public DownloadTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-dl-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_workDir, "repo");
        _srcDir = Path.Combine(_workDir, "src");
        Directory.CreateDirectory(_srcDir);
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

    private sealed record Fixture(
        BackupRepository Repo,
        BackupManifest Manifest,
        string[] ChunkIds,
        Dictionary<string, byte[]> Blobs,
        string NzbXml);

    /// <summary>
    /// Backs up a small tree and uploads every chunk to the fake server,
    /// then generates the NZB the download pipeline consumes.
    /// </summary>
    private Fixture BackupUploadAndIndex()
    {
        var rnd = new Random(1234);
        for (int i = 0; i < 3; i++)
        {
            byte[] data = new byte[ChunkSize + rnd.Next(5000)];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(_srcDir, $"file{i}.bin"), data);
        }
        Directory.CreateDirectory(Path.Combine(_srcDir, "empty"));

        var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations);
        var manifest = repo.BackupDirectory(_srcDir);
        string[] chunkIds = manifest.Files.SelectMany(f => f.Chunks).Distinct().ToArray();
        Assert.NotEmpty(chunkIds);

        var blobs = chunkIds.ToDictionary(id => id, id => repo.GetChunkBlob(id));
        using (var client = Connect())
        using (var store = new NntpBlobStore(client, "alt.binaries.test", repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            foreach (string id in chunkIds)
                if (!store.Exists(id))
                    store.Put(id, blobs[id]);
        }
        Assert.Equal(chunkIds.Length, _server.PostCount);

        string nzb = NzbGenerator.Generate(
            manifest,
            chunkId => blobs[chunkId],
            _ => DateTime.UtcNow,
            new NzbGenerator.Options("alt.binaries.test", "usenet-backup", repo.RepoId));
        return new Fixture(repo, manifest, chunkIds, blobs, nzb);
    }

    private void WipeLocalChunks()
    {
        foreach (string f in Directory.EnumerateFiles(
                     Path.Combine(_repoDir, "chunks"), "*", SearchOption.AllDirectories))
            File.Delete(f);
    }

    private string ChunkPath(string chunkId) =>
        Path.Combine(_repoDir, "chunks", chunkId[..2], chunkId[2..]);

    // ---------- NzbParser ----------

    [Fact]
    public void Parser_RoundTripsGeneratedNzb()
    {
        var fx = BackupUploadAndIndex();
        using var repo = fx.Repo;
        var doc = NzbParser.Parse(fx.NzbXml);

        Assert.Equal(fx.Manifest.BackupId, doc.BackupId);
        Assert.Equal(fx.Manifest.RootSha256, doc.RootSha256);
        Assert.NotNull(doc.Generator);
        Assert.Equal(fx.ChunkIds.Length, doc.Files.Count);

        var seen = new HashSet<string>();
        foreach (var file in doc.Files)
        {
            Assert.NotNull(file.ChunkId);
            Assert.True(seen.Add(file.ChunkId!), "chunk listed twice");
            Assert.Single(file.Segments);
            var seg = file.Segments[0];
            Assert.Equal(
                ArticleCodec.MakeMessageId(file.ChunkId!, fx.Repo.RepoId),
                seg.MessageId);
            Assert.True(seg.Bytes > 0);
            Assert.Equal(1, seg.Number);
        }
        Assert.Equal(fx.ChunkIds.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public void Parser_RejectsMalformedInput()
    {
        // File with no segments.
        Assert.Throws<InvalidDataException>(() =>
            NzbParser.Parse("<nzb><file poster=\"p\" subject=\"s\"></file></nzb>"));
        // Not XML at all.
        Assert.Throws<System.Xml.XmlException>(() => NzbParser.Parse("this is not xml"));
        // Well-formed but foreign NZB: parses, but no chunk IDs.
        var doc = NzbParser.Parse(
            "<nzb><file poster=\"p\" subject=\"s\"><segments>" +
            "<segment bytes=\"10\" number=\"1\">&lt;abc@other&gt;</segment>" +
            "</segments></file></nzb>");
        Assert.Single(doc.Files);
        Assert.Null(doc.Files[0].ChunkId);
    }

    // ---------- download pipeline ----------

    [Fact]
    public void Download_RestoresLostChunks_EndToEnd()
    {
        var fx = BackupUploadAndIndex();
        using var repo = fx.Repo;
        WipeLocalChunks();

        var doc = NzbParser.Parse(fx.NzbXml);
        DownloadResult result;
        using (var client = Connect())
        using (var remote = new NntpBlobStore(client, "alt.binaries.test", fx.Repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            int progressCalls = 0;
            result = fx.Repo.DownloadChunks(doc, remote, (_, _) => progressCalls++);
            Assert.True(progressCalls > 0);
        }

        Assert.Equal(fx.ChunkIds.Length, result.Total);
        Assert.Equal(fx.ChunkIds.Length, result.Downloaded);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(fx.ChunkIds.Length, _server.ArticleCount);

        // Every blob is byte-identical to the original upload.
        foreach (string id in fx.ChunkIds)
            Assert.Equal(fx.Blobs[id], fx.Repo.GetChunkBlob(id));

        // The backup verifies and restores from the downloaded chunks.
        Assert.Empty(fx.Repo.Verify(fx.Manifest.BackupId));
        string dest = Path.Combine(_workDir, "restored");
        fx.Repo.Restore(fx.Manifest.BackupId, dest);
        foreach (string src in Directory.EnumerateFiles(_srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_srcDir, src);
            Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(dest, rel)));
        }
    }

    [Fact]
    public void Download_ResumesWithoutRefetching()
    {
        var fx = BackupUploadAndIndex();
        using var repo = fx.Repo;
        WipeLocalChunks();
        var doc = NzbParser.Parse(fx.NzbXml);

        DownloadResult first, second, third;
        using (var client = Connect())
        using (var remote = new NntpBlobStore(client, "alt.binaries.test", fx.Repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            first = fx.Repo.DownloadChunks(doc, remote);
        }
        Assert.Equal(fx.ChunkIds.Length, first.Downloaded);
        int afterFirst = _server.ArticleCount;

        // Nothing missing: second run fetches zero articles.
        using (var client = Connect())
        using (var remote = new NntpBlobStore(client, "alt.binaries.test", fx.Repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            second = fx.Repo.DownloadChunks(doc, remote);
        }
        Assert.Equal(0, second.Downloaded);
        Assert.Equal(fx.ChunkIds.Length, second.AlreadyPresent);
        Assert.Equal(afterFirst, _server.ArticleCount);

        // Lose one chunk (journal entry kept): only it is re-fetched.
        File.Delete(ChunkPath(fx.ChunkIds[0]));
        using (var client = Connect())
        using (var remote = new NntpBlobStore(client, "alt.binaries.test", fx.Repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            third = fx.Repo.DownloadChunks(doc, remote);
        }
        Assert.Equal(1, third.Downloaded);
        Assert.Equal(fx.ChunkIds.Length - 1, third.AlreadyPresent);
        Assert.Equal(afterFirst + 1, _server.ArticleCount);
        Assert.Empty(fx.Repo.Verify(fx.Manifest.BackupId));
    }

    [Fact]
    public void Download_RejectsCorruptedArticle_AndLeavesNoTrace()
    {
        var fx = BackupUploadAndIndex();
        using var repo = fx.Repo;
        WipeLocalChunks();
        var doc = NzbParser.Parse(fx.NzbXml);

        // Tamper with the FIRST article in NZB order (generator sorts chunk
        // IDs), so the failure hits before anything is downloaded.
        string victim = fx.ChunkIds.OrderBy(x => x, StringComparer.Ordinal).First();
        string msgKey = _server.Articles.Keys.First(k => k.Contains(victim));
        string original = _server.Articles[msgKey];
        int bodyAt = original.IndexOf("=ybegin", StringComparison.Ordinal);
        Assert.True(bodyAt > 0);
        int eol = original.IndexOf("\r\n", bodyAt, StringComparison.Ordinal);
        int flipAt = eol + 12; // inside the yEnc data, past the =ybegin line
        Assert.True(flipAt < original.Length);
        char[] tampered = original.ToCharArray();
        tampered[flipAt] = tampered[flipAt] == 'A' ? 'B' : 'A';
        _server.Articles[msgKey] = new string(tampered);

        using (var client = Connect())
        using (var remote = new NntpBlobStore(client, "alt.binaries.test", fx.Repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            var ex = Assert.Throws<InvalidDataException>(() => fx.Repo.DownloadChunks(doc, remote));
            Assert.Contains("CRC-32", ex.Message);
        }

        // Failed chunk: no blob stored, no journal entry.
        Assert.False(File.Exists(ChunkPath(victim)));
        string messageId = ArticleCodec.MakeMessageId(victim, fx.Repo.RepoId);
        using (var catalog = new Catalog(Path.Combine(_repoDir, "catalog.db")))
            Assert.False(catalog.IsDownloaded(messageId));

        // Repair the article: re-running downloads everything cleanly.
        _server.Articles[msgKey] = original;
        DownloadResult result;
        using (var client = Connect())
        using (var remote = new NntpBlobStore(client, "alt.binaries.test", fx.Repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            result = fx.Repo.DownloadChunks(doc, remote);
        }
        Assert.Equal(fx.ChunkIds.Length, result.Downloaded);
        Assert.Empty(fx.Repo.Verify(fx.Manifest.BackupId));
    }

    [Fact]
    public void Download_RejectsForeignNzb()
    {
        var fx = BackupUploadAndIndex();
        using var repo = fx.Repo;
        var doc = NzbParser.Parse(
            "<nzb><file poster=\"p\" subject=\"s\"><segments>" +
            "<segment bytes=\"10\" number=\"1\">&lt;abc@other&gt;</segment>" +
            "</segments></file></nzb>");

        using var client = Connect();
        using var remote = new NntpBlobStore(client, "alt.binaries.test", fx.Repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db"));
        Assert.Throws<InvalidDataException>(() => fx.Repo.DownloadChunks(doc, remote));
    }
}
