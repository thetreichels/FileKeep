using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Offline export + forget (slice 3, part 2): the encrypted
/// package format (no credentials, no plaintext filenames outside
/// the encrypted blob, fail-closed on format mismatch) and the
/// three handlers (export / import / forget) over the unlocked
/// card session. The reader (part 3) consumes this format.
/// </summary>
public sealed class OfflineExportTests : IDisposable
{
    private readonly string _workDir;

    private const string CardSecret = "the-card-secret";
    private const string ExportSecret = "the-export-passphrase";

    public OfflineExportTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-exp-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Package_RoundTrips_AndHidesNames()
    {
        var folder = FolderEntry();
        var package = OfflineExport.CreatePackage(folder, SmbLocation(), ExportSecret);

        Assert.Equal(OfflineExport.Format, package.Format);
        Assert.Equal(600_000, package.KdfIterations);
        // The envelope itself carries no filenames: names live only
        // inside the encrypted blob.
        Assert.DoesNotContain("a.txt", JsonSerializer.Serialize(package));
        Assert.DoesNotContain("dir/b.txt", JsonSerializer.Serialize(package));

        var payload = OfflineExport.DecryptPackage(package, ExportSecret);
        Assert.Equal("F", payload.Folder);
        Assert.Equal("smb", payload.Location.Type);
        Assert.Equal("NAS", payload.Location.Name);
        Assert.Equal(@"\\NAS\backups\fk", payload.Location.Share);
        Assert.Null(payload.Location.VolumeSerial);
        Assert.Equal(2, payload.Files.Count);
        Assert.Equal(new[] { ChunkId("chunk-b1"), ChunkId("chunk-b2") }, payload.Files[1].ChunkIds);
    }

    [Fact]
    public void Decrypt_WrongPassphrase_Throws()
    {
        var package = OfflineExport.CreatePackage(FolderEntry(), SmbLocation(), ExportSecret);
        Assert.ThrowsAny<CryptographicException>(() => OfflineExport.DecryptPackage(package, "not-the-passphrase"));
    }

    [Fact]
    public void Decrypt_TamperedData_Throws()
    {
        var package = OfflineExport.CreatePackage(FolderEntry(), SmbLocation(), ExportSecret);
        byte[] data = Convert.FromBase64String(package.DataB64);
        data[data.Length / 2] ^= 0xFF;
        var tampered = package with { DataB64 = Convert.ToBase64String(data) };
        Assert.ThrowsAny<CryptographicException>(() => OfflineExport.DecryptPackage(tampered, ExportSecret));
    }

    [Fact]
    public void Decrypt_WrongFormat_Refused()
    {
        var package = OfflineExport.CreatePackage(FolderEntry(), SmbLocation(), ExportSecret);
        var wrong = package with { Format = "FileKeep offline export v99" };
        Assert.Throws<InvalidDataException>(() => OfflineExport.DecryptPackage(wrong, ExportSecret));
    }

    [Fact]
    public void CreatePackage_RefusesNonSmbUsb()
    {
        var folder = FolderEntry();
        var nntp = new BackupLocation { Name = "News", Type = "nntp", Host = "news.example.invalid" };
        Assert.Throws<InvalidOperationException>(() => OfflineExport.CreatePackage(folder, nntp, ExportSecret));
    }

    [Fact]
    public void Handler_ExportImport_RoundTrip()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        var folder = FolderEntry();
        var sourceSession = UnlockedSession(new List<OfflineFolderEntry> { folder });

        var (exportStatus, exportPayload) = DashboardApi.ExportOfflineFolder(
            config, sourceSession, "F", ExportSecret);
        Assert.Equal(200, exportStatus);
        var result = Assert.IsType<OfflineExportResultDto>(exportPayload);
        Assert.Equal("F.filekeep-export.json", result.FileName);

        // Import into a machine whose config has the location: no warnings.
        string catalogB = CatalogPath();
        var targetSession = UnlockedSession(new List<OfflineFolderEntry>());
        var (importStatus, importPayload) = DashboardApi.ImportOfflineFolder(
            config, targetSession, catalogB, result.Package, ExportSecret);
        Assert.Equal(200, importStatus);
        var summary = Assert.IsType<OfflineImportSummaryDto>(importPayload);
        Assert.Equal("F", summary.Folder);
        Assert.Equal(2, summary.Files);
        Assert.Equal(3, summary.Chunks);
        Assert.Empty(summary.Warnings);
        var imported = Assert.Single(targetSession.GetEntries()!);
        Assert.Equal("F", imported.Name);
        Assert.Equal(2, imported.Files.Count);
        // The catalog on disk holds it under the card secret.
        var loaded = OfflineFoldersCatalog.Load(catalogB, OfflineFoldersCatalog.DeriveKey(CardSecret));
        Assert.Single(loaded);
        Assert.Equal("F", loaded[0].Name);

        // Import into a machine WITHOUT the location: warned, still imported.
        var bareConfig = new ServiceConfig();
        string catalogC = CatalogPath();
        var bareSession = UnlockedSession(new List<OfflineFolderEntry>());
        var (bareStatus, barePayload) = DashboardApi.ImportOfflineFolder(
            bareConfig, bareSession, catalogC, result.Package, ExportSecret);
        Assert.Equal(200, bareStatus);
        Assert.Single(Assert.IsType<OfflineImportSummaryDto>(barePayload).Warnings);
    }

    [Fact]
    public void Handler_Import_DuplicateAndWrongPassphrase_Refused()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        var package = OfflineExport.CreatePackage(FolderEntry(), SmbLocation(), ExportSecret);
        string catalog = CatalogPath();
        var session = UnlockedSession(new List<OfflineFolderEntry>());

        var (firstStatus, _) = DashboardApi.ImportOfflineFolder(config, session, catalog, package, ExportSecret);
        Assert.Equal(200, firstStatus);
        var (dupStatus, dupPayload) = DashboardApi.ImportOfflineFolder(config, session, catalog, package, ExportSecret);
        Assert.Equal(400, dupStatus);
        Assert.Contains("already exists", ErrorOf(dupPayload));
        Assert.Single(session.GetEntries()!);

        var (wrongStatus, _) = DashboardApi.ImportOfflineFolder(config, session, catalog, package, "wrong");
        Assert.Equal(400, wrongStatus);
        Assert.Single(session.GetEntries()!);
    }

    [Fact]
    public void Handler_Export_RequiresUnlock_AndKnownLocation()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        var locked = new OfflineFoldersSession();
        var (lockedStatus, _) = DashboardApi.ExportOfflineFolder(config, locked, "F", ExportSecret);
        Assert.Equal(403, lockedStatus);

        var orphan = new OfflineFolderEntry("Orphan", "Gone", DateTimeOffset.UtcNow);
        var session = UnlockedSession(new List<OfflineFolderEntry> { orphan });
        var (goneStatus, gonePayload) = DashboardApi.ExportOfflineFolder(config, session, "Orphan", ExportSecret);
        Assert.Equal(400, goneStatus);
        Assert.Contains("no longer exists", ErrorOf(gonePayload));
    }

    [Fact]
    public void Handler_Forget_RemovesEntry_ReportsJobs()
    {
        var config = new ServiceConfig();
        config.Locations.Add(SmbLocation());
        config.Jobs.Add(new BackupJobConfig { Name = "daily", Targets = new List<string> { "NAS" } });
        config.Jobs.Add(new BackupJobConfig { Name = "other", Targets = new List<string> { "Elsewhere" } });
        string catalog = CatalogPath();
        var session = UnlockedSession(new List<OfflineFolderEntry> { FolderEntry() });

        // Without confirmation: refused, entry intact.
        var (unconfirmedStatus, _) = DashboardApi.ForgetOfflineFolder(config, session, catalog, "F", confirm: false);
        Assert.Equal(400, unconfirmedStatus);
        Assert.Single(session.GetEntries()!);

        var (status, payload) = DashboardApi.ForgetOfflineFolder(config, session, catalog, "F", confirm: true);
        Assert.Equal(200, status);
        var summary = Assert.IsType<OfflineForgetSummaryDto>(payload);
        Assert.Equal("F", summary.Folder);
        Assert.Equal(2, summary.Files);
        Assert.Equal(3, summary.Chunks);
        Assert.Equal(new[] { "daily" }, summary.JobsUsingLocation);
        Assert.Empty(session.GetEntries()!);
        Assert.Empty(OfflineFoldersCatalog.Load(catalog, OfflineFoldersCatalog.DeriveKey(CardSecret)));

        // Locked: refused.
        var locked = new OfflineFoldersSession();
        var (lockedStatus, _) = DashboardApi.ForgetOfflineFolder(config, locked, catalog, "F", confirm: true);
        Assert.Equal(403, lockedStatus);
    }
}
