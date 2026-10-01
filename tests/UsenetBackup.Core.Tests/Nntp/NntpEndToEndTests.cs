using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Milestone 3 end-to-end: back up a tree with the real repository engine,
/// upload every chunk through <see cref="NntpBlobStore"/> over an in-memory
/// NNTP transport, then download them all back with a fresh store instance
/// and confirm the blobs are byte-identical.
/// </summary>
public sealed class NntpEndToEndTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int ChunkSize = 64 * 1024;
    private const int KdfIterations = 10_000;

    private readonly string _workDir;
    private readonly string _repoDir;
    private readonly string _srcDir;
    private readonly FakeNntpServer _server = new();

    public NntpEndToEndTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-nntp-e2e-" + Guid.NewGuid().ToString("N"));
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
    public void Backup_Upload_Download_BlobsAreIdentical()
    {
        // Arrange: a small tree with multi-chunk files and an empty dir.
        var rnd = new Random(42);
        for (int i = 0; i < 4; i++)
        {
            byte[] data = new byte[ChunkSize + rnd.Next(1000)];
            rnd.NextBytes(data);
            string path = Path.Combine(_srcDir, $"file{i}.bin");
            File.WriteAllBytes(path, data);
        }
        Directory.CreateDirectory(Path.Combine(_srcDir, "empty"));

        using var repo = BackupRepository.Init(_repoDir, Passphrase, ChunkSize, KdfIterations);
        var manifest = repo.BackupDirectory(_srcDir);
        string[] chunkIds = manifest.Files.SelectMany(f => f.Chunks).Distinct().ToArray();
        Assert.NotEmpty(chunkIds);

        // Act: upload every chunk (the nntp-upload CLI loop), resume-style.
        using (var client = Connect())
        using (var store = new NntpBlobStore(client, "alt.binaries.test", repo.RepoId,
                   Path.Combine(_repoDir, "catalog.db")))
        {
            foreach (string id in chunkIds)
                if (!store.Exists(id))
                    store.Put(id, repo.GetChunkBlob(id));
        }
        Assert.Equal(chunkIds.Length, _server.PostCount);

        // Assert: a fresh store (empty journal) downloads identical blobs,
        // adopting server state via STAT without re-posting.
        using (var client = Connect())
        using (var store = new NntpBlobStore(client, "alt.binaries.test", repo.RepoId,
                   Path.Combine(_workDir, "fresh-journal.db")))
        {
            foreach (string id in chunkIds)
            {
                byte[] expected = repo.GetChunkBlob(id);
                byte[] actual = store.Get(id);
                Assert.Equal(expected, actual);
            }
        }
        Assert.Equal(chunkIds.Length, _server.PostCount); // no re-posts on download

        // And the repository still restores the tree from local blobs.
        string dest = Path.Combine(_workDir, "restored");
        repo.Restore(manifest.BackupId, dest);
        foreach (string src in Directory.EnumerateFiles(_srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_srcDir, src);
            Assert.Equal(File.ReadAllBytes(src),
                File.ReadAllBytes(Path.Combine(dest, rel)));
        }
    }
}
