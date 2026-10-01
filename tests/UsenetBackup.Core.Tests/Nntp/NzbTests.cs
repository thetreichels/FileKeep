using System.Text;
using System.Xml;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Milestone 4: NZB generation — deterministic, valid XML that indexes
/// exactly the articles an upload would post.
/// </summary>
public sealed class NzbTests
{
    private const string RepoId = "0123456789abcdef";
    private const string Newsgroup = "alt.binaries.test";
    private const string Poster = "usenet-backup";

    // Valid 64-hex-char chunk IDs for the synthetic manifests.
    private static readonly string IdA = new('a', 64);
    private static readonly string IdB = new('b', 64);
    private static readonly string IdC = new('c', 64);
    private static readonly string IdD = new('d', 64);
    private static readonly string IdE = new('e', 64);
    private static readonly string IdF = new('f', 64);

    private static BackupManifest MakeManifest(params (string path, string[] chunks)[] files)
    {
        var manifest = new BackupManifest
        {
            BackupId = "20261001-test",
            CreatedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            RootSha256 = new string('a', 64),
        };
        foreach (var (path, chunks) in files)
            manifest.Files.Add(new FileEntry { Path = path, Chunks = chunks.ToList() });
        return manifest;
    }

    private static string Generate(
        BackupManifest manifest,
        Func<string, byte[]>? blobs = null,
        Func<string, DateTime?>? uploadTimes = null,
        string poster = Poster)
    {
        blobs ??= (id => Encoding.Latin1.GetBytes("blob:" + id));
        uploadTimes ??= (_ => null);
        return NzbGenerator.Generate(manifest, blobs, uploadTimes,
            new NzbGenerator.Options(Newsgroup, poster, RepoId));
    }

    private static XmlDocument Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml); // throws on malformed XML
        return doc;
    }

    private static XmlNamespaceManager NzbNs(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", "http://www.newzbin.com/DTD/nzb/nzb-1.1");
        return ns;
    }

    [Fact]
    public void Generate_ListsEveryDistinctChunk_ExactlyOnce()
    {
        var manifest = MakeManifest(
            ("a.bin", new[] { IdA, IdB, IdC }),
            ("b.bin", new[] { IdB, IdC, IdD })); // bb, cc shared

        XmlDocument doc = Parse(Generate(manifest));
        var ns = NzbNs(doc);
        var files = doc.SelectNodes("//n:file", ns)!;
        Assert.Equal(4, files.Count);

        var subjects = files.Cast<XmlElement>()
            .Select(e => e.GetAttribute("subject")).ToList();
        Assert.Equal(subjects.Count, subjects.Distinct().Count());
        Assert.Contains("[usenet-backup] chunk " + IdA, subjects);
        Assert.Contains("[usenet-backup] chunk " + IdD, subjects);

        // One segment per file, with the deterministic message-ID in brackets.
        foreach (XmlElement file in files)
        {
            var segments = file.SelectNodes("n:segments/n:segment", ns)!;
            Assert.Single(segments.Cast<XmlElement>());
            string chunkId = file.GetAttribute("subject").Split(' ').Last();
            Assert.Equal(ArticleCodec.MakeMessageId(chunkId, RepoId),
                ((XmlElement)segments[0]!).InnerText);
        }
    }

    [Fact]
    public void Generate_SegmentBytes_AreExactArticleSizes()
    {
        var manifest = MakeManifest(("a.bin", new[] { IdA, IdB }));
        XmlDocument doc = Parse(Generate(manifest));
        var ns = NzbNs(doc);

        foreach (XmlElement file in doc.SelectNodes("//n:file", ns)!.Cast<XmlElement>())
        {
            string chunkId = file.GetAttribute("subject").Split(' ').Last();
            string article = ArticleCodec.BuildArticle(
                chunkId, RepoId, Encoding.Latin1.GetBytes("blob:" + chunkId),
                Newsgroup, Poster);
            long expected = Encoding.Latin1.GetByteCount(article);
            var segment = (XmlElement)file.SelectSingleNode("n:segments/n:segment", ns)!;
            Assert.Equal(expected.ToString(), segment.GetAttribute("bytes"));
            Assert.Equal("1", segment.GetAttribute("number"));
            Assert.Equal(Newsgroup, file.SelectSingleNode("n:groups/n:group", ns)!.InnerText);
            Assert.Equal(Poster, file.GetAttribute("poster"));
        }
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        var manifest = MakeManifest(("a.bin", new[] { IdF, IdA, IdC }));
        Assert.Equal(Generate(manifest), Generate(manifest));
    }

    [Fact]
    public void Generate_Head_ContainsBackupMetadata()
    {
        var manifest = MakeManifest(("a.bin", new[] { IdA }));
        XmlDocument doc = Parse(Generate(manifest));
        var ns = NzbNs(doc);

        string Meta(string type) =>
            doc.SelectSingleNode($"//n:head/n:meta[@type='{type}']", ns)!.InnerText;

        Assert.Equal("20261001-test", Meta("x-usenetbackup-backup-id"));
        Assert.Equal(new string('a', 64), Meta("x-usenetbackup-root-sha256"));
        Assert.Equal("1", Meta("x-usenetbackup-chunk-count"));
        Assert.Contains("usenet-backup", Meta("x-usenetbackup-generator"));
    }

    [Fact]
    public void Generate_UsesUploadTime_WhenAvailable_FallsBackToManifestTime()
    {
        var manifest = MakeManifest(("a.bin", new[] { IdA, IdB }));
        var uploaded = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        string xml = Generate(manifest, uploadTimes: id => id == IdA ? uploaded : null);

        XmlDocument doc = Parse(xml);
        var ns = NzbNs(doc);
        string DateFor(string chunkId)
        {
            var file = (XmlElement)doc.SelectSingleNode(
                $"//n:file[@subject='[usenet-backup] chunk {chunkId}']", ns)!;
            return file.GetAttribute("date");
        }

        Assert.Equal(new DateTimeOffset(uploaded).ToUnixTimeSeconds().ToString(), DateFor(IdA));
        Assert.Equal(new DateTimeOffset(manifest.CreatedUtc).ToUnixTimeSeconds().ToString(), DateFor(IdB));
    }

    [Fact]
    public void Generate_EscapesSpecialCharacters()
    {
        var manifest = MakeManifest(("a.bin", new[] { IdA }));
        XmlDocument doc = Parse(Generate(manifest, poster: "a&b<c>\"d\""));
        var ns = NzbNs(doc);
        var file = (XmlElement)doc.SelectSingleNode("//n:file", ns)!;
        Assert.Equal("a&b<c>\"d\"", file.GetAttribute("poster"));
    }

    [Fact]
    public void Generate_RequiresArguments()
    {
        var options = new NzbGenerator.Options(Newsgroup, Poster, RepoId);
        Assert.Throws<ArgumentNullException>(() =>
            NzbGenerator.Generate(null!, _ => Array.Empty<byte>(), _ => null, options));
        Assert.Throws<ArgumentNullException>(() =>
            NzbGenerator.Generate(MakeManifest(), null!, _ => null, options));
    }
}

/// <summary>
/// The NZB must describe articles that actually exist on the server:
/// upload a backup, generate its NZB, then fetch every segment by
/// message-ID and confirm the chunk header matches.
/// </summary>
public sealed class NzbRoundTripTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;
    private const string Newsgroup = "alt.binaries.test";

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly FakeNntpServer _server = new();

    public NzbRoundTripTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-nzb-e2e-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Nzb_EverySegment_DownloadsAndVerifies()
    {
        var rnd = new Random(7);
        for (int i = 0; i < 3; i++)
        {
            byte[] data = new byte[ChunkSize + rnd.Next(500)];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(_srcDir, $"file{i}.bin"), data);
        }

        using var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations);
        var manifest = repo.BackupDirectory(_srcDir);
        string[] chunkIds = manifest.Files.SelectMany(f => f.Chunks).Distinct().ToArray();
        Assert.NotEmpty(chunkIds);

        // Upload everything, journaling each post.
        using (var client = Connect())
        using (var store = new NntpBlobStore(client, Newsgroup, repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            foreach (string id in chunkIds)
                if (!store.Exists(id))
                    store.Put(id, repo.GetChunkBlob(id));
        }

        // Generate the NZB the way the CLI does, with journal upload times.
        using var catalog = new Catalog(Path.Combine(_repoDir, "catalog.db"));
        string xml = NzbGenerator.Generate(
            manifest,
            chunkId => repo.GetChunkBlob(chunkId),
            chunkId => catalog.GetUploadTimeUtc(chunkId),
            new NzbGenerator.Options(Newsgroup, "usenet-backup", repo.RepoId));

        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", "http://www.newzbin.com/DTD/nzb/nzb-1.1");
        var files = doc.SelectNodes("//n:file", ns)!;
        Assert.Equal(chunkIds.Length, files.Count);

        // Every segment message-ID must fetch a valid, matching article.
        using var downloader = Connect();
        foreach (XmlElement file in files.Cast<XmlElement>())
        {
            var segment = (XmlElement)file.SelectSingleNode("n:segments/n:segment", ns)!;
            string messageId = segment.InnerText;
            string? article = downloader.GetArticle(messageId);
            Assert.NotNull(article);
            var (chunkId, _) = ArticleCodec.ParseArticle(article);
            Assert.Equal(ArticleCodec.MakeMessageId(chunkId, repo.RepoId), messageId);
            Assert.Contains(chunkId, chunkIds);
        }
    }
}
