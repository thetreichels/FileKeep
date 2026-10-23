using UsenetBackup.Core;
using UsenetBackup.Core.Security;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using UsenetBackup.Service.Security;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Security fix phase 1 (findings C-1/C-2/C-3): once any unlock method
/// is enabled, the security-config operations must refuse to run
/// without a fresh proof of an existing method carried by the request —
/// the current passphrase, the typed recovery secret in
/// generated-secret mode, or a YubiKey touch. A phone/TOTP code is
/// never a proof, and an unlocked restore session is not one either.
/// With NO method enabled, first-time setup stays proof-free.
///
/// Fixture note: test configs carry a fake DPAPI blob (unresolvable),
/// so the "current passphrase" resolves through the
/// USENETBACKUP_PASSPHRASE env var, which BackupScheduler.GetPassphrase
/// checks first — the same resolution the production link step uses.
/// </summary>
public sealed class SecurityProofTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string Pass = "the-current-passphrase";

    public SecurityProofTests()
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

    private static void AssertProofRefused((int Status, object Payload) result)
    {
        Assert.Equal(403, result.Status);
        Assert.Contains("proof of an existing unlock method", ErrorMessage(result));
    }

    /// <summary>A config with the passphrase method enabled, resolvable via the env var.</summary>
    private ServiceConfig PassphraseConfig()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        return new ServiceConfig { PassphraseProtected = "dpapi-blob" };
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

    // ---------- C-1: the protection profile (YubiKey gate flip) ----------

    [Fact]
    public void SetProtectionProfile_YubiKeyFlipOff_NoProof_Refused403()
    {
        var config = PassphraseConfig();
        config.PassphraseYubiKey = true;
        config.ProtectionLinkMode = "and";
        // The exact C-1 attack call: one POST flipping the YubiKey gate
        // off, nothing else changed, no proof of any existing method.
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "or",
        });
        AssertProofRefused(result);
        Assert.True(config.PassphraseYubiKey);
        Assert.Equal("and", config.ProtectionLinkMode);
    }

    [Fact]
    public void SetProtectionProfile_YubiKeyFlipOff_WrongProof_Refused403()
    {
        var config = PassphraseConfig();
        config.PassphraseYubiKey = true;
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "or",
            ProofPassphrase = "not-the-passphrase",
        });
        AssertProofRefused(result);
        Assert.True(config.PassphraseYubiKey);
    }

    [Fact]
    public void SetProtectionProfile_YubiKeyFlipOff_WithPassphraseProof_Succeeds()
    {
        var config = PassphraseConfig();
        config.PassphraseYubiKey = true;
        config.ProtectionLinkMode = "and";
        var (status, _) = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "or",
            ProofPassphrase = Pass,
        });
        Assert.Equal(200, status);
        Assert.False(config.PassphraseYubiKey);
        Assert.Equal("or", config.ProtectionLinkMode);
    }

    [Fact]
    public void SetProtectionProfile_WithTouchProof_Succeeds()
    {
        // Proof type (c): a fresh touch unwraps the stored blob. The
        // wrap pair is built with the production setup machinery and
        // the simulated provider, so this runs without DPAPI — and
        // with no env var set, no typed proof could resolve at all.
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped, _) = YubiKeySetup.Setup(provider, Pass, slot: 2);
        var config = new ServiceConfig
        {
            PassphraseProtected = "dpapi-blob",
            PassphraseYubiKey = true,
            YubiKeyChallenge = challenge,
            PassphraseYubiKeyWrapped = wrapped,
            YubiKeySlot = 2,
        };
        var (status, _) = DashboardApi.SetProtectionProfile(config, ConfigPath(),
            new ProtectionProfileRequest
            {
                PassphraseEnabled = true, YubiKeyEnabled = false,
                PhoneEnabled = false, LinkMode = "or",
                ProofYubiKey = true,
            },
            yubiKeyProvider: provider);
        Assert.Equal(200, status);
        Assert.False(config.PassphraseYubiKey);
    }

    [Fact]
    public void SetProtectionProfile_TouchProofWithWrongKey_Refused403()
    {
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped, _) = YubiKeySetup.Setup(provider, Pass, slot: 2);
        var config = new ServiceConfig
        {
            PassphraseProtected = "dpapi-blob",
            PassphraseYubiKey = true,
            YubiKeyChallenge = challenge,
            PassphraseYubiKeyWrapped = wrapped,
            YubiKeySlot = 2,
        };
        // A different key (never programmed with the slot secret)
        // cannot unwrap the blob, so its "touch" proves nothing.
        var stranger = new SimulatedYubiKeyProvider();
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(),
            new ProtectionProfileRequest
            {
                PassphraseEnabled = true, YubiKeyEnabled = false,
                PhoneEnabled = false, LinkMode = "or",
                ProofYubiKey = true,
            },
            yubiKeyProvider: stranger);
        AssertProofRefused(result);
        Assert.True(config.PassphraseYubiKey);
    }

    // ---------- Generated-secret mode: the typed secret is the proof ----------

    [Fact]
    public void SetProtectionProfile_GeneratedMode_DisplayFormSecretProof_Succeeds()
    {
        string canonical = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, canonical);
        var config = new ServiceConfig
        {
            GeneratedSecretMode = true,
            PassphraseYubiKey = true,
            PassphraseProtected = "dpapi-blob",
            YubiKeyChallenge = "challenge",
            PassphraseYubiKeyWrapped = "wrapped",
        };
        // The proof is the recovery-sheet form of the secret (grouped
        // in 4s): the gate normalizes it exactly like the typed-secret
        // restore unlock does.
        var (status, _) = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = true, YubiKeyOnly = true,
            PhoneEnabled = true, LinkMode = "or",
            ProofPassphrase = GeneratedSecret.FormatForDisplay(canonical),
        });
        Assert.Equal(200, status);
        Assert.True(config.PhoneAuthEnabled);
    }

    [Fact]
    public void SetProtectionProfile_GeneratedMode_WrongSecretProof_Refused403()
    {
        string canonical = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, canonical);
        var config = new ServiceConfig
        {
            GeneratedSecretMode = true,
            PassphraseYubiKey = true,
            PassphraseProtected = "dpapi-blob",
            YubiKeyChallenge = "challenge",
            PassphraseYubiKeyWrapped = "wrapped",
        };
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = true, YubiKeyOnly = true,
            PhoneEnabled = true, LinkMode = "or",
            ProofPassphrase = GeneratedSecret.Generate(), // A secret — not THE secret.
        });
        AssertProofRefused(result);
        Assert.False(config.PhoneAuthEnabled);
    }

    // ---------- C-2: the passphrase wipe ----------

    [Fact]
    public void SetPassphrase_EmptyWipe_NoProof_Refused403()
    {
        var config = PassphraseConfig();
        config.PassphraseYubiKey = true;
        config.YubiKeyChallenge = "challenge";
        config.PassphraseYubiKeyWrapped = "wrapped";
        // The exact C-2 attack call: an empty passphrase clears the
        // entire unlock configuration.
        var result = DashboardApi.SetPassphrase(
            config, ConfigPath(), "", useYubiKey: false, null);
        AssertProofRefused(result);
        Assert.Equal("dpapi-blob", config.PassphraseProtected);
        Assert.True(config.PassphraseYubiKey);
        Assert.Equal("challenge", config.YubiKeyChallenge);
    }

    [Fact]
    public void SetPassphrase_EmptyWipe_WithProof_Succeeds()
    {
        var config = PassphraseConfig();
        var (status, _) = DashboardApi.SetPassphrase(
            config, ConfigPath(), "", useYubiKey: false, null,
            proofPassphrase: Pass);
        Assert.Equal(200, status);
        Assert.Null(config.PassphraseProtected);
    }

    [Fact]
    public void SetPassphrase_Replace_NoProof_Refused403()
    {
        var config = PassphraseConfig();
        var result = DashboardApi.SetPassphrase(
            config, ConfigPath(), "a-brand-new-passphrase", useYubiKey: false, null);
        AssertProofRefused(result);
        Assert.Equal("dpapi-blob", config.PassphraseProtected);
    }

    // ---------- C-3: the phone takeover chain ----------

    [Fact]
    public void SetupPhoneTotp_Stranger_NoProof_Refused403()
    {
        // A passphrase method is enabled; a stranger asking to enroll
        // a phone gets no secret and stages nothing. (The gate runs
        // before any DPAPI work, so this runs on any OS.)
        var config = PassphraseConfig();
        var result = DashboardApi.SetupPhoneTotp(config, ConfigPath());
        AssertProofRefused(result);
        Assert.Null(config.PendingPhoneTotpSecretProtected);
        Assert.Null(config.PhoneTotpSecretProtected);
    }

    [Fact]
    public void VerifyPhoneTotp_Stranger_NoProof_Refused403()
    {
        // Even with an enrollment staged, promoting it takes proof —
        // the code from the new secret alone authorizes nothing.
        var config = PassphraseConfig();
        config.PendingPhoneTotpSecretProtected = "staged-secret";
        var result = DashboardApi.VerifyPhoneTotp(config, "123456", ConfigPath());
        AssertProofRefused(result);
        Assert.Equal("staged-secret", config.PendingPhoneTotpSecretProtected);
        Assert.Null(config.PhoneTotpSecretProtected);
    }

    [Fact]
    public void SetupPhoneTotp_CodeShapedProof_Refused403()
    {
        // A 6-digit code offered as the proof field is just a wrong
        // passphrase to the gate: TOTP is never consulted as proof.
        var config = PassphraseConfig();
        config.PhoneAuthEnabled = true;
        var result = DashboardApi.SetupPhoneTotp(config, ConfigPath(),
            proofPassphrase: "123456");
        AssertProofRefused(result);
    }

    [Fact]
    public void PhoneChain_WithPassphraseProof_Succeeds()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; phone enrollment success path is Windows-only.");

        // The legitimate C-3 chain, with proof at every gated step:
        // enroll → verify (promotes) → enable phone in the profile.
        var config = PassphraseConfig();
        string path = ConfigPath();
        var (ss, sp) = DashboardApi.SetupPhoneTotp(config, path, proofPassphrase: Pass);
        Assert.Equal(200, ss);
        string secret = Assert.IsType<PhoneSetupDto>(sp).Secret.Replace(" ", "");
        var (vs, okp) = DashboardApi.VerifyPhoneTotp(config, CurrentTotpCode(secret), path,
            proofPassphrase: Pass);
        Assert.Equal(200, vs);
        Assert.True(Assert.IsType<OkDto>(okp).Ok);
        var (ps, _) = DashboardApi.SetProtectionProfile(config, path, new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = true, LinkMode = "or",
            ProofPassphrase = Pass,
        });
        Assert.Equal(200, ps);
        Assert.True(config.PhoneAuthEnabled);
    }

    [Fact]
    public void ValidTotpCode_OfferedAsProof_Refused403()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; a live TOTP secret is Windows-only here.");

        // Enroll a phone legitimately (with passphrase proof), enable
        // it, then attack: offer a CURRENTLY VALID TOTP code — the
        // very code the restore unlock would accept right now — as the
        // proof for further security changes. It must fail: the phone
        // never authorizes changing methods.
        var config = PassphraseConfig();
        string path = ConfigPath();
        var (ss, sp) = DashboardApi.SetupPhoneTotp(config, path, proofPassphrase: Pass);
        Assert.Equal(200, ss);
        string secret = Assert.IsType<PhoneSetupDto>(sp).Secret.Replace(" ", "");
        var (vs, _) = DashboardApi.VerifyPhoneTotp(config, CurrentTotpCode(secret), path,
            proofPassphrase: Pass);
        Assert.Equal(200, vs);
        var (ps, _) = DashboardApi.SetProtectionProfile(config, path, new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = true, LinkMode = "or",
            ProofPassphrase = Pass,
        });
        Assert.Equal(200, ps);

        string validCode = CurrentTotpCode(secret);
        var profileAttack = DashboardApi.SetProtectionProfile(config, path, new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "or",
            ProofPassphrase = validCode,
        });
        AssertProofRefused(profileAttack);
        Assert.True(config.PhoneAuthEnabled);

        var enrollAttack = DashboardApi.SetupPhoneTotp(config, path,
            proofPassphrase: validCode);
        AssertProofRefused(enrollAttack);
    }

    // ---------- The other gated operations ----------

    [Fact]
    public void AddYubiKeyName_WithMethodEnabled_NoProof_Refused_WithProof_Succeeds()
    {
        var config = PassphraseConfig();
        var refused = DashboardApi.AddYubiKeyName(config, ConfigPath(), "YubiKey 2");
        AssertProofRefused(refused);
        Assert.Empty(config.YubiKeys);

        var (status, _) = DashboardApi.AddYubiKeyName(config, ConfigPath(), "YubiKey 2",
            proofPassphrase: Pass);
        Assert.Equal(200, status);
        Assert.Equal(new[] { "YubiKey 2" }, config.YubiKeys);
    }

    [Fact]
    public void ProgramSpareKey_WithMethodEnabled_NoProof_Refused403()
    {
        var config = PassphraseConfig();
        config.PassphraseYubiKey = true;
        config.YubiKeySlotSecretProtected = "slot-secret";
        var refused = DashboardApi.ProgramSpareKey(
            config, ConfigPath(), new SimulatedYubiKeyProvider());
        AssertProofRefused(refused);

        // With proof the gate passes and the operation reaches its own
        // machinery — which then fails on the fake stored slot secret
        // (400), proving the refusal above came from the gate alone.
        var (status, _) = DashboardApi.ProgramSpareKey(
            config, ConfigPath(), new SimulatedYubiKeyProvider(),
            proofPassphrase: Pass);
        Assert.Equal(400, status);
    }

    [Fact]
    public void LinkYubiKey_WithMethodEnabled_NoProof_Refused403()
    {
        // Passphrase already configured (env): linking a key against it
        // is a security change. No proof → refused before any hardware
        // or passphrase handling.
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, Pass);
        var config = new ServiceConfig();
        var result = DashboardApi.LinkYubiKey(
            config, ConfigPath(), 2, new SimulatedYubiKeyProvider());
        AssertProofRefused(result);
        Assert.False(config.PassphraseYubiKey);
    }

    // ---------- An unlocked session is not proof (and is undisturbed) ----------

    [Fact]
    public void UnlockedRestoreSession_IsNotProof_AndIsNotDisturbed()
    {
        // A restore session unlocked with the typed secret (the
        // UNVERIFIED path) must not authorize security changes…
        var config = new ServiceConfig
        {
            GeneratedSecretMode = true,
            PassphraseYubiKey = true,
        };
        string canonical = GeneratedSecret.Generate();
        var session = new RestoreSession();
        var (us, _) = DashboardApi.UnlockRestoreSessionWithSecret(config, session, canonical);
        Assert.Equal(200, us);
        Assert.True(session.IsUnlocked);

        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = true, YubiKeyOnly = true,
            PhoneEnabled = true, LinkMode = "or",
        });
        AssertProofRefused(result);
        Assert.False(config.PhoneAuthEnabled);

        // …and performing the (refused) check left the session exactly
        // as it was: still unlocked, same passphrase.
        Assert.True(session.IsUnlocked);
        Assert.Equal(canonical, session.GetPassphrase());
    }

    // ---------- First-time setup: no method enabled, no proof required ----------

    [Fact]
    public void SetPassphrase_NoMethodConfigured_NoProof_NotRefused()
    {
        var config = new ServiceConfig();
        var (status, _) = DashboardApi.SetPassphrase(
            config, ConfigPath(), "", useYubiKey: false, null);
        Assert.Equal(200, status);
    }

    [Fact]
    public void SetupPhoneTotp_FirstTime_NoProof_NotRefusedByGate()
    {
        // No method enabled: the gate must not fire. The operation
        // itself needs DPAPI for the staged secret, so off-Windows it
        // ends in the operation's own 400 — the assertion is that the
        // refusal is NOT the gate's 403.
        var config = new ServiceConfig();
        var (status, _) = DashboardApi.SetupPhoneTotp(config, ConfigPath());
        Assert.NotEqual(403, status);
    }

    [Fact]
    public void PhoneChain_FirstTimeSetup_NoProof_Succeeds()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; phone enrollment success path is Windows-only.");

        // First-time setup ordering matters: the staged secret from
        // setup is NOT an enabled method, so the verify/promote step
        // that follows in the same flow must also stay proof-free.
        var config = new ServiceConfig { DashboardPort = 15789 };
        string path = ConfigPath();
        var (ss, sp) = DashboardApi.SetupPhoneTotp(config, path);
        Assert.Equal(200, ss);
        string secret = Assert.IsType<PhoneSetupDto>(sp).Secret.Replace(" ", "");
        var (vs, okp) = DashboardApi.VerifyPhoneTotp(config, CurrentTotpCode(secret), path);
        Assert.Equal(200, vs);
        Assert.True(Assert.IsType<OkDto>(okp).Ok);
        Assert.False(string.IsNullOrWhiteSpace(config.PhoneTotpSecretProtected));
    }
}
