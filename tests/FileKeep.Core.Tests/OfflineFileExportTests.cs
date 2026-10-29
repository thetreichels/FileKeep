using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Per-file offline export (slice 3, part 2 remainder): a package
/// holding exactly one file's entry, in the same v1 envelope as a
/// folder package, and an import that merges it into an existing
/// folder instead of refusing the duplicate folder name. Written
/// before the implementation; the new members do not exist yet.
/// </summary>
public sealed class OfflineFileExportTests : IDisposable
{
    private readonly string _workDir;

    private const string CardSecret = "the-card-secret";
    private const string ExportSecret = "the-export-passphrase";

    public OfflineFileExportTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-exp-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string CatalogPath() => Path.Combine(_workDir, "offline-" + Guid.NewGuid().ToString("N") + ".json");

    private static string ChunkId(string seed) => Hashing.Sha256Hex(Encoding.UTF8.GetBytes(seed));

    private static OfflineFolderEntry FolderEntry() =>
        new("F", "NAS", DateTimeOffset.UtcNow)
        {
            Files = new()
            {
                new("a.txt", 5, DateTimeOffset.UtcNow, new[] { ChunkId("chunk-a") }),
                new("dir/b.txt", 7, DateTimeOffset.UtcNow, new[] { ChunkId("chunk-b1"), ChunkId("chunk-b2") }),
            },
        };

    private static BackupLocation SmbLocation() => new()
    {
        Name = "NAS", Type = "smb", Share = @"\\NAS\backups\fk",
        SmbUser = "someuser", SmbPasswordPlaintext = "must-never-export",
    };

    private static OfflineFoldersSession UnlockedSession(List<OfflineFolderEntry> entries)
    {
        var session = new OfflineFoldersSession();
        session.Unlock(OfflineFoldersCatalog.DeriveKey(CardSecret),
            OfflineFileStore.DeriveContentKey(CardSecret), entries);
        return session;
    }

    private static string ErrorOf(object payload)
    {
        var prop = payload.GetType().GetProperty("Error") ?? payload.GetType().GetProperty("error");
        Assert.NotNull(prop);
        return (string?)prop.GetValue(payload) ?? "";
    }

    private static OfflineExportPackageDto EncryptPayload(OfflineExport.Payload payload, string passphrase)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        using var pbkdf2 = new Rfc2898DeriveBytes(passphrase, salt, 600_000, HashAlgorithmName.SHA512);
        byte[] key = pbkdf2.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(ChunkCrypto.NonceSizeBytes);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[ChunkCrypto.TagSizeBytes];
        try
        {
            using (var gcm = new AesGcm(key, ChunkCrypto.TagSizeBytes))
                gcm.Encrypt(nonce, plaintext, ciphertext, tag);
            byte[] data = new byte[ciphertext.Length + tag.Length];
            ciphertext.CopyTo(data, 0);
            tag.CopyTo(data, ciphertext.Length);
            return new OfflineExportPackageDto(OfflineExport.Format,
                Convert.ToBase64String(salt), 600_000,
                Convert.ToBase64String(nonce), Convert.ToBase64String(data));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    [Fact]
    public void FilePackage_RoundTrips_ContainsOnlyThatFile()
    {
        var folder = FolderEntry();
        var file = folder.Files[1];
        var package = OfflineExport.CreateFilePackage(folder, file, SmbLocation(), ExportSecret);

        Assert.Equal(OfflineExport.Format, package.Format);
        // The envelope itself carries no filenames at all.
        Assert.DoesNotContain("a.txt", JsonSerializer.Serialize(package));
        Assert.DoesNotContain("dir/b.txt", JsonSerializer.Serialize(package));

        var payload = OfflineExport.DecryptPackage(package, ExportSecret);
        Assert.Equal("file", payload.Scope);
        Assert.Equal("F", payload.Folder);
        Assert.Equal("smb", payload.Location.Type);
        Assert.Single(payload.Files);
        Assert.Equal("dir/b.txt", payload.Files[0].Path);
        Assert.Equal(new[] { ChunkId("chunk-b1"), ChunkId("chunk-b2") }, payload.Files[0].ChunkIds);
    }

    [Fact]
    public void FolderPackage_Scope_IsFolder()
    {
        var package = OfflineExport.CreatePackage(FolderEntry(), SmbLocation(), ExportSecret);
        var payload = OfflineExport.DecryptPackage(package, ExportSecret);
        Assert.Equal("folder", payload.Scope);
        Assert.Equal(2, payload.Files.Count);
    }

    [Fact]
    public void Decrypt_UnknownScope_Refused()
    {
        var payload = new OfflineExport.Payload
        {
            Folder = "F",
            Location = new OfflineExport.LocationPointer { Type = "smb", Name = "NAS", Share = @"\\NAS\backups\fk" },
            Files = new() { new OfflineExport.FileRef { Path = "a.txt", Size = 5, ModifiedUtc = DateTimeOffset.UtcNow, ChunkIds = new() { ChunkId("chunk-a") } } },
            Scope = "bogus",
        };
        var package = EncryptPayload(payload, ExportSecret);
        Assert.Throws<InvalidDataException>(() => OfflineExport.DecryptPackage(package, ExportSecret));
    }

    [Fact]
    public void CreateFilePackage_RefusesNonSmbUsb()
    {
        var folder = FolderEntry();
        var nntp = new BackupLocation { Name = "News", Type = "nntp", Host = "news.example.invalid" };
        Assert.Throws<InvalidOperationException>(() =>
            OfflineExport.CreateFilePackage(folder, folder.Files[0], nntp, ExportSecret));
    }

    [Fact]
    public void Handler_ExportFile_ReturnsSingleFilePackage()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        var session = UnlockedSession(new List<OfflineFolderEntry> { FolderEntry() });

        var (status, payload) = DashboardApi.ExportOfflineFile(config, session, "F", "dir/b.txt", ExportSecret);
        Assert.Equal(200, status);
        var result = Assert.IsType<OfflineExportResultDto>(payload);
        Assert.Equal("F-b.txt.filekeep-export.json", result.FileName);
        var decrypted = OfflineExport.DecryptPackage(result.Package, ExportSecret);
        Assert.Equal("file", decrypted.Scope);
        Assert.Single(decrypted.Files);
        Assert.Equal("dir/b.txt", decrypted.Files[0].Path);
    }

    [Fact]
    public void Handler_ExportFile_Errors()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        var session = UnlockedSession(new List<OfflineFolderEntry> { FolderEntry() });

        var (lockedStatus, _) = DashboardApi.ExportOfflineFile(
            config, new OfflineFoldersSession(), "F", "a.txt", ExportSecret);
        Assert.Equal(403, lockedStatus);

        var (noFolderStatus, _) = DashboardApi.ExportOfflineFile(config, session, "Nope", "a.txt", ExportSecret);
        Assert.Equal(404, noFolderStatus);

        var (noFileStatus, noFilePayload) = DashboardApi.ExportOfflineFile(config, session, "F", "missing.txt", ExportSecret);
        Assert.Equal(404, noFileStatus);
        Assert.Contains("No such offline file", ErrorOf(noFilePayload));

        var (noPassStatus, _) = DashboardApi.ExportOfflineFile(config, session, "F", "a.txt", null);
        Assert.Equal(400, noPassStatus);

        var orphanConfig = new ServiceConfig();
        orphanConfig.Locations.Add(new BackupLocation { Name = "News", Type = "nntp", Host = "news.example.invalid" });
        var orphan = new OfflineFolderEntry("Orphan", "News", DateTimeOffset.UtcNow)
        {
            Files = new() { new("a.txt", 5, DateTimeOffset.UtcNow, new[] { ChunkId("chunk-a") }) },
        };
        var orphanSession = UnlockedSession(new List<OfflineFolderEntry> { orphan });
        var (nntpStatus, _) = DashboardApi.ExportOfflineFile(orphanConfig, orphanSession, "Orphan", "a.txt", ExportSecret);
        Assert.Equal(400, nntpStatus);
    }

    [Fact]
    public void Handler_ImportFile_MergesIntoExistingFolder()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        var sourceFolder = new OfflineFolderEntry("F", "NAS", DateTimeOffset.UtcNow)
        {
            Files = new() { new("c.txt", 3, DateTimeOffset.UtcNow, new[] { ChunkId("chunk-c") }) },
        };
        var sourceSession = UnlockedSession(new List<OfflineFolderEntry> { sourceFolder });
        var (exportStatus, exportPayload) = DashboardApi.ExportOfflineFile(
            config, sourceSession, "F", "c.txt", ExportSecret);
        Assert.Equal(200, exportStatus);
        var package = Assert.IsType<OfflineExportResultDto>(exportPayload).Package;

        string catalog = CatalogPath();
        var targetSession = UnlockedSession(new List<OfflineFolderEntry> { FolderEntry() });
        var (status, payload) = DashboardApi.ImportOfflineFolder(config, targetSession, catalog, package, ExportSecret);
        Assert.Equal(200, status);
        var summary = Assert.IsType<OfflineImportSummaryDto>(payload);
        Assert.Equal("F", summary.Folder);
        Assert.Equal(1, summary.Files);
        Assert.Equal(1, summary.Chunks);
        Assert.Empty(summary.Warnings);
        var merged = Assert.Single(targetSession.GetEntries()!);
        Assert.Equal(3, merged.Files.Count);
        Assert.Contains(merged.Files, f => f.Path == "a.txt");
        Assert.Contains(merged.Files, f => f.Path == "dir/b.txt");
        Assert.Contains(merged.Files, f => f.Path == "c.txt");
        var loaded = OfflineFoldersCatalog.Load(catalog, OfflineFoldersCatalog.DeriveKey(CardSecret));
        Assert.Single(loaded);
        Assert.Equal(3, loaded[0].Files.Count);
    }

    [Fact]
    public void Handler_ImportFile_DuplicatePath_Refused()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        var sourceSession = UnlockedSession(new List<OfflineFolderEntry> { FolderEntry() });
        var (exportStatus, exportPayload) = DashboardApi.ExportOfflineFile(
            config, sourceSession, "F", "a.txt", ExportSecret);
        Assert.Equal(200, exportStatus);
        var package = Assert.IsType<OfflineExportResultDto>(exportPayload).Package;

        string catalog = CatalogPath();
        var targetSession = UnlockedSession(new List<OfflineFolderEntry> { FolderEntry() });
        var (status, payload) = DashboardApi.ImportOfflineFolder(config, targetSession, catalog, package, ExportSecret);
        Assert.Equal(400, status);
        Assert.Contains("a.txt", ErrorOf(payload));
        // Nothing changed: no duplicate, no silent swap.
        var files = Assert.Single(targetSession.GetEntries()!).Files;
        Assert.Equal(2, files.Count);
        Assert.Equal(new[] { ChunkId("chunk-a") },
            Assert.Single(files, f => f.Path == "a.txt").ChunkIds);
    }

    [Fact]
    public void Handler_ImportFile_CreatesFolderWhenAbsent()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        var sourceSession = UnlockedSession(new List<OfflineFolderEntry> { FolderEntry() });
        var (exportStatus, exportPayload) = DashboardApi.ExportOfflineFile(
            config, sourceSession, "F", "dir/b.txt", ExportSecret);
        Assert.Equal(200, exportStatus);
        var package = Assert.IsType<OfflineExportResultDto>(exportPayload).Package;

        string catalog = CatalogPath();
        var targetSession = UnlockedSession(new List<OfflineFolderEntry>());
        var (status, payload) = DashboardApi.ImportOfflineFolder(config, targetSession, catalog, package, ExportSecret);
        Assert.Equal(200, status);
        var summary = Assert.IsType<OfflineImportSummaryDto>(payload);
        Assert.Equal("F", summary.Folder);
        Assert.Equal(1, summary.Files);
        var created = Assert.Single(targetSession.GetEntries()!);
        Assert.Equal("F", created.Name);
        Assert.Single(created.Files);
        Assert.Equal("dir/b.txt", created.Files[0].Path);
        Assert.Single(OfflineFoldersCatalog.Load(catalog, OfflineFoldersCatalog.DeriveKey(CardSecret)));
    }

    [Fact]
    public void ExportFileResult_SerializesThroughProductionJson()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        var session = UnlockedSession(new List<OfflineFolderEntry> { FolderEntry() });
        var (status, payload) = DashboardApi.ExportOfflineFile(config, session, "F", "a.txt", ExportSecret);
        Assert.Equal(200, status);

        // The export-file route forwards this payload to Results.Json
        // under the production resolver chain; a missing registration
        // throws here the same way it 500s in the trimmed build.
        // This also covers the nested package DTO and, below, the
        // import/forget summaries the family shares.
        string json = JsonSerializer.Serialize(payload, payload.GetType(), ProductionJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("F-a.txt.filekeep-export.json", doc.RootElement.GetProperty("fileName").GetString());
        Assert.Equal(OfflineExport.Format, doc.RootElement.GetProperty("package").GetProperty("format").GetString());
        var roundTrip = JsonSerializer.Deserialize<OfflineExportResultDto>(json, ProductionJson.Options);
        Assert.NotNull(roundTrip);
        Assert.Equal("F-a.txt.filekeep-export.json", roundTrip.FileName);

        var importSummary = new OfflineImportSummaryDto("F", 1, 2, new List<string>());
        string importJson = JsonSerializer.Serialize(importSummary, ProductionJson.Options);
        Assert.Contains("\"folder\":\"F\"", importJson);

        var forgetSummary = new OfflineForgetSummaryDto("F", 1, 2, new List<string>());
        string forgetJson = JsonSerializer.Serialize(forgetSummary, ProductionJson.Options);
        Assert.Contains("\"jobsUsingLocation\":[]", forgetJson);
    }
}
