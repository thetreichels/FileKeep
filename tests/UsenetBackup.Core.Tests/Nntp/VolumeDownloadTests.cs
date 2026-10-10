using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Redundancy;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Volume packer: NZB -> DownloadVolumes -> unpack -> verify, plus parity
/// reconstruction over volumes, against the fake NNTP server.
/// </summary>
public sealed class VolumeDownloadTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 32 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly FakeNntpServer _server = new();

    public VolumeDownloadTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-voldl-" + Guid.NewGuid().ToString("N"));
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
        Dictionary<string, byte[]> VolumeBlobs,
        string NzbXml);

    /// <summary>
    /// Backs up a tree, packs into small volumes (one chunk each), posts
    /// them plus per-group PAR2 parity, and builds the volume NZB.
    /// Mirrors the scheduler's volume upload path.
    /// </summary>
    private Fixture BackupUploadAndIndex(bool withParity)
    {
        var rnd = new Random(4242);
        for (int i = 0; i < 6; i++)
        {
            byte[] data = new byte[ChunkSize + rnd.Next(8000)];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(_srcDir, $"file{i}.bin"), data);
        }

        var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations);
        var manifest = repo.BackupDirectory(_srcDir);
        string[] chunkIds = ChunkOrdering.GetOrderedChunkIds(manifest);
        Assert.True(chunkIds.Length >= 11, "need enough chunks for two parity groups");

        var blobs = chunkIds.ToDictionary(id => id, id => repo.GetChunkBlob(id));
        // Small target: one chunk per volume -> #volumes == #chunks.
        var groups = VolumePacker.Plan(chunkIds, id => blobs[id].Length, targetVolumeBytes: 1024);
        Assert.Equal(chunkIds.Length, groups.Count);

        var volumeBlobs = new Dictionary<string, byte[]>();
        var entries = new List<VolumeEntry>();
        using (var client = Connect())
        using (var store = new NntpBlobStore(client, "alt.binaries.test", repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            foreach (var group in groups)
            {
                var volume = VolumePacker.BuildVolume(group, id => blobs[id]);
                if (!store.Exists(volume.Id))
                    store.PutVolume(volume.Id, volume.Bytes);
                volumeBlobs[volume.Id] = volume.Bytes;
                entries.Add(new VolumeEntry
                {
                    Id = volume.Id,
                    ChunkIds = volume.ChunkIds.ToList(),
                    SizeBytes = volume.Bytes.Length,
                });
            }

            if (withParity)
            {
                string[] sortedVolIds = volumeBlobs.Keys
                    .OrderBy(id => id, StringComparer.Ordinal).ToArray();
                for (int g = 0; g < sortedVolIds.Length; g += 10)
                {
                    string[] group = sortedVolIds.Skip(g).Take(10).ToArray();
                    if (group.Length < 2)
                        continue;
                    var blocks = Par2Redundancy.GenerateParity(
                        group, volId => volumeBlobs[volId]);
                    foreach (var kvp in blocks)
                        if (!store.Exists(kvp.Key))
                            store.PutVolume(kvp.Key, kvp.Value);
                }
            }
        }
        manifest.Volumes = entries
            .OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        manifest.VolumeSize = 1024;
        repo.SaveManifest(manifest);

        string nzb = NzbGenerator.Generate(
            manifest,
            _ => throw new InvalidOperationException("chunk path must not be used"),
            _ => DateTime.UtcNow,
            new NzbGenerator.Options("alt.binaries.test", "usenet-backup", repo.RepoId),
            getVolumeBlob: id => volumeBlobs[id]);
        return new Fixture(repo, manifest, chunkIds, blobs, volumeBlobs, nzb);
    }

    private void WipeLocalChunks()
    {
        foreach (string f in Directory.EnumerateFiles(
                     Path.Combine(_repoDir, "chunks"), "*", SearchOption.AllDirectories))
            File.Delete(f);
    }

    private void AssertAllChunksPresent(Fixture fx)
    {
        // NOTE: does not dispose fx.Repo; the caller owns it.
        foreach (string id in fx.ChunkIds)
            Assert.Equal(fx.Blobs[id], fx.Repo.GetChunkBlob(id));
    }

    [Fact]
    public void DownloadVolumes_RoundTrip_RestoresAllChunks()
    {
        var fx = BackupUploadAndIndex(withParity: false);
        WipeLocalChunks();

        using var repo = fx.Repo;
        var doc = NzbParser.Parse(fx.NzbXml);
        Assert.True(doc.IsVolumeNzb);
        using var client = Connect();
        using var remote = new NntpBlobStore(client, "alt.binaries.test", repo.RepoId,
            Path.Combine(_repoDir, "catalog.db"));
        remote.MaxArticleBytes = Math.Max(repo.MaxDownloadBytes, repo.VolumeSizeBytes * 2);

        var result = repo.DownloadVolumes(doc, remote);
        Assert.Equal(0, result.Reconstructed);
        Assert.Equal(fx.ChunkIds.Length, result.Downloaded + result.AlreadyPresent);
        AssertAllChunksPresent(fx);

        // Second download is fully skipped.
        var again = repo.DownloadVolumes(doc, remote);
        Assert.Equal(0, again.Downloaded);
        Assert.Equal(fx.ChunkIds.Length, again.AlreadyPresent);
    }

    [Fact]
    public void DownloadVolumes_ReconstructsMissingVolumeViaPar2()
    {
        var fx = BackupUploadAndIndex(withParity: true);
        WipeLocalChunks();

        using var repo = fx.Repo;
        var doc = NzbParser.Parse(fx.NzbXml);
        // Expire one volume article on the server (first volume of group 1).
        string victim = doc.Files[0].VolumeId!;
        _server.FailArticleFetch.Add(ArticleCodec.MakeVolumeMessageId(victim, repo.RepoId));

        using var client = Connect();
        using var remote = new NntpBlobStore(client, "alt.binaries.test", repo.RepoId,
            Path.Combine(_repoDir, "catalog.db"));
        remote.MaxArticleBytes = Math.Max(repo.MaxDownloadBytes, repo.VolumeSizeBytes * 2);

        var result = repo.DownloadVolumes(doc, remote);
        Assert.Equal(1, result.Reconstructed);
        AssertAllChunksPresent(fx);
    }

    [Fact]
    public void DownloadNzb_DispatchesOnIndexKind()
    {
        var fx = BackupUploadAndIndex(withParity: false);
        WipeLocalChunks();

        using var repo = fx.Repo;
        var doc = NzbParser.Parse(fx.NzbXml);
        using var client = Connect();
        using var remote = new NntpBlobStore(client, "alt.binaries.test", repo.RepoId,
            Path.Combine(_repoDir, "catalog.db"));
        remote.MaxArticleBytes = Math.Max(repo.MaxDownloadBytes, repo.VolumeSizeBytes * 2);

        var result = repo.DownloadNzb(doc, remote);
        Assert.Equal(fx.ChunkIds.Length, result.Downloaded + result.AlreadyPresent);
        AssertAllChunksPresent(fx);
    }
}
