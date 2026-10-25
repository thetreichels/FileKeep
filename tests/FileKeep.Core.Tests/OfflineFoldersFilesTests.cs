using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Offline Folders slice 2: file entries in the encrypted catalog,
/// store/retrieve through locations (plain, disguised, USB-folder),
/// update/reload, the loopback WebDAV share, and forensic lockup.
///
/// Fixture note (same as slice 1): test configs carry a fake DPAPI
/// blob, so the "current secret" resolves through the
/// FILEKEEP_PASSPHRASE env var, which GetPassphrase checks first.
/// SMB test locations point at local temp directories: SmbShare
/// treats a local path as an already-connected share, so no network
/// is involved. USB resolution needs a real drive letter, so USB
/// coverage goes through the folder store directly plus a
/// not-connected ResolveStore case.
/// </summary>
public sealed class OfflineFoldersFilesTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string Pass = "the-current-passphrase";

    public OfflineFoldersFilesTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-off-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string CatalogPath() => Path.Combine(_workDir, "offline-folders.json");
    private string ShareDir(string name)
    {
        string dir = Path.Combine(_workDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ServiceConfig ConfigWith(params BackupLocation[] locations)
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        foreach (var loc in locations)
            config.Locations.Add(loc);
        return config;
    }

    private static (ServiceConfig Config, OfflineFoldersSession Session, string Catalog) UnlockedFolder(
        string workDir, string catalogPath, BackupLocation location, string folderName = "Vault")
    {
        var config = ConfigWith(location);
        var session = new OfflineFoldersSession();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, catalogPath, Pass, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, catalogPath, folderName, location.Name).Status);
        return (config, session, catalogPath);
    }

    private static byte[] Content(int size, int seed)
    {
        var rnd = new Random(seed);
        byte[] data = new byte[size];
        rnd.NextBytes(data);
        // A recognizable marker so disk scans can prove the plaintext
        // never landed anywhere as a file.
        byte[] marker = Encoding.UTF8.GetBytes("PLAINTEXT-MARKER-OFFLINE-FILES-TEST");
        if (size >= marker.Length + 16)
            Buffer.BlockCopy(marker, 0, data, 16, marker.Length);
        return data;
    }

    private static List<OfflineFileDto> FilesPayload((int Status, object Payload) result)
    {
        Assert.Equal(200, result.Status);
        return Assert.IsAssignableFrom<List<OfflineFileDto>>(result.Payload);
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }

    private static string DavText(OfflineFoldersDavHandler.DavResult result)
    {
        Assert.NotNull(result.BodyBytes);
        return Encoding.UTF8.GetString(result.BodyBytes!);
    }

    private static async Task<byte[]> DavGetBytes(
        OfflineFoldersDavHandler handler, params string[] segments)
    {
        var result = handler.Handle(new OfflineFoldersDavHandler.DavRequest
        {
            Method = "GET",
            Segments = segments,
        });
        Assert.Equal(200, result.Status);
        Assert.NotNull(result.BodyWriter);
        using var ms = new MemoryStream();
        await result.BodyWriter!(ms);
        return ms.ToArray();
    }

    [Fact]
    public void StoreRetrieve_Smb_RoundTrip_MultiChunk()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        byte[] data = Content(9 * 1024 * 1024 + 123, seed: 7);
        var stored = DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "big.bin", new MemoryStream(data), DateTimeOffset.UtcNow);
        Assert.Equal(200, stored.Status);

        using var outMs = new MemoryStream();
        var (status, error) = DashboardApi.StreamOfflineFile(config, session, "Vault", "big.bin", outMs);
        Assert.Equal(200, status);
        Assert.Null(error);
        Assert.Equal(data, outMs.ToArray());

        var files = FilesPayload(DashboardApi.GetOfflineFolderFiles(session, "Vault"));
        Assert.Single(files);
        Assert.Equal("big.bin", files[0].Path);
        Assert.Equal(data.Length, files[0].Size);
    }

    [Fact]
    public void StoreRetrieve_EmptyFile()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "empty.txt", new MemoryStream(Array.Empty<byte>()), DateTimeOffset.UtcNow).Status);
        using var outMs = new MemoryStream();
        Assert.Equal(200, DashboardApi.StreamOfflineFile(config, session, "Vault", "empty.txt", outMs).Status);
        Assert.Empty(outMs.ToArray());
    }

    [Fact]
    public void CatalogFile_ContainsNoPlaintextFileNamesOrContent()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "SecretReport.txt", new MemoryStream(Content(1024, seed: 3)), DateTimeOffset.UtcNow).Status);
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "sub/Private Notes.md", new MemoryStream(Content(512, seed: 4)), DateTimeOffset.UtcNow).Status);

        string fileText = File.ReadAllText(catalog);
        Assert.DoesNotContain("SecretReport", fileText);
        Assert.DoesNotContain("Private Notes", fileText);
        Assert.DoesNotContain("PLAINTEXT-MARKER", fileText);
        // No store file anywhere under the share holds the plaintext either.
        foreach (string f in Directory.EnumerateFiles(share, "*", SearchOption.AllDirectories))
            Assert.False(ContainsBytes(File.ReadAllBytes(f), Encoding.UTF8.GetBytes("PLAINTEXT-MARKER")));
    }

    [Fact]
    public void CatalogV1_Loads_WithEmptyFileIndex()
    {
        string path = CatalogPath();
        byte[] key = OfflineFoldersCatalog.DeriveKey(Pass);
        try
        {
            string plaintext = "{\"version\":1,\"entries\":[{\"name\":\"Old\",\"locationId\":\"nas\",\"addedUtc\":\"2026-01-01T00:00:00Z\"}]}";
            byte[] plain = Encoding.UTF8.GetBytes(plaintext);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16];
            using (var gcm = new AesGcm(key, 16))
                gcm.Encrypt(nonce, plain, cipher, tag);
            byte[] blob = nonce.Concat(cipher).Concat(tag).ToArray();
            File.WriteAllText(path, Convert.ToBase64String(blob));

            var config = ConfigWith(new BackupLocation { Name = "nas", Type = "smb", Share = ShareDir("share") });
            var session = new OfflineFoldersSession();
            Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, path, Pass, false, null).Status);
            var files = FilesPayload(DashboardApi.GetOfflineFolderFiles(session, "Old"));
            Assert.Empty(files);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public void StoreRetrieve_SmbDisguised_RoundTrip()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation
            {
                Name = "stick", Type = "smb", Share = share,
                DisguiseEnabled = true, CoverName = "Photos",
            });
        byte[] data = Content(100_000, seed: 11);
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "diary.txt", new MemoryStream(data), DateTimeOffset.UtcNow).Status);

        // On disk: flat in the cover folder, derived media-style names
        // only — nothing named after the file, no chunks/ layout.
        string cover = Path.Combine(share, "Photos");
        Assert.True(Directory.Exists(cover));
        Assert.False(Directory.Exists(Path.Combine(share, "chunks")));
        string[] stored = Directory.GetFiles(cover);
        Assert.NotEmpty(stored);
        foreach (string f in stored)
        {
            string name = Path.GetFileName(f);
            Assert.DoesNotContain("diary", name);
            string ext = Path.GetExtension(name);
            Assert.Contains(ext, DisguisedLayout.Extensions);
        }

        using var outMs = new MemoryStream();
        Assert.Equal(200, DashboardApi.StreamOfflineFile(config, session, "Vault", "diary.txt", outMs).Status);
        Assert.Equal(data, outMs.ToArray());
    }

    [Fact]
    public void StoreRetrieve_UsbFolder_PlainAndDisguised()
    {
        string root = ShareDir("usbroot");
        byte[] key = OfflineFileStore.DeriveContentKey(Pass);
        try
        {
            byte[] data = Content(200_000, seed: 13);
            // Plain USB folder store.
            var plain = new LocalBlobStore(Path.Combine(root, "plain"));
            var (ids, size) = OfflineFileStore.Store(new MemoryStream(data), plain, key);
            Assert.Equal(data.Length, size);
            using (var ms = new MemoryStream())
            {
                OfflineFileStore.Retrieve(ids, plain, key, ms);
                Assert.Equal(data, ms.ToArray());
            }
            // Disguised USB folder store: flat cover folder, wrapped blobs.
            var layout = new DisguisedLayout("stick", "Photos", false);
            var disguised = new LocalBlobStore(root, layout);
            var (ids2, _) = OfflineFileStore.Store(new MemoryStream(data), disguised, key);
            string cover = Path.Combine(root, "Photos");
            Assert.True(Directory.Exists(cover));
            foreach (string f in Directory.GetFiles(cover))
                Assert.DoesNotContain("chunks", Path.GetFileName(f));
            using (var ms = new MemoryStream())
            {
                OfflineFileStore.Retrieve(ids2, disguised, key, ms);
                Assert.Equal(data, ms.ToArray());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public void UpdateReload_SupersedesOldContent()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        byte[] v1 = Content(50_000, seed: 21);
        byte[] v2 = Content(60_000, seed: 22);
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "notes.txt", new MemoryStream(v1), DateTimeOffset.UtcNow).Status);
        byte[] key = OfflineFoldersCatalog.DeriveKey(Pass);
        List<string> oldChunks;
        try { oldChunks = OfflineFoldersCatalog.Load(catalog, key)[0].Files[0].ChunkIds.ToList(); }
        finally { CryptographicOperations.ZeroMemory(key); }

        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "notes.txt", new MemoryStream(v2), DateTimeOffset.UtcNow).Status);
        using (var ms = new MemoryStream())
        {
            Assert.Equal(200, DashboardApi.StreamOfflineFile(config, session, "Vault", "notes.txt", ms).Status);
            Assert.Equal(v2, ms.ToArray());
        }
        byte[] key2 = OfflineFoldersCatalog.DeriveKey(Pass);
        try
        {
            var entry = OfflineFoldersCatalog.Load(catalog, key2)[0].Files[0];
            Assert.Equal(v2.Length, entry.Size);
            Assert.DoesNotContain(oldChunks[0], entry.ChunkIds);
        }
        finally { CryptographicOperations.ZeroMemory(key2); }
    }

    [Fact]
    public void FilePathValidation_RejectsBadPaths_AcceptsNested()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        foreach (string bad in new[] { "", "  ", "../escape.txt", "a//b.txt", @"a\b.txt",
            "a:b.txt", "a\tb.txt", new string('x', 129) + ".txt", ".hidden/../x.txt" })
        {
            var r = DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
                bad, new MemoryStream(new byte[] { 1 }), DateTimeOffset.UtcNow);
            Assert.Equal(400, r.Status);
        }
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "sub/dir/deep.txt", new MemoryStream(new byte[] { 1 }), DateTimeOffset.UtcNow).Status);
    }

    [Fact]
    public void Store_Locked_UnknownFolder_UnknownLocation_Errors()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Assert.Equal(404, DashboardApi.StoreOfflineFile(config, session, catalog, "Nope",
            "a.txt", new MemoryStream(new byte[] { 1 }), DateTimeOffset.UtcNow).Status);
        Assert.Equal(200, DashboardApi.LockOfflineFolders(session).Status);
        Assert.Equal(403, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "a.txt", new MemoryStream(new byte[] { 1 }), DateTimeOffset.UtcNow).Status);
        Assert.Equal(403, DashboardApi.GetOfflineFolderFiles(session, "Vault").Status);

        // Location removed after the folder was created: store refuses.
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, catalog, Pass, false, null).Status);
        config.Locations.Clear();
        Assert.Equal(400, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "a.txt", new MemoryStream(new byte[] { 1 }), DateTimeOffset.UtcNow).Status);
    }

    [Fact]
    public void Store_UsbNotConnected_Refused()
    {
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "stick", Type = "usb", VolumeSerial = "DEADBEEF", UsbFolder = "FileKeep" });
        var (status, payload) = DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "a.txt", new MemoryStream(new byte[] { 1 }), DateTimeOffset.UtcNow);
        Assert.Equal(400, status);
        string message = payload.GetType().GetProperty("Error")?.GetValue(payload) as string ?? "";
        Assert.Contains("not currently connected", message);
    }

    [Fact]
    public void Retrieve_AbortsWhenLockedMidStream()
    {
        string share = ShareDir("share");
        byte[] key = OfflineFileStore.DeriveContentKey(Pass);
        try
        {
            var store = new LocalBlobStore(share);
            // 40 MiB: far above the 4 MiB CDC average, so multiple
            // chunks are near-certain (a 9 MiB sample once formed one).
            byte[] data = Content(40 * 1024 * 1024 + 5, seed: 31);
            var (ids, _) = OfflineFileStore.Store(new MemoryStream(data), store, key);
            Assert.True(ids.Count > 1);
            int calls = 0;
            using var ms = new MemoryStream();
            var ex = Assert.Throws<OperationCanceledException>(() =>
                OfflineFileStore.Retrieve(ids, store, key, ms, () => ++calls <= 1));
            Assert.Contains("locked", ex.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public async Task Dav_RoundTrip_PropfindGetPutDelete()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        byte[] report = Content(5000, seed: 41);
        byte[] notes = Content(300, seed: 42);
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "docs/report.txt", new MemoryStream(report), DateTimeOffset.UtcNow).Status);
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "notes.md", new MemoryStream(notes), DateTimeOffset.UtcNow).Status);
        var handler = new OfflineFoldersDavHandler(config, session, catalog);

        var root = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "PROPFIND", Segments = Array.Empty<string>(), Depth = 1 });
        Assert.Equal(207, root.Status);
        Assert.Contains("Vault", DavText(root));

        var folder = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "PROPFIND", Segments = new[] { "Vault" }, Depth = 1 });
        Assert.Equal(207, folder.Status);
        string folderXml = DavText(folder);
        Assert.Contains("notes.md", folderXml);
        Assert.Contains("docs", folderXml);

        var docs = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "PROPFIND", Segments = new[] { "Vault", "docs" }, Depth = 1 });
        Assert.Contains("report.txt", DavText(docs));

        Assert.Equal(report, await DavGetBytes(handler, "Vault", "docs", "report.txt"));

        // PUT replaces: 204, new bytes back, catalog size updated.
        byte[] report2 = Content(6000, seed: 43);
        var put = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "PUT", Segments = new[] { "Vault", "docs", "report.txt" }, Body = new MemoryStream(report2) });
        Assert.Equal(204, put.Status);
        Assert.Equal(report2, await DavGetBytes(handler, "Vault", "docs", "report.txt"));
        var files = FilesPayload(DashboardApi.GetOfflineFolderFiles(session, "Vault"));
        Assert.Equal(report2.Length, files.First(f => f.Path == "docs/report.txt").Size);

        // PUT creates: 201.
        var putNew = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "PUT", Segments = new[] { "Vault", "brand.txt" }, Body = new MemoryStream(notes) });
        Assert.Equal(201, putNew.Status);
        Assert.Equal(notes, await DavGetBytes(handler, "Vault", "brand.txt"));

        // DELETE removes; GET then 404s.
        var del = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "DELETE", Segments = new[] { "Vault", "brand.txt" } });
        Assert.Equal(204, del.Status);
        var gone = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "GET", Segments = new[] { "Vault", "brand.txt" } });
        Assert.Equal(404, gone.Status);
    }

    [Fact]
    public void Dav_LockUnlock_AndLockedShareRefused()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "a.txt", new MemoryStream(Content(100, seed: 51)), DateTimeOffset.UtcNow).Status);
        var handler = new OfflineFoldersDavHandler(config, session, catalog);

        var options = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "OPTIONS", Segments = Array.Empty<string>() });
        Assert.Equal(200, options.Status);
        Assert.Equal("1", options.Headers["DAV"]);

        var locked = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "LOCK", Segments = new[] { "Vault", "a.txt" } });
        Assert.Equal(200, locked.Status);
        Assert.True(locked.Headers.TryGetValue("Lock-Token", out string? tokenHeader));
        string token = tokenHeader!.Trim('<', '>', ' ');

        var conflict = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "PUT", Segments = new[] { "Vault", "a.txt" }, Body = new MemoryStream(new byte[] { 9 }) });
        Assert.Equal(423, conflict.Status);

        var withToken = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "PUT", Segments = new[] { "Vault", "a.txt" }, Body = new MemoryStream(new byte[] { 9 }), LockToken = token });
        Assert.Equal(204, withToken.Status);

        var unlocked = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "UNLOCK", Segments = new[] { "Vault", "a.txt" }, LockToken = token });
        Assert.Equal(204, unlocked.Status);

        // Locking the card drops the share entirely, including locks.
        Assert.Equal(200, DashboardApi.LockOfflineFolders(session).Status);
        Assert.Null(session.CopyContentKey());
        var refused = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "PROPFIND", Segments = Array.Empty<string>(), Depth = 1 });
        Assert.Equal(403, refused.Status);
        var getRefused = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "GET", Segments = new[] { "Vault", "a.txt" } });
        Assert.Equal(403, getRefused.Status);

        // Re-unlock re-reads from disk: files are back.
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, catalog, Pass, false, null).Status);
        var files = FilesPayload(DashboardApi.GetOfflineFolderFiles(session, "Vault"));
        Assert.Single(files);
    }

    [Fact]
    public async Task Dav_GetAfterLock_WriterRefuses()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "a.txt", new MemoryStream(Content(100, seed: 61)), DateTimeOffset.UtcNow).Status);
        var handler = new OfflineFoldersDavHandler(config, session, catalog);
        var get = handler.Handle(new OfflineFoldersDavHandler.DavRequest
            { Method = "GET", Segments = new[] { "Vault", "a.txt" } });
        Assert.Equal(200, get.Status);
        Assert.Equal(200, DashboardApi.LockOfflineFolders(session).Status);
        // The writer was built while unlocked; invoking it after lock
        // must fail, never serve bytes.
        await Assert.ThrowsAsync<OfflineFoldersDavHandler.DavStreamException>(async () =>
        {
            using var ms = new MemoryStream();
            await get.BodyWriter!(ms);
        });
        Assert.NotNull(get.BodyWriter);
    }

    [Fact]
    public void Serve_CreatesNoPlaintextFiles()
    {
        string share = ShareDir("share");
        var (config, session, catalog) = UnlockedFolder(_workDir, CatalogPath(),
            new BackupLocation { Name = "nas", Type = "smb", Share = share });
        byte[] data = Content(2000, seed: 71);
        Assert.Equal(200, DashboardApi.StoreOfflineFile(config, session, catalog, "Vault",
            "secret.txt", new MemoryStream(data), DateTimeOffset.UtcNow).Status);
        var before = Directory.EnumerateFiles(share, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(share, f)).OrderBy(x => x).ToList();

        var handler = new OfflineFoldersDavHandler(config, session, catalog);
        Assert.Equal(data, DavGetBytes(handler, "Vault", "secret.txt").GetAwaiter().GetResult());

        var after = Directory.EnumerateFiles(share, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(share, f)).OrderBy(x => x).ToList();
        Assert.Equal(before, after);
        foreach (string f in Directory.EnumerateFiles(share, "*", SearchOption.AllDirectories))
            Assert.False(ContainsBytes(File.ReadAllBytes(f), Encoding.UTF8.GetBytes("PLAINTEXT-MARKER")));
    }
}
