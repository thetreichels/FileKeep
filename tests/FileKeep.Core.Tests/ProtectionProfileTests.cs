using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Service;
using FileKeep.Service.Security;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Protection profile (Passphrase / YubiKey / Phone): config fields,
/// dashboard API validation rules, and config save/load round-trips.
/// </summary>
public sealed class ProtectionProfileTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    public ProtectionProfileTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        // The TOTP guard's limiter is process-static: a lockout left by
        // another class would change this class's verify outcomes, so
        // every test starts (and ends) from a clean guard.
        TotpGuard.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        TotpGuard.ResetForTests();
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string ConfigPath() => Path.Combine(_workDir, "service-" + Guid.NewGuid().ToString("N") + ".json");

    // Security-proof fixtures: configs below carry a fake DPAPI blob
    // (unresolvable), so proof resolution goes through the env var —
    // which BackupScheduler.GetPassphrase checks first. Setting the
    // env var to this value makes ProofPass a valid proof.
    private const string ProofPass = "proof-pass-123";

    private void ArmProof() =>
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, ProofPass);

    private static string ErrorMessage((int Status, object Payload) result)
    {
        Assert.NotEqual(200, result.Status);
        var prop = result.Payload.GetType().GetProperty("Error")
            ?? result.Payload.GetType().GetProperty("error");
        Assert.NotNull(prop);
        return (string?)prop.GetValue(result.Payload) ?? "";
    }

    [Fact]
    public void GetProtectionProfile_Defaults()
    {
        var profile = DashboardApi.GetProtectionProfile(new ServiceConfig());
        Assert.False(profile.PassphraseEnabled);
        Assert.False(profile.YubiKeyEnabled);
        Assert.False(profile.PhoneEnabled);
        Assert.Equal("or", profile.LinkMode);
        Assert.Empty(profile.YubiKeys);
    }

    [Fact]
    public void GetProtectionProfile_ReflectsConfig()
    {
        var config = new ServiceConfig
        {
            PassphraseProtected = "dpapi-blob",
            PassphraseYubiKey = true,
            PhoneAuthEnabled = true,
            ProtectionLinkMode = "and",
            YubiKeys = new List<string> { "YubiKey 1", "YubiKey 2" },
        };
        var profile = DashboardApi.GetProtectionProfile(config);
        Assert.True(profile.PassphraseEnabled);
        Assert.True(profile.YubiKeyEnabled);
        Assert.True(profile.PhoneEnabled);
        Assert.Equal("and", profile.LinkMode);
        Assert.Equal(new[] { "YubiKey 1", "YubiKey 2" }, profile.YubiKeys);
    }

    [Fact]
    public void HasConfiguredPassphrase_SeesEnvVar()
    {
        var config = new ServiceConfig();
        Assert.False(DashboardApi.HasConfiguredPassphrase(config));
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "secret");
        Assert.True(DashboardApi.HasConfiguredPassphrase(config));
    }

    [Fact]
    public void SetProtectionProfile_RejectsBothDisabled()
    {
        var config = new ServiceConfig();
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "or",
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("At least one", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_RejectsPhoneSole()
    {
        // The phone authenticator can only ever be a secondary method:
        // USB recovery cannot use TOTP, so a phone-only profile is
        // refused even though a TOTP code stands alone as a proof in
        // the Windows app. (Corrected 2026-10-25: an earlier edit of
        // this test briefly accepted phone-sole by misreading the
        // "same level" order as covering configuration too.)
        var config = new ServiceConfig();
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = false,
            PhoneEnabled = true, LinkMode = "or",
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("only enabled method", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_RejectsZeroMethods()
    {
        // At least one of Passphrase or YubiKey must stay enabled.
        var config = new ServiceConfig();
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "or",
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("At least one of Passphrase or YubiKey", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_RejectsBadLinkMode()
    {
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "xor",
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("linkMode", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_RejectsAndWithSingleMethod()
    {
        ArmProof();
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "and",
            ProofPassphrase = ProofPass,
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("\"and\"", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_RejectsPassphraseEnabledWithoutPassphrase()
    {
        var config = new ServiceConfig();
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "or",
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("No passphrase", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_RejectsPassphraseDisabledWhileConfigured()
    {
        // Clearing a passphrase is destructive; it stays on the passphrase
        // settings, not the profile wizard.
        ArmProof();
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob", PassphraseYubiKey = true };
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = true,
            PhoneEnabled = false, LinkMode = "or",
            ProofPassphrase = ProofPass,
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("Clear it", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_RejectsYubiKeyEnabledWithoutSetup()
    {
        ArmProof();
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = true,
            PhoneEnabled = false, LinkMode = "or",
            ProofPassphrase = ProofPass,
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("not set up", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_SavesValidProfile()
    {
        ArmProof();
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob", PassphraseYubiKey = true };
        string path = ConfigPath();
        var (status, payload) = DashboardApi.SetProtectionProfile(config, path, new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = true,
            PhoneEnabled = true, LinkMode = "and",
            ProofPassphrase = ProofPass,
        });
        Assert.Equal(200, status);
        var profile = Assert.IsType<ProtectionProfileResponse>(payload);
        Assert.True(profile.PhoneEnabled);
        Assert.Equal("and", profile.LinkMode);
        Assert.True(config.PhoneAuthEnabled);
        Assert.Equal("and", config.ProtectionLinkMode);

        // Persisted: reload from disk.
        var reloaded = ServiceConfig.Load(path);
        Assert.True(reloaded.PhoneAuthEnabled);
        Assert.Equal("and", reloaded.ProtectionLinkMode);
    }

    [Fact]
    public void SetProtectionProfile_CanDisableYubiKey()
    {
        // The owner CAN turn the YubiKey gate off — with proof of the
        // existing passphrase. (The same call with no proof is the C-1
        // attack; SecurityProofTests covers its refusal.)
        ArmProof();
        var config = new ServiceConfig
        {
            PassphraseProtected = "dpapi-blob",
            PassphraseYubiKey = true,
            ProtectionLinkMode = "and",
        };
        var (status, _) = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "or",
            ProofPassphrase = ProofPass,
        });
        Assert.Equal(200, status);
        Assert.False(config.PassphraseYubiKey);
        Assert.Equal("or", config.ProtectionLinkMode);
    }

    [Fact]
    public void SetProtectionProfile_NormalizesLinkModeCase()
    {
        ArmProof();
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob", PassphraseYubiKey = true };
        var (status, payload) = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = true,
            PhoneEnabled = false, LinkMode = "AND",
            ProofPassphrase = ProofPass,
        });
        Assert.Equal(200, status);
        Assert.Equal("and", Assert.IsType<ProtectionProfileResponse>(payload).LinkMode);
    }

    [Fact]
    public void AddYubiKeyName_AddsAndSaves()
    {
        var config = new ServiceConfig();
        string path = ConfigPath();
        var (status, payload) = DashboardApi.AddYubiKeyName(config, path, "YubiKey 1");
        Assert.Equal(200, status);
        var response = Assert.IsType<AddYubiKeyResponse>(payload);
        Assert.True(response.Ok);
        Assert.Equal(new[] { "YubiKey 1" }, response.YubiKeys);

        var reloaded = ServiceConfig.Load(path);
        Assert.Equal(new[] { "YubiKey 1" }, reloaded.YubiKeys);
    }

    [Fact]
    public void AddYubiKeyName_TrimsAndRejectsDuplicates()
    {
        var config = new ServiceConfig();
        var (first, _) = DashboardApi.AddYubiKeyName(config, ConfigPath(), "  YubiKey 1 ");
        Assert.Equal(200, first);
        Assert.Equal(new[] { "YubiKey 1" }, config.YubiKeys);

        var dup = DashboardApi.AddYubiKeyName(config, ConfigPath(), "yubikey 1");
        Assert.Equal(400, dup.Status);
        Assert.Contains("already registered", ErrorMessage(dup));
        Assert.Single(config.YubiKeys);
    }

    [Fact]
    public void AddYubiKeyName_RejectsEmpty()
    {
        var config = new ServiceConfig();
        var result = DashboardApi.AddYubiKeyName(config, ConfigPath(), "   ");
        Assert.Equal(400, result.Status);
        Assert.Contains("required", ErrorMessage(result));

        var nullName = DashboardApi.AddYubiKeyName(config, ConfigPath(), null);
        Assert.Equal(400, nullName.Status);
    }

    [Fact]
    public void ServiceConfig_RoundTripsProtectionFields()
    {
        string path = ConfigPath();
        // Fixture must be a valid profile under load enforcement
        // (Joel, 2026-10-27): phone is secondary-only and "and"
        // requires both primaries, so the round-trip config carries
        // both primaries. The assertions below are about field
        // round-tripping, not about phone-only validity.
        new ServiceConfig
        {
            PassphraseProtected = "dpapi-copy-sentinel",
            PassphraseYubiKey = true,
            PhoneAuthEnabled = true,
            ProtectionLinkMode = "and",
            YubiKeys = new List<string> { "YubiKey 1", "YubiKey 2" },
        }.Save(path);
        var loaded = ServiceConfig.Load(path);
        Assert.True(loaded.PhoneAuthEnabled);
        Assert.Equal("and", loaded.ProtectionLinkMode);
        Assert.Equal(new[] { "YubiKey 1", "YubiKey 2" }, loaded.YubiKeys);
        Assert.False(new ServiceConfig().PhoneAuthEnabled);
        Assert.Equal("or", new ServiceConfig().ProtectionLinkMode);
        Assert.Empty(new ServiceConfig().YubiKeys);
    }

    [Fact]
    public void ServiceConfig_ValidateRejectsBadLinkMode()
    {
        var config = new ServiceConfig { ProtectionLinkMode = "sometimes" };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    private static string CurrentTotpCode(string base32)
    {
        byte[] secret = TotpCrypto.FromBase32(base32);
        try
        {
            long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TotpCrypto.TimeStepSeconds;
            return TotpCrypto.ComputeCode(secret, step);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }
    }

    [Fact]
    public void PhonePromotion_SaveFailure_RollsBackAndRetrySaves()
    {
        // P2-R3: promotion mutated the live config BEFORE Save; a failed
        // save left memory promoted and disk unpromoted — a retry then
        // verified against the new secret, returned success, and never
        // re-saved, so the next restart silently reverted the phone
        // secret. A failed save must roll the promotion back.
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("DPAPI requires Windows");

        string path = ConfigPath();
        var config = new ServiceConfig { DashboardPort = 15789 };
        var (s1, p1) = DashboardApi.SetupPhoneTotp(config, path);
        Assert.Equal(200, s1);
        string secret1 = Assert.IsType<PhoneSetupDto>(p1).Secret.Replace(" ", "");
        var (v1, ok1) = DashboardApi.VerifyPhoneTotp(config, CurrentTotpCode(secret1), path);
        Assert.True(Assert.IsType<OkDto>(ok1).Ok);
        string liveBefore = config.PhoneTotpSecretProtected!;

        // Stage a re-enrollment, then verify against an UNWRITABLE path.
        var (s2, p2) = DashboardApi.SetupPhoneTotp(config, path);
        Assert.Equal(200, s2);
        string secret2 = Assert.IsType<PhoneSetupDto>(p2).Secret.Replace(" ", "");
        string pendingBefore = config.PendingPhoneTotpSecretProtected!;
        string badPath = Path.Combine(_workDir, "no-such-dir", "service.json");
        var (vBad, _) = DashboardApi.VerifyPhoneTotp(config, CurrentTotpCode(secret2), badPath);
        Assert.Equal(400, vBad);
        // Rolled back: live untouched, pending still staged.
        Assert.Equal(liveBefore, config.PhoneTotpSecretProtected);
        Assert.Equal(pendingBefore, config.PendingPhoneTotpSecretProtected);

        // The retry (same code, writable path) promotes AND persists.
        var (v2, ok2) = DashboardApi.VerifyPhoneTotp(config, CurrentTotpCode(secret2), path);
        Assert.Equal(200, v2);
        Assert.True(Assert.IsType<OkDto>(ok2).Ok);
        Assert.Null(config.PendingPhoneTotpSecretProtected);
        var reloaded = ServiceConfig.Load(path);
        Assert.Equal(config.PhoneTotpSecretProtected, reloaded.PhoneTotpSecretProtected);
    }

    [Fact]
    public void PhoneReenrollment_StagesPending_PromotesOnlyOnVerifiedCode()
    {
        // SF-17: re-enrollment used to overwrite the live TOTP secret at
        // setup time, so abandoning or failing the wizard invalidated
        // the authenticator that already worked. The new secret must be
        // staged as pending and promoted only by a verified code.
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("DPAPI requires Windows");

        string path = ConfigPath();
        var config = new ServiceConfig { DashboardPort = 15789 };

        // First enrollment: setup stages pending; a verified code
        // promotes it to live.
        var (s1, p1) = DashboardApi.SetupPhoneTotp(config, path);
        Assert.Equal(200, s1);
        string secret1 = Assert.IsType<PhoneSetupDto>(p1).Secret.Replace(" ", "");
        Assert.Null(config.PhoneTotpSecretProtected);
        Assert.False(string.IsNullOrWhiteSpace(config.PendingPhoneTotpSecretProtected));
        var (v1, ok1) = DashboardApi.VerifyPhoneTotp(config, CurrentTotpCode(secret1), path);
        Assert.Equal(200, v1);
        Assert.True(Assert.IsType<OkDto>(ok1).Ok);
        Assert.False(string.IsNullOrWhiteSpace(config.PhoneTotpSecretProtected));
        Assert.Null(config.PendingPhoneTotpSecretProtected);
        string liveAfterFirst = config.PhoneTotpSecretProtected!;

        // Re-enrollment: setup must NOT touch the live secret...
        var (s2, p2) = DashboardApi.SetupPhoneTotp(config, path);
        Assert.Equal(200, s2);
        string secret2 = Assert.IsType<PhoneSetupDto>(p2).Secret.Replace(" ", "");
        Assert.Equal(liveAfterFirst, config.PhoneTotpSecretProtected);
        Assert.False(string.IsNullOrWhiteSpace(config.PendingPhoneTotpSecretProtected));

        // ...and neither must a failed verification.
        string wrong = CurrentTotpCode(secret2) == "000000" ? "111111" : "000000";
        var (v2, ok2) = DashboardApi.VerifyPhoneTotp(config, wrong, path);
        Assert.Equal(200, v2);
        Assert.False(Assert.IsType<OkDto>(ok2).Ok);
        Assert.Equal(liveAfterFirst, config.PhoneTotpSecretProtected);
        Assert.False(string.IsNullOrWhiteSpace(config.PendingPhoneTotpSecretProtected));

        // The correct code promotes the new secret, persisted to disk.
        var (v3, ok3) = DashboardApi.VerifyPhoneTotp(config, CurrentTotpCode(secret2), path);
        Assert.Equal(200, v3);
        Assert.True(Assert.IsType<OkDto>(ok3).Ok);
        Assert.Null(config.PendingPhoneTotpSecretProtected);
        Assert.NotEqual(liveAfterFirst, config.PhoneTotpSecretProtected);
        var reloaded = ServiceConfig.Load(path);
        Assert.Equal(config.PhoneTotpSecretProtected, reloaded.PhoneTotpSecretProtected);
        Assert.Null(reloaded.PendingPhoneTotpSecretProtected);
    }
}
