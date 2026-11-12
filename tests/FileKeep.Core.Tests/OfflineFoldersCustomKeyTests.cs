using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// E-1: per-folder YubiKey creation is refused (501) until real
/// challenge-response wrapping exists, and legacy entries created by
/// the pre-fix placeholder (raw key in WrappedKeyB64) are flagged on
/// unlock instead of silently treated as wrapped.
/// </summary>
public sealed class OfflineFoldersCustomKeyTests : IDisposable
{
    private readonly string _workDir;

    public OfflineFoldersCustomKeyTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "fk-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private static ServiceConfig ConfigWithLocation(string name) => new()
    {
        Locations = new List<BackupLocation>
        {
            new() { Name = name, Type = "usb", VolumeSerial = "V-" + name },
        },
    };

    private static string ErrorMessage((int Status, object Payload) result)
    {
        Assert.NotEqual(200, result.Status);
        var prop = result.Payload.GetType().GetProperty("Error")
            ?? result.Payload.GetType().GetProperty("error");
        Assert.NotNull(prop);
        return (string?)prop.GetValue(result.Payload) ?? "";
    }

    private string CustomFolderPath(string folderName)
    {
        using var sha = SHA256.Create();
        string hex = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(folderName))).ToLowerInvariant();
        return Path.Combine(_workDir, $"offline-folders.{hex}.dat");
    }

    private void WriteCustomIndex(CustomFolderIndexEntry entry)
    {
        File.WriteAllText(Path.Combine(_workDir, "offline-folders.custom.json"),
            JsonSerializer.Serialize(new[] { entry }));
    }

    [Fact]
    public void CreateCustomFolder_YubiKey_Returns501AndWritesNothing()
    {
        var config = ConfigWithLocation("loc1");
        var session = new OfflineFoldersSession();
        string catalogPath = Path.Combine(_workDir, "offline-folders.json");
        using var doc = JsonDocument.Parse("""{"customKind":"yubikey"}""");
        var (status, payload) = DashboardApi.CreateOfflineFolderWithProof(
            config, session, catalogPath, _workDir, "vault", "loc1", "custom",
            doc.RootElement, yubiKeyProvider: null);
        Assert.Equal(501, status);
        Assert.Contains("not available", ErrorMessage((status, payload)), StringComparison.OrdinalIgnoreCase);
        // Refused before any file was written: no index, no folder data.
        Assert.False(File.Exists(Path.Combine(_workDir, "offline-folders.custom.json")));
        Assert.False(File.Exists(CustomFolderPath("vault")));
    }

    [Fact]
    public void CreateCustomFolder_Passphrase_StillWorks()
    {
        // Regression guard: refusing YubiKey must not break the other
        // custom-key branches, which share the create path.
        var config = ConfigWithLocation("loc1");
        var session = new OfflineFoldersSession();
        string catalogPath = Path.Combine(_workDir, "offline-folders.json");
        using var doc = JsonDocument.Parse(
            """{"customKind":"passphrase","newPassphrase":"correct horse battery staple"}""");
        var (status, _) = DashboardApi.CreateOfflineFolderWithProof(
            config, session, catalogPath, _workDir, "vault", "loc1", "custom",
            doc.RootElement, yubiKeyProvider: null);
        Assert.Equal(200, status);
        var index = DashboardApi.LoadCustomIndex(_workDir);
        var entry = Assert.Single(index);
        Assert.Equal("passphrase", entry.Kind);
        Assert.True(File.Exists(CustomFolderPath("vault")));
    }

    [Fact]
    public void UnlockCustomFolder_LegacyPlaintextYubiKey_Returns500()
    {
        // Entry exactly as the pre-fix placeholder wrote it: WrappedKeyB64
        // is the raw 32-byte folder key, base64-encoded — not a wrap.
        var keyInfo = new CustomFolderKeyInfo
        {
            Kind = "yubikey",
            ChallengeB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            WrappedKeyB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            YubiKeySlot = 2,
        };
        WriteCustomIndex(new CustomFolderIndexEntry(
            "legacy", "loc1", DateTimeOffset.UtcNow, "yubikey", keyInfo));
        // The .dat file must exist for the unlock path to reach the key check.
        File.WriteAllText(CustomFolderPath("legacy"), "placeholder");
        var session = new OfflineFoldersSession();
        using var doc = JsonDocument.Parse("""{"proofYubiKey":true}""");
        var (status, payload) = DashboardApi.UnlockCustomFolder(
            session, _workDir, "legacy", doc.RootElement, yubiKeyProvider: null);
        Assert.Equal(500, status);
        Assert.Contains("stored unprotected", ErrorMessage((status, payload)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnlockCustomFolder_YubiKeyWrappedShape_Returns501()
    {
        // A WrappedKeyB64 that is NOT the 32-byte placeholder shape is
        // treated as a (future) real wrap: the generic 501, not the
        // legacy-plaintext error.
        var keyInfo = new CustomFolderKeyInfo
        {
            Kind = "yubikey",
            ChallengeB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            WrappedKeyB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            YubiKeySlot = 2,
        };
        WriteCustomIndex(new CustomFolderIndexEntry(
            "wrapped", "loc1", DateTimeOffset.UtcNow, "yubikey", keyInfo));
        File.WriteAllText(CustomFolderPath("wrapped"), "placeholder");
        var session = new OfflineFoldersSession();
        using var doc = JsonDocument.Parse("""{"proofYubiKey":true}""");
        var (status, payload) = DashboardApi.UnlockCustomFolder(
            session, _workDir, "wrapped", doc.RootElement, new SimulatedYubiKeyProvider());
        Assert.Equal(501, status);
        Assert.Contains("not yet implemented", ErrorMessage((status, payload)), StringComparison.OrdinalIgnoreCase);
    }
}
