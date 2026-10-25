using FileKeep.Core;
using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Passphrase-free YubiKey-only mode: the generated-secret format,
/// generated-secret setup (DashboardApi.SetupGeneratedSecret), the
/// SetPassphrase guards, the protection-profile rules in generated
/// mode, typed-secret restore unlock, and scheduler secret resolution.
/// Success paths that persist through DPAPI are Windows-gated, like
/// the existing YubiKey tests; the YubiKey itself is the simulated
/// provider throughout.
/// </summary>
public sealed class GeneratedSecretModeTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    public GeneratedSecretModeTests()
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

    private static string ErrorMessage((int Status, object Payload) result)
    {
        Assert.NotEqual(200, result.Status);
        var prop = result.Payload.GetType().GetProperty("Error")
            ?? result.Payload.GetType().GetProperty("error");
        Assert.NotNull(prop);
        return (string?)prop.GetValue(result.Payload) ?? "";
    }

    /// <summary>A config as generated-secret setup leaves it: mode on, setup data present.</summary>
    private static ServiceConfig GeneratedModeConfig() => new()
    {
        GeneratedSecretMode = true,
        PassphraseYubiKey = true,
        PassphraseProtected = "dpapi-blob",
        YubiKeyChallenge = "challenge",
        PassphraseYubiKeyWrapped = "wrapped",
        YubiKeySlotSecretProtected = "slot-secret",
    };

    // ---------- GeneratedSecret format ----------

    [Fact]
    public void Generate_ProducesCanonicalForm()
    {
        string a = GeneratedSecret.Generate();
        string b = GeneratedSecret.Generate();
        Assert.NotEqual(a, b);
        Assert.Equal(GeneratedSecret.CanonicalLength, a.Length);
        Assert.All(a, c => Assert.True(
            c is (>= 'A' and <= 'Z') or (>= '2' and <= '7'),
            $"character '{c}' is not in the base32 alphabet"));
    }

    [Fact]
    public void FormatForDisplay_GroupsOfFour()
    {
        string canonical = GeneratedSecret.Generate();
        string display = GeneratedSecret.FormatForDisplay(canonical);
        string[] groups = display.Split(' ');
        Assert.Equal(13, groups.Length);
        Assert.All(groups, g => Assert.Equal(4, g.Length));
        Assert.Equal(canonical, string.Concat(groups));
        Assert.Equal("", GeneratedSecret.FormatForDisplay(""));
    }

    [Fact]
    public void IsCanonicalForm_OnlyAcceptsCanonicalSecrets()
    {
        string canonical = GeneratedSecret.Generate();
        Assert.True(GeneratedSecret.IsCanonicalForm(canonical));
        // Mangled sheet forms are not canonical (they normalize to it).
        Assert.False(GeneratedSecret.IsCanonicalForm(canonical.ToLowerInvariant()));
        Assert.False(GeneratedSecret.IsCanonicalForm(GeneratedSecret.FormatForDisplay(canonical)));
        // Wrong length, either way.
        Assert.False(GeneratedSecret.IsCanonicalForm(canonical[..^1]));
        Assert.False(GeneratedSecret.IsCanonicalForm(canonical + "A"));
        // Right length but '0' is outside the base32 alphabet.
        Assert.False(GeneratedSecret.IsCanonicalForm(new string('A', GeneratedSecret.CanonicalLength - 1) + "0"));
        Assert.False(GeneratedSecret.IsCanonicalForm(""));
        Assert.False(GeneratedSecret.IsCanonicalForm(null));
    }

    [Fact]
    public void Normalize_RoundTripsTypedForms()
    {
        string canonical = GeneratedSecret.Generate();
        string display = GeneratedSecret.FormatForDisplay(canonical);
        Assert.Equal(canonical, GeneratedSecret.Normalize(display));
        Assert.Equal(canonical, GeneratedSecret.Normalize(canonical));
        Assert.Equal(canonical, GeneratedSecret.Normalize(canonical.ToLowerInvariant()));
        Assert.Equal(canonical, GeneratedSecret.Normalize(display.Replace(' ', '-')));
        Assert.Equal(canonical, GeneratedSecret.Normalize("  " + display + "\n"));
        Assert.Equal("", GeneratedSecret.Normalize(null));
        Assert.Equal("", GeneratedSecret.Normalize(" - "));
    }

    // ---------- SetupGeneratedSecret ----------

    [Fact]
    public void SetupGeneratedSecret_NoProvider_Rejects400()
    {
        var result = DashboardApi.SetupGeneratedSecret(new ServiceConfig(), ConfigPath(), 2, null);
        Assert.Equal(400, result.Status);
        Assert.Contains("unavailable", ErrorMessage(result));
    }

    [Fact]
    public void SetupGeneratedSecret_BadSlot_Rejects400()
    {
        var result = DashboardApi.SetupGeneratedSecret(
            new ServiceConfig(), ConfigPath(), 3, new SimulatedYubiKeyProvider());
        Assert.Equal(400, result.Status);
        Assert.Contains("slot", ErrorMessage(result));
    }

    [Fact]
    public void SetupGeneratedSecret_EnvPassphraseConfigured_Rejects400()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "env-pass");
        // The env passphrase is an enabled method, so the proof gate
        // runs first; the (valid) env proof passes it and the
        // already-configured refusal is what surfaces.
        var result = DashboardApi.SetupGeneratedSecret(
            new ServiceConfig(), ConfigPath(), 2, new SimulatedYubiKeyProvider(),
            proofPassphrase: "env-pass");
        Assert.Equal(400, result.Status);
        Assert.Contains("already configured", ErrorMessage(result));
    }

    [Fact]
    public void SetupGeneratedSecret_StoredPassphraseConfigured_Rejects400()
    {
        // A stored passphrase is configured, so the proof gate runs
        // first — and no proof can verify here (the fake blob does not
        // resolve), so the gate's 403 is the refusal that surfaces,
        // ahead of the operation's own already-configured check.
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        var result = DashboardApi.SetupGeneratedSecret(
            config, ConfigPath(), 2, new SimulatedYubiKeyProvider());
        Assert.Equal(403, result.Status);
        Assert.Contains("proof", ErrorMessage(result));
    }

    [Fact]
    public void SetupGeneratedSecret_Success_SetsModeResolvesAndUnlocks()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; generated-secret setup success path is Windows-only.");

        var provider = new SimulatedYubiKeyProvider();
        var config = new ServiceConfig();
        string path = ConfigPath();

        var (status, payload) = DashboardApi.SetupGeneratedSecret(config, path, 2, provider);
        Assert.Equal(200, status);
        var dto = Assert.IsType<YubiKeySetupGeneratedDto>(payload);
        Assert.True(dto.Ok);
        Assert.True(dto.Yubikey);

        // The secret comes back exactly once, in grouped display form;
        // its canonical form is what every other path works with.
        string canonical = GeneratedSecret.Normalize(dto.RecoverySecret);
        Assert.Equal(GeneratedSecret.CanonicalLength, canonical.Length);
        Assert.Equal(GeneratedSecret.FormatForDisplay(canonical), dto.RecoverySecret);

        // Mode and setup data persisted; the DPAPI copy resolves to the
        // very same secret through the shared scheduler resolution.
        Assert.True(config.GeneratedSecretMode);
        Assert.True(config.PassphraseYubiKey);
        Assert.False(string.IsNullOrEmpty(config.YubiKeyChallenge));
        Assert.False(string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped));
        Assert.False(string.IsNullOrEmpty(config.YubiKeySlotSecretProtected));
        Assert.False(string.IsNullOrEmpty(config.PassphraseProtected));
        Assert.Equal(canonical, BackupScheduler.GetPassphrase(config));

        var reloaded = ServiceConfig.Load(path);
        Assert.True(reloaded.GeneratedSecretMode);
        Assert.True(reloaded.PassphraseYubiKey);

        // Full circle: a touch unwraps the same secret into a restore
        // session.
        var session = new RestoreSession();
        var (uStatus, _) = DashboardApi.UnlockRestoreSession(config, session, provider);
        Assert.Equal(200, uStatus);
        Assert.Equal(canonical, DashboardApi.GetRestorePassphrase(config, session));

        // Generation is once-ever: a second attempt is rejected now that
        // a secret exists. The generated mode is an enabled method, so
        // the call carries the secret itself as proof (the gate passes;
        // the once-ever refusal is what surfaces).
        var again = DashboardApi.SetupGeneratedSecret(config, path, 2, provider,
            proofPassphrase: canonical);
        Assert.Equal(400, again.Status);
        Assert.Contains("already configured", ErrorMessage(again));
    }

    // ---------- SetPassphrase guards in generated mode ----------

    [Fact]
    public void SetPassphrase_TypedPassphrase_RejectsInGeneratedMode()
    {
        // Generated mode counts as an enabled method, so the proof gate
        // runs first; the env-held secret is the valid proof and the
        // generated-mode guard is what surfaces.
        string canonical = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, canonical);
        var config = new ServiceConfig { GeneratedSecretMode = true };
        var result = DashboardApi.SetPassphrase(
            config, ConfigPath(), "typed-pass", useYubiKey: false, null,
            proofPassphrase: canonical);
        Assert.Equal(400, result.Status);
        Assert.Contains("generated", ErrorMessage(result));
        Assert.True(config.GeneratedSecretMode);
    }

    [Fact]
    public void SetPassphrase_Clear_ResetsGeneratedMode()
    {
        // The owner CAN clear in generated mode — with proof: the typed
        // recovery secret, in its grouped sheet form (the gate
        // normalizes it the way the restore unlock does). The same call
        // with no proof is the C-2 wipe; SecurityProofTests covers it.
        string canonical = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, canonical);
        var config = GeneratedModeConfig();
        var (status, _) = DashboardApi.SetPassphrase(
            config, ConfigPath(), "", useYubiKey: false, null,
            proofPassphrase: GeneratedSecret.FormatForDisplay(canonical));
        Assert.Equal(200, status);
        Assert.False(config.GeneratedSecretMode);
        Assert.False(config.PassphraseYubiKey);
        Assert.Null(config.PassphraseProtected);
        Assert.Null(config.YubiKeyChallenge);
        Assert.Null(config.PassphraseYubiKeyWrapped);
    }

    // ---------- Protection profile in generated mode ----------

    [Fact]
    public void GetProtectionProfile_GeneratedMode_ReportsYubiKeyOnly()
    {
        // The stored DPAPI copy must NOT be reported as a user
        // passphrase, even though HasConfiguredPassphrase sees it.
        var profile = DashboardApi.GetProtectionProfile(GeneratedModeConfig());
        Assert.False(profile.PassphraseEnabled);
        Assert.True(profile.YubiKeyEnabled);
        Assert.True(profile.YubiKeyOnly);
    }

    [Fact]
    public void SetProtectionProfile_GeneratedMode_AcceptsYubiKeyOnly()
    {
        string canonical = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, canonical);
        var config = GeneratedModeConfig();
        var (status, payload) = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = true, YubiKeyOnly = true,
            PhoneEnabled = false, LinkMode = "or",
            ProofPassphrase = canonical,
        });
        Assert.Equal(200, status);
        var profile = Assert.IsType<ProtectionProfileResponse>(payload);
        Assert.False(profile.PassphraseEnabled);
        Assert.True(profile.YubiKeyEnabled);
        Assert.True(profile.YubiKeyOnly);
    }

    [Fact]
    public void SetProtectionProfile_GeneratedMode_RejectsYubiKeyWithoutSetupData()
    {
        // Mode flag set but the setup data is missing: enablement must
        // be refused — the check is on the data, not the flag. (The env
        // secret passes the proof gate so the enablement check is what
        // surfaces.)
        string canonical = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, canonical);
        var config = new ServiceConfig { GeneratedSecretMode = true };
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = true,
            PhoneEnabled = false, LinkMode = "or",
            ProofPassphrase = canonical,
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("not set up", ErrorMessage(result));
    }

    // ---------- Typed-secret restore unlock ----------

    [Fact]
    public void UnlockWithSecret_PassphraseMode_Rejects400()
    {
        var session = new RestoreSession();
        var result = DashboardApi.UnlockRestoreSessionWithSecret(
            new ServiceConfig(), session, GeneratedSecret.Generate());
        Assert.Equal(400, result.Status);
        Assert.Contains("YubiKey-only", ErrorMessage(result));
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void UnlockWithSecret_GeneratedMode_AcceptsAndNormalizes()
    {
        var config = new ServiceConfig
        {
            GeneratedSecretMode = true,
            PassphraseYubiKey = true,
        };
        string canonical = GeneratedSecret.Generate();
        string[] typedForms =
        {
            GeneratedSecret.FormatForDisplay(canonical),   // sheet form
            canonical.ToLowerInvariant(),                  // lowercase copy
            GeneratedSecret.FormatForDisplay(canonical).Replace(' ', '-'),
        };
        foreach (string typed in typedForms)
        {
            var session = new RestoreSession();
            var (status, _) = DashboardApi.UnlockRestoreSessionWithSecret(config, session, typed);
            Assert.Equal(200, status);
            Assert.True(session.IsUnlocked);
            Assert.Equal(canonical, session.GetPassphrase());
            Assert.Equal(canonical, DashboardApi.GetRestorePassphrase(config, session));
        }
    }

    [Fact]
    public void UnlockWithSecret_GeneratedMode_RejectsEmpty()
    {
        var config = new ServiceConfig { GeneratedSecretMode = true };
        var session = new RestoreSession();
        var result = DashboardApi.UnlockRestoreSessionWithSecret(config, session, "   ");
        Assert.Equal(400, result.Status);
        Assert.Contains("required", ErrorMessage(result));
        Assert.False(session.IsUnlocked);
    }

    // ---------- Scheduler secret resolution ----------

    [Fact]
    public void GetPassphrase_StoredCopyFallback_AndEnvPrecedence()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; stored-copy resolution is Windows-only.");

        var config = new ServiceConfig { PassphraseProtected = Dpapi.Protect("stored-secret") };
        Assert.Equal("stored-secret", BackupScheduler.GetPassphrase(config));

        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "env-secret");
        Assert.Equal("env-secret", BackupScheduler.GetPassphrase(config));
    }
}
