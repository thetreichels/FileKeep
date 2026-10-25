using System.Text;
using System.Xml;
using FileKeep.Core;
using FileKeep.Core.Nntp;
using Xunit;

namespace FileKeep.Core.Tests.Nntp;

/// <summary>
/// Milestone 4: NZB generation — deterministic, valid XML that indexes
/// exactly the articles an upload would post.
/// </summary>
public sealed class NzbTests
{
    private const string RepoId = "0123456789abcdef";
    private const string Newsgroup = "alt.binaries.test";
    private const string Poster = "FileKeep";

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
        Assert.Contains($"\"{IdA[..16]}.bin\" - yEnc", subjects);
        Assert.Contains($"\"{IdD[..16]}.bin\" - yEnc", subjects);

        // One segment per file, with the deterministic message-ID in brackets.
        foreach (XmlElement file in files)
        {
            var segments = file.SelectNodes("n:segments/n:segment", ns)!;
            Assert.Single(segments.Cast<XmlElement>());
            string segmentText = ((XmlElement)segments[0]!).InnerText;
            // Message-ID is <chunkId.repoId@invalid>; subject filename
            // carries the first 16 chars of the chunk ID.
            string chunkId = segmentText.Trim('<', '>').Split('.')[0];
            Assert.Equal(ArticleCodec.MakeMessageId(chunkId, RepoId), segmentText);
            string subject = file.GetAttribute("subject");
            Assert.Equal($"\"{chunkId[..16].ToLowerInvariant()}.bin\" - yEnc", subject);
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
            var segment = (XmlElement)file.SelectSingleNode("n:segments/n:segment", ns)!;
            // Message-ID is <chunkId.repoId@invalid>; subject filename
            // carries only the first 16 chars of the chunk ID.
            string chunkId = segment.InnerText.Trim('<', '>').Split('.')[0];
            string article = ArticleCodec.BuildArticle(
                chunkId, RepoId, Encoding.Latin1.GetBytes("blob:" + chunkId),
                Newsgroup, Poster);
            long expected = Encoding.Latin1.GetByteCount(article);
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

        Assert.Equal("20261001-test", Meta("x-filekeep-backup-id"));
        Assert.Equal(new string('a', 64), Meta("x-filekeep-root-sha256"));
        Assert.Equal("1", Meta("x-filekeep-chunk-count"));
        Assert.Contains("FileKeep", Meta("x-filekeep-generator"));
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
                $"//n:file[@subject='\"{chunkId[..16].ToLowerInvariant()}.bin\" - yEnc']", ns)!;
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
/// Volume packer: NZB generation/parsing in volume mode.
/// </summary>
public sealed class NzbVolumeTests
{
    private const string RepoId = "0123456789abcdef";
    private const string Newsgroup = "alt.binaries.test";
    private const string Poster = "FileKeep";

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    private static (BackupManifest Manifest, Dictionary<string, byte[]> VolumeBlobs) MakeVolumeManifest(
        int chunkCount, int chunkSize, long targetVolumeBytes)
    {
        var blobs = new Dictionary<string, byte[]>();
        var ids = new List<string>();
        for (int i = 0; i < chunkCount; i++)
        {
            byte[] blob = RandomBytes(chunkSize);
            string id = Hashing.Sha256Hex(blob);
            ids.Add(id);
            blobs[id] = blob;
        }
        ids.Sort(StringComparer.Ordinal);

        var manifest = new BackupManifest
        {
            BackupId = "20261010-voltest",
            CreatedUtc = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
            RootSha256 = new string('b', 64),
            VolumeSize = targetVolumeBytes,
            Volumes = new List<VolumeEntry>(),
        };
        manifest.Files.Add(new FileEntry { Path = "a.bin", Chunks = ids });

        var volumes = VolumePacker.Pack(ids, id => blobs[id], targetVolumeBytes);
        var volumeBlobs = new Dictionary<string, byte[]>();
        foreach (var v in volumes)
        {
            manifest.Volumes.Add(new VolumeEntry
            {
                Id = v.Id,
                ChunkIds = v.ChunkIds.ToList(),
                SizeBytes = v.Bytes.Length,
            });
            volumeBlobs[v.Id] = v.Bytes;
        }
        return (manifest, volumeBlobs);
    }

    [Fact]
    public void Generate_VolumeMode_OneFilePerVolume()
    {
        var (manifest, volumeBlobs) = MakeVolumeManifest(chunkCount: 6, chunkSize: 5000, targetVolumeBytes: 12000);
        Assert.True(manifest.Volumes!.Count > 1); // sanity: actually split

        string xml = NzbGenerator.Generate(
            manifest,
            getBlob: _ => throw new InvalidOperationException("chunk blobs must not be needed in volume mode"),
            getUploadTimeUtc: _ => null,
            new NzbGenerator.Options(Newsgroup, Poster, RepoId),
            getVolumeBlob: id => volumeBlobs[id]);

        NzbDocument doc = NzbParser.Parse(xml);
        Assert.True(doc.IsVolumeNzb);
        Assert.Equal(manifest.Volumes.Count, doc.Files.Count);
        var parsedIds = doc.Files.Select(f => f.VolumeId!).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var expectedIds = manifest.Volumes.Select(v => v.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedIds, parsedIds);
        foreach (var file in doc.Files)
        {
            Assert.Matches("^\"[^\"]+\" \\[\\d{3}/\\d{3}\\] - yEnc$", file.Subject);
            Assert.Equal(ArticleCodec.MakeVolumeMessageId(file.VolumeId!, RepoId),
                file.Segments[0].MessageId);
        }
    }

    [Fact]
    public void Generate_ChunkMode_ParsesAsNotVolume()
    {
        var manifest = new BackupManifest
        {
            BackupId = "20261010-chunktest",
            CreatedUtc = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
            RootSha256 = new string('c', 64),
        };
        manifest.Files.Add(new FileEntry { Path = "a.bin", Chunks = new List<string> { new('a', 64) } });

        string xml = NzbGenerator.Generate(
            manifest,
            id => new byte[10],
            _ => null,
            new NzbGenerator.Options(Newsgroup, Poster, RepoId));

        NzbDocument doc = NzbParser.Parse(xml);
        Assert.False(doc.IsVolumeNzb);
        Assert.Single(doc.Files);
        Assert.Equal(new string('a', 64), doc.Files[0].ChunkId);
    }

    [Fact]
    public void Generate_VolumeMode_RequiresVolumeBlobProvider()
    {
        var (manifest, _) = MakeVolumeManifest(chunkCount: 2, chunkSize: 100, targetVolumeBytes: 10000);
        Assert.Throws<ArgumentException>(() => NzbGenerator.Generate(
            manifest,
            _ => new byte[10],
            _ => null,
            new NzbGenerator.Options(Newsgroup, Poster, RepoId)));
    }
}
