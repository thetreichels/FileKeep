using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Reader;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Portable reader (Stealth Folders slice 3, part 3): the on-stick
/// layout + discovery, the stick-first composite chunk store, the
/// reader DAV handler (GET/PROPFIND/PUT/DELETE/LOCK), the full
/// export producer, and the WinRE reader-step validation. The
/// reader runs only on Windows in production; everything here is
/// filesystem + crypto, so it runs in the sandbox. GUI proof and
/// a second-machine run remain owed and are NOT claimed here.
/// </summary>
public sealed class ReaderTests : IDisposable
{
    private readonly string _workDir;

    private const string VaultSecret = "the-vault-secret";
    private const string ExportSecret = "the-export-passphrase";

    public ReaderTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class MemoryStore : IBlobStore
    {
        public Dictionary<string, byte[]> Blobs { get; } = new();
        public bool Exists(string id) => Blobs.ContainsKey(id);
        public void Put(string id, byte[] blob) => Blobs[id] = blob;
        public void Delete(string id) => Blobs.Remove(id);
        public byte[] Get(string id) => Blobs.TryGetValue(id, out var b)
            ? b
            : throw new InvalidDataException($"Missing {id}.");
        public long StoredCount => Blobs.Count;
    }

    private static OfflineExportEnvelope.Payload PayloadWith(
        string folder, params (string Path, string[] Chunks)[] files) =>
        new()
        {
            Folder = folder,
            Location = new OfflineExportEnvelope.LocationPointer { Type = "usb", Name = "STICK", VolumeSerial = "ABCD-1234" },
            Files = files.Select(f => new OfflineExportEnvelope.FileRef
            {
                Path = f.Path,
                Size = f.Chunks.Length,
                ModifiedUtc = DateTimeOffset.UtcNow,
                ChunkIds = f.Chunks.ToList(),
            }).ToList(),
        };

    private ReaderSession SessionFor(string packagePath, OfflineExportEnvelope.Payload payload,
        IBlobStore stick, IBlobStore? original = null)
    {
        byte[] key = OfflineChunkStore.DeriveContentKey(VaultSecret);
        return new ReaderSession(packagePath, payload, key, ExportSecret, stick, original);
    }

    private (ReaderSession Session, ReaderDavHandler Dav, string PackagePath) DavSession(
        string content, string path = "hello.txt")
    {
        string readerDir = Path.Combine(_workDir, Guid.NewGuid().ToString("N"));
        var stick = new LocalBlobStore(readerDir);
        byte[] key = OfflineChunkStore.DeriveContentKey(VaultSecret);
        List<string> chunkIds;
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(content)))
            (chunkIds, _) = OfflineChunkStore.Store(input, stick, key);
        var payload = PayloadWith("F", (path, chunkIds.ToArray()));
        string packagePath = Path.Combine(readerDir, OfflineStickLayout.ManifestFileName);
        var package = OfflineExportEnvelope.CreatePackage(payload, ExportSecret);
        OfflineStickLayout.WritePackageAtomic(packagePath, JsonSerializer.Serialize(package));
        var session = SessionFor(packagePath, payload, stick);
        return (session, new ReaderDavHandler(session), packagePath);
    }

    private static async Task<byte[]> StreamBytes(ReaderDavHandler.Response response)
    {
        using var ms = new MemoryStream();
        await response.StreamBody!(ms);
        return ms.ToArray();
    }

    [Fact]
    public void StickLayout_DiscoveryPrefersReaderManifest()
    {
        string stick = Path.Combine(_workDir, "stick");
        string readerDir = OfflineStickLayout.ReaderFolder(stick);
        Directory.CreateDirectory(readerDir);
        string manifest = Path.Combine(readerDir, OfflineStickLayout.ManifestFileName);
        string other = Path.Combine(readerDir, "other.filekeep-export.json");
        string root = Path.Combine(stick, "root.filekeep-export.json");
        File.WriteAllText(manifest, "{}");
        File.WriteAllText(other, "{}");
        File.WriteAllText(root, "{}");
        var found = OfflineStickLayout.DiscoverPackages(stick);
        Assert.Equal(manifest, found[0]);
        Assert.Equal(3, found.Count);
        Assert.Empty(OfflineStickLayout.DiscoverPackages(Path.Combine(_workDir, "nope")));
    }

    [Fact]
    public void StickLayout_WritePackageAtomic_RoundTrips()
    {
        string path = Path.Combine(_workDir, "pkg.json");
        OfflineStickLayout.WritePackageAtomic(path, "{\"a\":1}");
        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_workDir, "*.tmp-*"));
    }

    [Fact]
    public void CompositeStore_StickFirst_ThenOriginal_ThenMissing()
    {
        var stick = new MemoryStore();
        var original = new MemoryStore();
        stick.Put("aa", new byte[] { 1 });
        original.Put("aa", new byte[] { 2 });
        original.Put("bb", new byte[] { 3 });
        var composite = new ReaderSession.CompositeStore(stick, original);
        Assert.Equal(new byte[] { 1 }, composite.Get("aa"));
        Assert.Equal(new byte[] { 3 }, composite.Get("bb"));
        Assert.Throws<InvalidDataException>(() => composite.Get("cc"));
        Assert.True(composite.Exists("aa"));
        Assert.False(composite.Exists("cc"));
        composite.Put("dd", new byte[] { 4 });
        Assert.True(stick.Exists("dd"));
        Assert.False(original.Exists("dd"));
    }

    [Fact]
    public async Task Dav_Get_ReturnsPlaintext_AndMissingChunk_Is404NotPartial()
    {
        var (session, dav, _) = DavSession("hello reader");
        using (session)
        {
            var get = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "GET",
                Segments = new[] { "hello.txt" },
            });
            Assert.Equal(200, get.Status);
            Assert.Equal("hello reader", Encoding.UTF8.GetString(await StreamBytes(get)));

            // A file whose chunks are not on the stick or the
            // original location is 404, never a partial stream.
            session.Files.Add(new ReaderSession.Entry
            {
                Path = "gone.txt",
                Size = 3,
                ModifiedUtc = DateTimeOffset.UtcNow,
                ChunkIds = new() { new string('a', 64) },
            });
            var missing = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "GET",
                Segments = new[] { "gone.txt" },
            });
            Assert.Equal(404, missing.Status);
            Assert.Null(missing.StreamBody);
        }
    }

    [Fact]
    public void Dav_Propfind_ListsFiles_AndIntermediateCollections()
    {
        var (session, dav, _) = DavSession("x", "dir/sub/nested.txt");
        using (session)
        {
            session.Files.Add(new ReaderSession.Entry
            {
                Path = "top.txt",
                Size = 1,
                ModifiedUtc = DateTimeOffset.UtcNow,
                ChunkIds = new(),
            });
            var depth1 = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "PROPFIND",
                Segments = Array.Empty<string>(),
                Depth = "1",
            });
            Assert.Equal(207, depth1.Status);
            string xml1 = Encoding.UTF8.GetString(depth1.Body!);
            Assert.Contains("top.txt", xml1);
            Assert.Contains("dir", xml1);
            Assert.DoesNotContain("nested.txt", xml1);
            var infinity = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "PROPFIND",
                Segments = Array.Empty<string>(),
                Depth = "infinity",
            });
            string xmlInf = Encoding.UTF8.GetString(infinity.Body!);
            Assert.Contains("nested.txt", xmlInf);
            Assert.Contains("sub", xmlInf);
        }
    }

    [Fact]
    public void Dav_Put_ThenDelete_ReencryptsPackage()
    {
        var (session, dav, packagePath) = DavSession("first");
        using (session)
        {
            var put = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "PUT",
                Segments = new[] { "new.txt" },
                Body = Encoding.UTF8.GetBytes("second"),
            });
            Assert.Equal(201, put.Status);
            var putAgain = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "PUT",
                Segments = new[] { "new.txt" },
                Body = Encoding.UTF8.GetBytes("third"),
            });
            Assert.Equal(204, putAgain.Status);

            // The stick package now carries the new file, and a
            // Service-side decrypt (the main machine's adapter)
            // reads it — one format, both sides.
            var envelope = JsonSerializer.Deserialize<OfflineExportEnvelope.Package>(
                File.ReadAllText(packagePath))!;
            var payload = OfflineExportEnvelope.DecryptPackage(envelope, ExportSecret);
            Assert.Contains(payload.Files, f => f.Path == "new.txt");
            var servicePayload = OfflineExport.DecryptPackage(
                new OfflineExportPackageDto(envelope.Format, envelope.KdfSaltB64,
                    envelope.KdfIterations, envelope.NonceB64, envelope.DataB64),
                ExportSecret);
            Assert.Contains(servicePayload.Files, f => f.Path == "new.txt");

            var del = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "DELETE",
                Segments = new[] { "new.txt" },
            });
            Assert.Equal(204, del.Status);
            var delMissing = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "DELETE",
                Segments = new[] { "new.txt" },
            });
            Assert.Equal(404, delMissing.Status);
        }
    }

    [Fact]
    public void Dav_Lock_Conflicts_AndUnlock()
    {
        var (session, dav, _) = DavSession("locked-content");
        using (session)
        {
            var locked = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "LOCK",
                Segments = new[] { "hello.txt" },
            });
            Assert.Equal(200, locked.Status);
            string token = locked.Headers["Lock-Token"];
            var conflict = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "LOCK",
                Segments = new[] { "hello.txt" },
            });
            Assert.Equal(423, conflict.Status);
            var putLocked = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "PUT",
                Segments = new[] { "hello.txt" },
                Body = Encoding.UTF8.GetBytes("no"),
            });
            Assert.Equal(423, putLocked.Status);
            var putWithToken = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "PUT",
                Segments = new[] { "hello.txt" },
                Body = Encoding.UTF8.GetBytes("yes"),
                LockToken = token,
            });
            Assert.Equal(204, putWithToken.Status);
            var badUnlock = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "UNLOCK",
                Segments = Array.Empty<string>(),
                LockToken = "opaquelocktoken:nope",
            });
            Assert.Equal(409, badUnlock.Status);
            var unlock = dav.Handle(new ReaderDavHandler.Request
            {
                Method = "UNLOCK",
                Segments = Array.Empty<string>(),
                LockToken = token,
            });
            Assert.Equal(204, unlock.Status);
        }
    }

    [Fact]
    public void Dav_LockedSession_Refuses()
    {
        var (session, dav, _) = DavSession("x");
        session.Lock();
        var get = dav.Handle(new ReaderDavHandler.Request
        {
            Method = "GET",
            Segments = new[] { "hello.txt" },
        });
        Assert.Equal(423, get.Status);
        session.Dispose();
    }

    [Fact]
    public void ExportFull_CopiesChunks_AndReportsMissing()
    {
        string sourceDir = Path.Combine(_workDir, "source");
        var source = new LocalBlobStore(sourceDir);
        byte[] key = OfflineChunkStore.DeriveContentKey(VaultSecret);
        List<string> chunkIds;
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes("full export content")))
            (chunkIds, _) = OfflineChunkStore.Store(input, source, key);
        var folder = new OfflineFolderEntry("F", "STICK", DateTimeOffset.UtcNow)
        {
            Files = new()
            {
                new("a.txt", 19, DateTimeOffset.UtcNow, chunkIds),
                new("gone.txt", 1, DateTimeOffset.UtcNow, new[] { new string('b', 64) }),
            },
        };
        var location = new BackupLocation { Name = "STICK", Type = "usb", VolumeSerial = "ABCD-1234" };
        string destDir = Path.Combine(_workDir, "dest");
        var (status, payload) = DashboardApi.ExportOfflineFull(
            folder, location, source, destDir, ExportSecret);
        Assert.Equal(200, status);
        var summary = Assert.IsType<OfflineExportFullSummaryDto>(payload);
        Assert.Equal(2, summary.Files);
        Assert.Equal(chunkIds.Count, summary.Copied);
        Assert.Equal(1, summary.MissingChunks);
        Assert.Equal(new[] { "gone.txt" }, summary.MissingFiles);
        string manifestPath = OfflineStickLayout.ManifestPath(destDir);
        Assert.True(File.Exists(manifestPath));
        var envelope = JsonSerializer.Deserialize<OfflineExportEnvelope.Package>(File.ReadAllText(manifestPath))!;
        var decrypted = OfflineExportEnvelope.DecryptPackage(envelope, ExportSecret);
        Assert.Equal("F", decrypted.Folder);
        Assert.Equal(2, decrypted.Files.Count);

        // Second run: everything present is skipped, not recopied.
        var (status2, payload2) = DashboardApi.ExportOfflineFull(
            folder, location, source, destDir, ExportSecret);
        Assert.Equal(200, status2);
        var summary2 = Assert.IsType<OfflineExportFullSummaryDto>(payload2);
        Assert.Equal(0, summary2.Copied);
        Assert.Equal(chunkIds.Count, summary2.Skipped);
    }

    [Fact]
    public void WinReReader_Validation_AndArguments()
    {
        string exe = Path.Combine(_workDir, "FileKeepReader.exe");
        File.WriteAllText(exe, "fake");
        string pkg = Path.Combine(_workDir, "pkg.filekeep-export.json");
        File.WriteAllText(pkg, "{}");
        string fullDir = Path.Combine(_workDir, "full");
        Directory.CreateDirectory(fullDir);
        File.WriteAllText(Path.Combine(fullDir, OfflineStickLayout.ManifestFileName), "{}");

        Assert.Null(DashboardApi.ValidateReaderOptions(null, null, null));
        Assert.Null(DashboardApi.ValidateReaderOptions(exe, null, "None"));
        Assert.Null(DashboardApi.ValidateReaderOptions(exe, pkg, "Manifest"));
        Assert.Null(DashboardApi.ValidateReaderOptions(exe, fullDir, "Full"));
        Assert.NotNull(DashboardApi.ValidateReaderOptions(null, null, "Bogus"));
        Assert.NotNull(DashboardApi.ValidateReaderOptions(null, pkg, "Manifest"));
        Assert.NotNull(DashboardApi.ValidateReaderOptions(exe, null, "Manifest"));
        Assert.NotNull(DashboardApi.ValidateReaderOptions(Path.Combine(_workDir, "missing.exe"), null, "None"));
        Assert.NotNull(DashboardApi.ValidateReaderOptions(exe, Path.Combine(_workDir, "missing.json"), "Manifest"));
        Assert.NotNull(DashboardApi.ValidateReaderOptions(exe, pkg, "Full"));
        Assert.NotNull(DashboardApi.ValidateReaderOptions(exe, fullDir, "None"));

        Assert.Empty(DashboardApi.BuildReaderArguments(null, null, null));
        Assert.Equal(new[] { "-IncludeReader", "-ReaderExe", exe },
            DashboardApi.BuildReaderArguments(exe, null, "None"));
        Assert.Equal(new[] { "-IncludeReader", "-ReaderExe", exe, "-ReaderExportPath", fullDir, "-ReaderExportMode", "Full" },
            DashboardApi.BuildReaderArguments(exe, fullDir, "Full"));
    }
}
