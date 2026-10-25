using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Offline Folders sidebar card, slice 1 (locked card + encrypted
/// catalog). The memory-served open is the next slice and is not
/// tested here — these tests pin the lock, the ciphertext-only
/// catalog, the proof rules, and the session separation from
/// restores.
///
/// Fixture note (same as SecurityProofTests): test configs carry a
/// fake DPAPI blob, so the "current secret" resolves through the
/// FILEKEEP_PASSPHRASE env var, which GetPassphrase checks first.
/// </summary>
public sealed class OfflineFoldersTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string Pass = "the-current-passphrase";

    public OfflineFoldersTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-of-" + Guid.NewGuid().ToString("N"));
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

    private static ServiceConfig PassphraseConfig()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        config.Locations.Add(new BackupLocation { Name = "nas", Type = "smb" });
        return config;
    }

    private static string ErrorMessage((int Status, object Payload) result)
    {
        var prop = result.Payload.GetType().GetProperty("Error")
            ?? result.Payload.GetType().GetProperty("error");
        Assert.NotNull(prop);
        return (string?)prop.GetValue(result.Payload) ?? "";
    }

    private static List<OfflineFolderDto> FoldersPayload((int Status, object Payload) result)
    {
        Assert.Equal(200, result.Status);
        return Assert.IsAssignableFrom<List<OfflineFolderDto>>(result.Payload);
    }

    [Fact]
    public void LockedStatus_RevealsNothing()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        var (status, payload) = DashboardApi.UnlockOfflineFolders(
            config, session, CatalogPath(), Pass, false, null);
        Assert.Equal(200, status);
        var add = DashboardApi.AddOfflineFolder(config, session, CatalogPath(), "SecretFolder", "nas");
        Assert.Equal(200, add.Status);
        var add2 = DashboardApi.AddOfflineFolder(config, session, CatalogPath(), "AnotherOne", "nas");
        Assert.Equal(200, add2.Status);
        Assert.Equal(200, DashboardApi.LockOfflineFolders(session).Status);

        var (sStatus, sPayload) = DashboardApi.GetOfflineFoldersStatus(session);
        Assert.Equal(200, sStatus);
        var dto = Assert.IsType<OfflineFoldersStatusDto>(sPayload);
        Assert.True(dto.Locked);
        // The locked wire shape is exactly one boolean — no names,
        // no counts, no hint whether a catalog file even exists.
        string json = JsonSerializer.Serialize(sPayload);
        Assert.Equal("{\"locked\":true}", json);
        Assert.DoesNotContain("SecretFolder", json);
        Assert.DoesNotContain("AnotherOne", json);
    }

    [Fact]
    public void CatalogFile_ContainsNoPlaintextNames()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string path = CatalogPath();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, path, Pass, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, path, "SecretFolder", "nas").Status);

        string fileText = File.ReadAllText(path);
        Assert.DoesNotContain("SecretFolder", fileText);
        Assert.DoesNotContain("nas", fileText);
        Assert.DoesNotContain("entries", fileText);
        // The whole file is one base64 ciphertext blob — no envelope.
        byte[] blob = Convert.FromBase64String(fileText.Trim());
        Assert.True(blob.Length > 12 + 16);
    }

    [Fact]
    public void Unlock_NoMethodEnabled_Refused()
    {
        var config = new ServiceConfig();
        var session = new OfflineFoldersSession();
        var (status, _) = DashboardApi.UnlockOfflineFolders(
            config, session, CatalogPath(), "anything", false, null);
        Assert.Equal(400, status);
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void Unlock_WrongPassphrase_Refused()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        var (status, _) = DashboardApi.UnlockOfflineFolders(
            config, session, CatalogPath(), "wrong-passphrase", false, null);
        Assert.Equal(403, status);
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void Unlock_NoProof_Refused()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        var (status, _) = DashboardApi.UnlockOfflineFolders(
            config, session, CatalogPath(), null, false, null);
        Assert.Equal(403, status);
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void Unlock_WrongTouch_Refused()
    {
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped, _) = YubiKeySetup.Setup(provider, Pass, slot: 2);
        var config = new ServiceConfig
        {
            PassphraseYubiKey = true,
            YubiKeyChallenge = challenge,
            PassphraseYubiKeyWrapped = wrapped,
            YubiKeySlot = 2,
        };
        var stranger = new SimulatedYubiKeyProvider();
        var session = new OfflineFoldersSession();
        var (status, _) = DashboardApi.UnlockOfflineFolders(
            config, session, CatalogPath(), null, true, stranger);
        Assert.Equal(403, status);
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void Unlock_ViaPassphrase_RoundTrip()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string path = CatalogPath();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, path, Pass, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, path, "Photos", "nas").Status);
        var folders = FoldersPayload(DashboardApi.GetOfflineFolders(session));
        Assert.Single(folders);
        Assert.Equal("Photos", folders[0].Name);
        Assert.Equal("nas", folders[0].LocationId);

        // Lock refuses everything, then a fresh unlock re-reads from disk.
        Assert.Equal(200, DashboardApi.LockOfflineFolders(session).Status);
        Assert.Equal(403, DashboardApi.GetOfflineFolders(session).Status);
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, path, Pass, false, null).Status);
        folders = FoldersPayload(DashboardApi.GetOfflineFolders(session));
        Assert.Single(folders);
        Assert.Equal("Photos", folders[0].Name);

        // Delete round-trips too.
        Assert.Equal(200, DashboardApi.DeleteOfflineFolder(session, path, "Photos").Status);
        Assert.Empty(FoldersPayload(DashboardApi.GetOfflineFolders(session)));
        Assert.Equal(404, DashboardApi.DeleteOfflineFolder(session, path, "Photos").Status);
    }

    [Fact]
    public void Unlock_ViaTypedSecret_DisplayForm()
    {
        string canonical = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, canonical);
        var config = new ServiceConfig
        {
            GeneratedSecretMode = true,
            PassphraseYubiKey = true,
            PassphraseProtected = "dpapi-blob",
        };
        config.Locations.Add(new BackupLocation { Name = "stick", Type = "usb" });
        var session = new OfflineFoldersSession();
        // The proof is the recovery-sheet form (grouped in 4s); the
        // card normalizes it exactly like the typed-secret unlock.
        var (status, _) = DashboardApi.UnlockOfflineFolders(config, session, CatalogPath(),
            GeneratedSecret.FormatForDisplay(canonical), false, null);
        Assert.Equal(200, status);
        Assert.True(session.IsUnlocked);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, CatalogPath(), "Vault", "stick").Status);
    }

    [Fact]
    public void Unlock_ViaTouch_Succeeds()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped, _) = YubiKeySetup.Setup(provider, Pass, slot: 2);
        var config = new ServiceConfig
        {
            PassphraseYubiKey = true,
            YubiKeyChallenge = challenge,
            PassphraseYubiKeyWrapped = wrapped,
            YubiKeySlot = 2,
        };
        var session = new OfflineFoldersSession();
        var (status, _) = DashboardApi.UnlockOfflineFolders(
            config, session, CatalogPath(), null, true, provider);
        Assert.Equal(200, status);
        Assert.True(session.IsUnlocked);
    }

    [Fact]
    public void Unlock_CatalogUnderDifferentSecret_Refused()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string path = CatalogPath();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, path, Pass, false, null).Status);
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, path, "Photos", "nas").Status);
        Assert.Equal(200, DashboardApi.LockOfflineFolders(session).Status);

        // The vault secret changed: the proof still validates against
        // the new secret, but the old catalog no longer decrypts.
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "the-new-passphrase");
        var (status, payload) = DashboardApi.UnlockOfflineFolders(
            config, session, path, "the-new-passphrase", false, null);
        Assert.Equal(400, status);
        Assert.Contains("different secret", ErrorMessage((status, payload)));
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void Add_Validation()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        string path = CatalogPath();
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, path, Pass, false, null).Status);

        Assert.Equal(400, DashboardApi.AddOfflineFolder(config, session, path, "X", "no-such-location").Status);
        Assert.Equal(400, DashboardApi.AddOfflineFolder(config, session, path, "", "nas").Status);
        Assert.Equal(400, DashboardApi.AddOfflineFolder(config, session, path, "   ", "nas").Status);
        Assert.Equal(400, DashboardApi.AddOfflineFolder(config, session, path, new string('x', 129), "nas").Status);
        Assert.Equal(400, DashboardApi.AddOfflineFolder(config, session, path, "bad\nname", "nas").Status);
        Assert.Equal(400, DashboardApi.AddOfflineFolder(config, session, path, "a/b", "nas").Status);

        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, path, "Photos", "nas").Status);
        // Duplicate, case-insensitively, is refused.
        Assert.Equal(400, DashboardApi.AddOfflineFolder(config, session, path, "photos", "nas").Status);
        // Location lookup is case-insensitive too (same as job targets).
        Assert.Equal(200, DashboardApi.AddOfflineFolder(config, session, path, "Docs", "NAS").Status);
        Assert.Equal(2, FoldersPayload(DashboardApi.GetOfflineFolders(session)).Count);
    }

    [Fact]
    public void LockedFolders_Refused()
    {
        var config = PassphraseConfig();
        var session = new OfflineFoldersSession();
        Assert.Equal(403, DashboardApi.GetOfflineFolders(session).Status);
        Assert.Equal(403, DashboardApi.AddOfflineFolder(config, session, CatalogPath(), "X", "nas").Status);
        Assert.Equal(403, DashboardApi.DeleteOfflineFolder(session, CatalogPath(), "X").Status);
    }

    [Fact]
    public void Session_ExpiresAfterIdleTimeout()
    {
        var old = OfflineFoldersSession.IdleTimeout;
        try
        {
            OfflineFoldersSession.IdleTimeout = TimeSpan.FromMilliseconds(50);
            var config = PassphraseConfig();
            var session = new OfflineFoldersSession();
            Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, session, CatalogPath(), Pass, false, null).Status);
            Assert.True(session.IsUnlocked);
            Thread.Sleep(120);
            Assert.False(session.IsUnlocked);
            Assert.Null(session.GetEntries());
            Assert.Equal(403, DashboardApi.GetOfflineFolders(session).Status);
        }
        finally { OfflineFoldersSession.IdleTimeout = old; }
    }

    [Fact]
    public void CardSession_IndependentFromRestoreSession()
    {
        var config = PassphraseConfig();
        var cardSession = new OfflineFoldersSession();
        var restoreSession = new RestoreSession();

        // Unlocking restores does not open the card.
        restoreSession.Unlock(Pass);
        Assert.True(restoreSession.IsUnlocked);
        Assert.False(cardSession.IsUnlocked);
        Assert.Equal(403, DashboardApi.GetOfflineFolders(cardSession).Status);

        // Unlocking the card does not open restores.
        Assert.Equal(200, DashboardApi.UnlockOfflineFolders(config, cardSession, CatalogPath(), Pass, false, null).Status);
        restoreSession.Lock();
        Assert.False(restoreSession.IsUnlocked);
        Assert.True(cardSession.IsUnlocked);
    }

    [Fact]
    public void CatalogPath_IsBesideConfig()
    {
        string dir = Path.Combine(_workDir, "cfg");
        Directory.CreateDirectory(dir);
        string configPath = Path.Combine(dir, "service.json");
        Assert.Equal(Path.Combine(dir, "offline-folders.json"),
            OfflineFoldersCatalog.CatalogPathFor(configPath));
    }
}
