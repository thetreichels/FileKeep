using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>Tests for BackupDiscovery (passphrase-free manifest scans).</summary>
public sealed class DiscoveryTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "fk-discovery-" + Guid.NewGuid().ToString("N"));

    public DiscoveryTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public void ScanPath_CreatedUtc_ParsesAsUtcNotLocal()
    {
        // P2-D7: created_utc is stored in UTC ("...Z"); the scanner used
        // plain DateTime.TryParse, producing a LOCAL-Kind value that
        // consumers labeled UTC — discovered backups showed times
        // shifted by the local offset.
        string id = new('a', 32);
        string manifests = Path.Combine(_workDir, "manifests");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, id + ".json"),
            """{"type":"full","created_utc":"2026-03-04T05:06:07Z","source":"C:\\data"}""");

        var found = BackupDiscovery.ScanPath(_workDir);
        var backup = Assert.Single(found);
        Assert.NotNull(backup.CreatedUtc);
        Assert.Equal(DateTimeKind.Utc, backup.CreatedUtc!.Value.Kind);
        Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            backup.CreatedUtc.Value);
    }
}
