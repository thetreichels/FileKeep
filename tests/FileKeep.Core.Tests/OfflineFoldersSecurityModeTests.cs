// O2: `securityMode` is required on catalog load. A catalog entry
// without a usable value (absent, non-string, or unknown) fails the
// whole load with MissingSecurityModeException, naming the entry.
// v1 catalogs carry no `securityMode` on any entry, so every v1
// catalog fails to load — decided, no migration path. The endpoint
// tests pin the precise 400 (not the decrypt-failure message) at
// unlock, rekey, and the passphrase-change probe.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

public sealed class OfflineFoldersSecurityModeTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string Secret = "the-vault-secret";
    private const string NewSecret = "the-new-vault-secret";

    public OfflineFoldersSecurityModeTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-of-sm-" + Guid.NewGuid().ToString("N"));
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

    private static string EntryJson(string name, string locationId, string? securityModeRaw, bool includeFiles = true)
    {
        var parts = new List<string>
        {
            $"\"name\":\"{name}\"",
            $"\"locationId\":\"{locationId}\"",
            "\"addedUtc\":\"2026-01-01T00:00:00Z\"",
        };
        if (securityModeRaw is not null)
            parts.Add($"\"securityMode\":{securityModeRaw}");
        if (includeFiles)
            parts.Add("\"files\":[]");
        return "{" + string.Join(",", parts) + "}";
    }

    private static string CatalogJson(int version, params string[] entries) =>
        $"{{\"version\":{version},\"entries\":[{string.Join(",", entries)}]}}";

    /// <summary>Encrypts hand-crafted plaintext catalog JSON exactly the way Save does (AES-GCM, nonce ‖ cipher ‖ tag, base64).</summary>
    private static void WriteCatalog(string path, string secret, string plaintextJson)
    {
        byte[] key = OfflineFoldersCatalog.DeriveKey(secret);
        try
        {
            byte[] plain = Encoding.UTF8.GetBytes(plaintextJson);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16];
            using (var gcm = new AesGcm(key, 16))
                gcm.Encrypt(nonce, plain, cipher, tag);
            byte[] blob = nonce.Concat(cipher).Concat(tag).ToArray();
            File.WriteAllText(path, Convert.ToBase64String(blob));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static MissingSecurityModeException LoadThrows(string path, string secret)
    {
        byte[] key = OfflineFoldersCatalog.DeriveKey(secret);
        try
        {
            return Assert.Throws<MissingSecurityModeException>(() => OfflineFoldersCatalog.Load(path, key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static List<OfflineFolderEntry> LoadOk(string path, string secret)
    {
        byte[] key = OfflineFoldersCatalog.DeriveKey(secret);
        try
        {
            return OfflineFoldersCatalog.Load(path, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static ServiceConfig PassphraseConfig()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Secret);
        return new ServiceConfig { PassphraseProtected = "dpapi-blob" };
    }

    [Fact]
    public void Load_MissingSecurityMode_ThrowsNamingEntry()
    {
        string path = CatalogPath();
        WriteCatalog(path, Secret, CatalogJson(2, EntryJson("Vault", "nas", null)));

        var ex = LoadThrows(path, Secret);

        Assert.Equal("Vault", ex.FolderName);
        Assert.Equal("nas", ex.LocationId);
        Assert.Equal("missing", ex.Reason);
        Assert.Contains("Vault", ex.Message);
        Assert.Contains("nas", ex.Message);
        Assert.Contains("no usable security mode", ex.Message);
        Assert.Contains("Delete and re-create the folder", ex.Message);
        // Distinct type in the IO load-failure family (IOException base —
        // InvalidDataException is sealed), not an InvalidDataException.
        Assert.IsAssignableFrom<IOException>(ex);
        Assert.IsNotType<InvalidDataException>(ex);
    }

    [Fact]
    public void Load_MissingSecurityMode_OneBadEntryFailsWholeLoad()
    {
        string path = CatalogPath();
        WriteCatalog(path, Secret, CatalogJson(2,
            EntryJson("Good", "nas", "\"Global\""),
            EntryJson("Stale", "usb", null)));

        var ex = LoadThrows(path, Secret);

        Assert.Equal("Stale", ex.FolderName);
        Assert.Equal("usb", ex.LocationId);
    }

    [Fact]
    public void Load_NonStringSecurityMode_Throws()
    {
        string path = CatalogPath();
        WriteCatalog(path, Secret, CatalogJson(2, EntryJson("Vault", "nas", "42")));

        var ex = LoadThrows(path, Secret);

        Assert.Equal("Vault", ex.FolderName);
        Assert.Equal("not a string", ex.Reason);
    }

    [Fact]
    public void Load_UnknownSecurityMode_Throws()
    {
        string path = CatalogPath();
        WriteCatalog(path, Secret, CatalogJson(2, EntryJson("Vault", "nas", "\"Bogus\"")));

        var ex = LoadThrows(path, Secret);

        Assert.Equal("Vault", ex.FolderName);
        Assert.Contains("Bogus", ex.Reason);
    }

    [Fact]
    public void Load_V1Catalog_ThrowsMissingSecurityMode()
    {
        // Decided blast radius: v1 catalogs carry no securityMode on
        // any entry, so every v1 catalog fails to load.
        string path = CatalogPath();
        WriteCatalog(path, Secret, CatalogJson(1, EntryJson("Old", "nas", null, includeFiles: false)));

        var ex = LoadThrows(path, Secret);

        Assert.Equal("Old", ex.FolderName);
        Assert.Equal("missing", ex.Reason);
    }

    [Fact]
    public void Load_GlobalAndCustomSecurityMode_LoadFine()
    {
        string path = CatalogPath();
        WriteCatalog(path, Secret, CatalogJson(2,
            EntryJson("GlobalFolder", "nas", "\"Global\""),
            EntryJson("CustomFolder", "usb", "\"Custom\"")));

        var loaded = LoadOk(path, Secret);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(FolderSecurityMode.Global, loaded[0].SecurityMode);
        Assert.Equal(FolderSecurityMode.Custom, loaded[1].SecurityMode);
    }

    [Fact]
    public void Unlock_MissingSecurityMode_Returns400WithPreciseMessage()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string path = CatalogPath();
        WriteCatalog(path, Secret, CatalogJson(2, EntryJson("Vault", "nas", null)));

        var (status, payload) = DashboardApi.UnlockOfflineFolders(
            config, session, path, Secret, false, null);

        Assert.Equal(400, status);
        string error = Assert.IsType<ErrorDto>(payload).Error;
        Assert.Contains("no usable security mode", error);
        Assert.Contains("Vault", error);
        Assert.DoesNotContain("cannot be decrypted with this vault secret", error);
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void Create_MissingSecurityMode_Returns400WithPreciseMessage()
    {
        // O2 follow-up: the global create-via-proof path catches
        // MissingSecurityModeException first (400), not the generic
        // decrypt-failure 403 — a securityMode-less catalog must
        // surface the precise message at create time too.
        var config = PassphraseConfig();
        config.Locations.Add(new BackupLocation { Name = "nas", Type = "smb", Share = Path.Combine(_workDir, "share") });
        var session = new OfflineFoldersSession();
        string path = CatalogPath();
        WriteCatalog(path, Secret, CatalogJson(2, EntryJson("Vault", "nas", null)));

        using var doc = JsonDocument.Parse($"{{\"proofPassphrase\":\"{Secret}\"}}");
        var (status, payload) = DashboardApi.CreateOfflineFolderWithProof(
            config, session, path, _workDir, "NewFolder", "nas", "global",
            doc.RootElement, yubiKeyProvider: null);

        Assert.Equal(400, status);
        string error = Assert.IsType<ErrorDto>(payload).Error;
        Assert.Contains("no usable security mode", error);
        Assert.Contains("Vault", error);
        Assert.DoesNotContain("Proof did not unlock", error);
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void RekeyHandler_MissingSecurityMode_Returns400WithPreciseMessage()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Secret);
        try
        {
            var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
            var session = new OfflineFoldersSession();
            string configPath = Path.Combine(_workDir, "service.json");
            WriteCatalog(CatalogPath(), Secret, CatalogJson(2, EntryJson("Vault", "nas", null)));

            var (status, payload) = DashboardApi.RekeyOfflineFolders(
                config, configPath, session,
                oldSecret: null, newSecret: NewSecret, yubiKeyProvider: null,
                proofPassphrase: Secret);

            Assert.Equal(400, status);
            string error = Assert.IsType<ErrorDto>(payload).Error;
            Assert.Contains("no usable security mode", error);
            Assert.Contains("Vault", error);
            Assert.DoesNotContain("could not be decrypted with the current secret", error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        }
    }

    [Fact]
    public void PassphraseChangeProbe_MissingSecurityModeUnderNewSecret_AbortsWith400()
    {
        var config = new ServiceConfig();
        var session = new OfflineFoldersSession();
        string path = CatalogPath();
        // Already under the NEW secret, but v1-shaped: the probe Load
        // decrypts fine and must abort here, never falling through to
        // a rewrite (the throwing resolver proves no fall-through).
        WriteCatalog(path, NewSecret, CatalogJson(2, EntryJson("Vault", "nas", null)));

        var result = DashboardApi.RekeyOfflineForPassphraseChange(
            config, session, path, NewSecret,
            _ => throw new InvalidOperationException("must not fall through to rewrite"));

        Assert.NotNull(result);
        Assert.Equal(400, result.Value.Status);
        string error = Assert.IsType<ErrorDto>(result.Value.Payload).Error;
        Assert.Contains("no usable security mode", error);
        Assert.Contains("Vault", error);
        Assert.Contains("The passphrase was NOT changed.", error);
    }

    [Fact]
    public void PassphraseChangeProbe_MissingSecurityModeUnderOldSecret_Returns400WithPreciseMessage()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Secret);
        try
        {
            var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
            var session = new OfflineFoldersSession();
            string path = CatalogPath();
            // Under the OLD secret: the probe Load sees only a decrypt
            // failure under the new key, falls through, and the rewrite's
            // Load throws — the rewrite catch must surface it precisely.
            WriteCatalog(path, Secret, CatalogJson(2, EntryJson("Vault", "nas", null)));

            var result = DashboardApi.RekeyOfflineForPassphraseChange(
                config, session, path, NewSecret,
                _ => throw new InvalidOperationException("must not be called"));

            Assert.NotNull(result);
            Assert.Equal(400, result.Value.Status);
            string error = Assert.IsType<ErrorDto>(result.Value.Payload).Error;
            Assert.Contains("no usable security mode", error);
            Assert.Contains("Vault", error);
            Assert.DoesNotContain("could not be decrypted with the current secret", error);
            Assert.Contains("The passphrase was NOT changed.", error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        }
    }
}
