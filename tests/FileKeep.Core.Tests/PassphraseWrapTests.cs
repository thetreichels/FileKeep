using FileKeep.Core;
using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Add-a-security-method, slice 1: adding a passphrase to a
/// YubiKey-only (generated-secret) profile. The passphrase wraps the
/// generated secret — it never replaces it — so the YubiKey touch
/// and the recovery-sheet secret keep working, and the added
/// passphrase unlocks restores and proves security-config changes
/// exactly like any other method. Removing it is the profile
/// toggle off, which clears the wrap.
///
/// Fixture note: the "current secret" resolves through the
/// FILEKEEP_PASSPHRASE env var, which BackupScheduler.GetPassphrase
/// checks first — the same resolution the production wrap step
/// uses. No DPAPI is involved in these tests.
/// </summary>
public sealed class PassphraseWrapTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string AddedPass = "the-added-passphrase";

    public PassphraseWrapTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string ConfigPath() => Path.Combine(_workDir, "service-" + Guid.NewGuid().ToString("N") + ".json");

    private static string ErrorMessage((int Status, object Payload) result) =>
        result.Payload is ErrorDto error ? error.Error : result.Payload?.ToString() ?? "";

    private static ServiceConfig GeneratedConfig(string yubiKeyWrapped = "yk-wrapped-sentinel")
    {
        return new ServiceConfig
        {
            GeneratedSecretMode = true,
            YubiKeyChallenge = "yk-challenge-sentinel",
            PassphraseYubiKeyWrapped = yubiKeyWrapped,
            PassphraseProtected = "dpapi-copy-sentinel",
        };
    }

    [Fact]
    public void AddWrap_Succeeds_AndUnlocksRestores()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        var session = new RestoreSession();

        var (status, _) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null, proofPassphrase: secret);
        Assert.Equal(200, status);
        Assert.False(string.IsNullOrEmpty(config.PassphraseWrapProtected));
        Assert.False(string.IsNullOrEmpty(config.PassphraseWrapSaltB64));
        Assert.Equal(BackupRepository.DefaultKdfIterations, config.PassphraseWrapIterations);
        // The wrap touches nothing else: YubiKey blob and DPAPI copy intact.
        Assert.Equal("yk-wrapped-sentinel", config.PassphraseYubiKeyWrapped);
        Assert.Equal("dpapi-copy-sentinel", config.PassphraseProtected);
        // The root secret is unchanged — the wrap wraps it, it does not replace it.
        Assert.Equal(secret, BackupScheduler.GetPassphrase(config));

        var profile = DashboardApi.GetProtectionProfile(config);
        Assert.True(profile.PassphraseEnabled);
        Assert.True(profile.YubiKeyEnabled);

        // The added passphrase unlocks a restore session with the ROOT secret.
        var (unlockStatus, _) = DashboardApi.UnlockRestoreSessionWithPassphrase(config, session, AddedPass);
        Assert.Equal(200, unlockStatus);
        Assert.True(session.IsUnlocked);
        Assert.Equal(secret, DashboardApi.GetRestorePassphrase(config, session));

        // A wrong added passphrase does not unlock.
        var locked = new RestoreSession();
        var (wrongStatus, wrongPayload) = DashboardApi.UnlockRestoreSessionWithPassphrase(config, locked, "not-the-added-passphrase");
        Assert.Equal(400, wrongStatus);
        Assert.Contains("added passphrase", ErrorMessage((wrongStatus, wrongPayload)));
        Assert.False(locked.IsUnlocked);
    }

    [Fact]
    public void AddWrap_RefusesWithoutProof()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();

        var (status, _) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null);
        Assert.Equal(403, status);
        Assert.Null(config.PassphraseWrapProtected);
    }

    [Fact]
    public void AddWrap_RefusesWhenNotGeneratedMode()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "the-current-passphrase");
        var config = new ServiceConfig { PassphraseProtected = "dpapi-copy-sentinel" };

        var (status, payload) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null, proofPassphrase: "the-current-passphrase");
        Assert.Equal(400, status);
        Assert.Contains("YubiKey-only mode", ErrorMessage((status, payload)));
        Assert.Null(config.PassphraseWrapProtected);
    }

    [Fact]
    public void AddWrap_RefusesWhenAlreadyAdded()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();

        var (first, _) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null, proofPassphrase: secret);
        Assert.Equal(200, first);
        var (second, payload) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), "a-different-passphrase",
            yubiKeyProvider: null, proofPassphrase: secret);
        Assert.Equal(400, second);
        Assert.Contains("already added", ErrorMessage((second, payload)));
    }

    [Fact]
    public void AddWrap_RefusesWhenNoResolvableSecret()
    {
        // A real YubiKey setup (simulated key) so the touch proof
        // passes the gate — but no env value and no stored copy, so
        // there is no root secret for the wrap to wrap.
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped, slotSecret) = YubiKeySetup.Setup(provider, "repo-pass", slot: 2);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(slotSecret);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        var config = new ServiceConfig
        {
            GeneratedSecretMode = true,
            YubiKeyChallenge = challenge,
            PassphraseYubiKeyWrapped = wrapped,
        };

        var (status, payload) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: provider, proofYubiKey: true);
        Assert.Equal(400, status);
        Assert.Contains("repository secret", ErrorMessage((status, payload)));
        Assert.Null(config.PassphraseWrapProtected);
    }

    [Fact]
    public void AddedPassphrase_ProvesSecurityConfigChanges()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        var (added, _) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null, proofPassphrase: secret);
        Assert.Equal(200, added);

        // The added passphrase alone is a valid gate proof.
        Assert.Null(DashboardApi.CheckSecurityChangeProof(config, null, AddedPass, proofYubiKey: false));
        // A wrong one is not.
        Assert.NotNull(DashboardApi.CheckSecurityChangeProof(config, null, "wrong-added-pass", proofYubiKey: false));
    }

    [Fact]
    public void ProfileToggle_MapsToWrap()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        var (added, _) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null, proofPassphrase: secret);
        Assert.Equal(200, added);

        // Toggle on with a wrap present: keeps it.
        var on = new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = true, PhoneEnabled = false,
            LinkMode = "or", ProofPassphrase = secret,
        };
        var (onStatus, _) = DashboardApi.SetProtectionProfile(config, ConfigPath(), on);
        Assert.Equal(200, onStatus);
        Assert.False(string.IsNullOrEmpty(config.PassphraseWrapProtected));

        // Toggle off: clears the wrap, and the profile reports it gone.
        var off = new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = true, PhoneEnabled = false,
            LinkMode = "or", ProofPassphrase = secret,
        };
        var (offStatus, _) = DashboardApi.SetProtectionProfile(config, ConfigPath(), off);
        Assert.Equal(200, offStatus);
        Assert.Null(config.PassphraseWrapProtected);
        Assert.Null(config.PassphraseWrapSaltB64);
        Assert.Equal(0, config.PassphraseWrapIterations);
        Assert.False(DashboardApi.GetProtectionProfile(config).PassphraseEnabled);

        // Toggle on with no wrap: refused — add first.
        var (onAgain, payload) = DashboardApi.SetProtectionProfile(config, ConfigPath(), on);
        Assert.Equal(400, onAgain);
        Assert.Contains("Add a passphrase first", ErrorMessage((onAgain, payload)));
    }

    [Fact]
    public void StatusReportsWrap()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        var session = new RestoreSession();

        // No wrap yet: the restore view must not offer a typed row.
        var (_, bare) = DashboardApi.GetRestoreSessionStatus(config, session);
        var bareDto = Assert.IsType<YubiKeyUnlockDto>(bare);
        Assert.False(bareDto.PassphraseEnabled);
        Assert.False(bareDto.PassphraseWrap);

        var (added, _) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null, proofPassphrase: secret);
        Assert.Equal(200, added);
        var (_, wrapped) = DashboardApi.GetRestoreSessionStatus(config, session);
        var wrappedDto = Assert.IsType<YubiKeyUnlockDto>(wrapped);
        Assert.True(wrappedDto.PassphraseEnabled);
        Assert.True(wrappedDto.PassphraseWrap);
    }

    [Fact]
    public void CardUnlock_AcceptsAddedPassphrase()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        var (added, _) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null, proofPassphrase: secret);
        Assert.Equal(200, added);

        // A catalog created under the root secret.
        string catalog = Path.Combine(_workDir, "offline-" + Guid.NewGuid().ToString("N") + ".json");
        OfflineFoldersCatalog.Save(catalog, OfflineFoldersCatalog.DeriveKey(secret),
            new List<OfflineFolderEntry> { new("Vault", "nas", DateTimeOffset.UtcNow) });

        // The added passphrase unlocks the card: it unwraps to the root.
        var cardSession = new OfflineFoldersSession();
        var (status, _) = DashboardApi.UnlockOfflineFolders(config, cardSession, catalog,
            proofPassphrase: AddedPass, proofYubiKey: false, yubiKeyProvider: null);
        Assert.Equal(200, status);
        Assert.True(cardSession.IsUnlocked);
        Assert.Single(cardSession.GetEntries()!);

        // A wrong added passphrase proves nothing.
        var wrongSession = new OfflineFoldersSession();
        var (wrongStatus, _) = DashboardApi.UnlockOfflineFolders(config, wrongSession, catalog,
            proofPassphrase: "not-the-added-passphrase", proofYubiKey: false, yubiKeyProvider: null);
        Assert.Equal(403, wrongStatus);
        Assert.False(wrongSession.IsUnlocked);

        // The sheet secret still unlocks (the wrap changed nothing else).
        var sheetSession = new OfflineFoldersSession();
        var (sheetStatus, _) = DashboardApi.UnlockOfflineFolders(config, sheetSession, catalog,
            proofPassphrase: secret, proofYubiKey: false, yubiKeyProvider: null);
        Assert.Equal(200, sheetStatus);
        Assert.True(sheetSession.IsUnlocked);
    }

    [Fact]
    public void TypedPassphraseUnlock_RefusesWithoutWrap()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        var session = new RestoreSession();

        // YubiKey-only with no added passphrase: no passphrase to type.
        var (status, payload) = DashboardApi.UnlockRestoreSessionWithPassphrase(config, session, "anything");
        Assert.Equal(400, status);
        Assert.Contains("added passphrase", ErrorMessage((status, payload)));
        Assert.False(session.IsUnlocked);
    }
}
