using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Volume packer: backup -> plan -> PutVolume -> NZB -> fetch back and
/// unpack, against the fake NNTP server. Mirrors the CLI nntp-upload flow.
/// </summary>
public sealed class VolumeUploadTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 32 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly FakeNntpServer _server = new();

    public VolumeUploadTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-volup-" + Guid.NewGuid().ToString("N"));
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
    public void Upload_PostsOneArticlePerVolume_AndNzbIndexesVolumes()
    {
        var rnd = new Random(777);
        for (int i = 0; i < 4; i++)
        {
            byte[] data = new byte[ChunkSize + rnd.Next(8000)];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(_srcDir, $"file{i}.bin"), data);
        }

        using var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations);
        var manifest = repo.BackupDirectory(_srcDir);
        string[] chunkIds = ChunkOrdering.GetOrderedChunkIds(manifest);
        Assert.True(chunkIds.Length > 2);

        // Shrink the volume target so this small backup actually splits.
        // (Repo config default is 32 MiB; override via reflection-free path:
        // re-plan with an explicit small target through VolumePacker.)
        long targetBytes = 40 * 1024;
        var groups = VolumePacker.Plan(chunkIds, repo.GetChunkBlobSize, targetBytes);
        Assert.True(groups.Count > 1);

        using (var client = Connect())
        using (var store = new NntpBlobStore(client, "alt.binaries.test", repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            var entries = new List<VolumeEntry>();
            foreach (var group in groups)
            {
                var volume = VolumePacker.BuildVolume(group, repo.GetChunkBlob);
                if (!store.Exists(volume.Id))
                    store.PutVolume(volume.Id, volume.Bytes);
                entries.Add(new VolumeEntry
                {
                    Id = volume.Id,
                    ChunkIds = volume.ChunkIds.ToList(),
                    SizeBytes = volume.Bytes.Length,
                });
            }
            manifest.Volumes = entries;
            manifest.VolumeSize = targetBytes;
            repo.SaveManifest(manifest);

            // Re-upload is idempotent: nothing new posted.
            int postsBefore = _server.PostCount;
            foreach (var group in groups)
            {
                var volume = VolumePacker.BuildVolume(group, repo.GetChunkBlob);
                if (!store.Exists(volume.Id))
                    store.PutVolume(volume.Id, volume.Bytes);
            }
            Assert.Equal(postsBefore, _server.PostCount);
        }
        Assert.Equal(groups.Count, _server.PostCount);

        // NZB in volume mode, then fetch every volume back and unpack.
        var volumeBlobs = manifest.Volumes!.ToDictionary(
            v => v.Id,
            v => VolumePacker.BuildVolume(v.ChunkIds, repo.GetChunkBlob).Bytes);
        string nzb = NzbGenerator.Generate(
            manifest,
            _ => throw new InvalidOperationException("chunk path must not be used"),
            id => DateTime.UtcNow,
            new NzbGenerator.Options("alt.binaries.test", "usenet-backup", repo.RepoId),
            getVolumeBlob: id => volumeBlobs[id]);
        var doc = NzbParser.Parse(nzb);
        Assert.True(doc.IsVolumeNzb);
        Assert.Equal(groups.Count, doc.Files.Count);

        using (var client = Connect())
        using (var store = new NntpBlobStore(client, "alt.binaries.test", repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            var recovered = new Dictionary<string, byte[]>();
            foreach (var file in doc.Files)
            {
                string volumeId = file.VolumeId!;
                byte[] volumeBytes = store.GetVolume(volumeId);
                Assert.Equal(volumeBlobs[volumeId], volumeBytes);
                foreach (var (chunkId, blob) in VolumePacker.Unpack(volumeBytes))
                    recovered[chunkId] = blob;
            }
            Assert.Equal(chunkIds.Length, recovered.Count);
            foreach (string id in chunkIds)
                Assert.Equal(repo.GetChunkBlob(id), recovered[id]);
        }

        // Saved manifest round-trips with volumes and a valid root hash.
        var reloaded = repo.LoadManifest(manifest.BackupId);
        Assert.Equal(manifest.Volumes!.Count, reloaded.Volumes!.Count);
        Assert.Equal(manifest.RootSha256, reloaded.RootSha256);
    }
}
