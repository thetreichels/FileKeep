using System.Text;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Guards the trimmed shipped build for the multipart upload response:
/// POST /api/offline-folders/{folder}/files forwards StoreOfflineFilesBatch's
/// payload to Results.Json with the HttpJsonOptions chain Dashboard.cs
/// configures (DashboardJsonContext, then FileKeepJsonContext) and no
/// reflection fallback. A response DTO missing from those contexts 500s
/// in the trimmed build while compiling and passing plain-reflection
/// tests — the exact WALK-9 failure mode. So this test serializes the
/// actual endpoint response through ProductionJson.Options, the same
/// resolver chain and Web defaults, where a missing registration throws
/// the same way in this sandbox. Deliberately NOT a Kestrel test:
/// DashboardApi is transport-free by design (loopback TCP from a test
/// process is blocked in this sandbox), and the route handler forwards
/// this payload to Results.Json unchanged.
/// </summary>
public sealed class OfflineBatchResponseJsonTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string Pass = "the-current-passphrase";

    public OfflineBatchResponseJsonTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-off-batch-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private static (ServiceConfig Config, OfflineFoldersSession Session, string Catalog) UnlockedFolder(
        string catalogPath, BackupLocation location, string folderName = "Vault")
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        config.Locations.Add(location);
        var session = new OfflineFoldersSession();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, catalogPath, Pass, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, catalogPath, folderName, location.Name).Status);
        return (config, session, catalogPath);
    }

    private static MemoryStream TextStream(string text) =>
        new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void BatchResponse_SerializesThroughProductionJsonOptions()
    {
        string shareDir = Path.Combine(_workDir, "share");
        Directory.CreateDirectory(shareDir);
        var location = new BackupLocation { Name = "Share", Type = "smb", Share = shareDir };
        string catalog = Path.Combine(_workDir, "offline-folders.json");
        var (config, session, _) = UnlockedFolder(catalog, location);

        // One stored file and one per-file failure, so both lists in the
        // response are non-empty and both element types must resolve.
        var entries = new List<(string, Stream, DateTimeOffset)>
        {
            ("good.txt", TextStream("good"), DateTimeOffset.UtcNow),
            ("bad|name.txt", TextStream("bad"), DateTimeOffset.UtcNow),
        };
        var (status, payload) = DashboardApi.StoreOfflineFilesBatch(config, session, catalog, "Vault", entries, null);
        Assert.Equal(200, status);
        var result = Assert.IsAssignableFrom<OfflineBatchResultDto>(payload);
        Assert.Single(result.Stored);
        Assert.Single(result.Failed);

        // The actual endpoint response, serialized the way Results.Json
        // serializes it in production: runtime type, production options.
        string json = JsonSerializer.Serialize(payload, payload.GetType(), ProductionJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("good.txt", root.GetProperty("stored")[0].GetProperty("path").GetString());
        Assert.Equal("bad|name.txt", root.GetProperty("failed")[0].GetProperty("path").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("failed")[0].GetProperty("error").GetString()));

        // Round-trip proves the read metadata is registered too.
        var roundTrip = JsonSerializer.Deserialize<OfflineBatchResultDto>(json, ProductionJson.Options);
        Assert.NotNull(roundTrip);
        Assert.Single(roundTrip.Stored);
        Assert.Single(roundTrip.Failed);
    }
}
