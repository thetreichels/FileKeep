using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service;
using FileKeep.Service.Security;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Protection-mode matrix (security review rec 8 remainder): the rules
/// live in code, and this file pins the full combination matrix at the
/// layer that enforces each rule. Cells already covered elsewhere are
/// NOT duplicated here — see the notes naming the covering test.
/// Two former splits are now fixed (Joel, 2026-10-27): (1) the
/// endpoint's method-combination rules are enforced at load —
/// phone-only profiles and "and" profiles without both primaries
/// fail Validate; (2) clearing the passphrase also removes the
/// added-passphrase wrap and the phone method. The tests below pin
/// the fixed behavior.
/// </summary>
public sealed class ProtectionModeMatrixTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    private const string AddedPass = "the-added-passphrase";

    public ProtectionModeMatrixTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _savedPassphrase = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        TotpGuard.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        TotpGuard.ResetForTests();
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private string ConfigPath() => Path.Combine(_workDir, "service-" + Guid.NewGuid().ToString("N") + ".json");

    private static (List<string> Lines, Action<string> Log) Capture()
    {
        var lines = new List<string>();
        Action<string> log = line => lines.Add(line);
        return (lines, log);
    }

    private static string ErrorMessage((int Status, object Payload) result) =>
        result.Payload is ErrorDto error ? error.Error : result.Payload?.ToString() ?? "";

    private static ServiceConfig GeneratedConfig() => new()
    {
        GeneratedSecretMode = true,
        YubiKeyChallenge = "yk-challenge-sentinel",
        PassphraseYubiKeyWrapped = "yk-wrapped-sentinel",
        PassphraseProtected = "dpapi-copy-sentinel",
    };

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

    // ---- Load enforcement of the profile method rules (Joel, 2026-10-27) ----

    [Fact]
    public void Load_PhoneOnly_Rejected_AndRequiresBoth()
    {
        // The profile endpoint's method-combination rules are now
        // enforced at load (Validate), not only at the endpoint:
        // phone is secondary-only, and "and" requires both primaries.
        // The endpoint refusal is covered elsewhere
        // (ProtectionProfileTests.RejectsPhoneSole on a fresh config;
        // SecurityProofTests.Profile_PhoneOnly_RefusedEvenWithValidTotpProof
        // on Windows for a phone-only config with a valid TOTP proof).
        var phoneOnly = new ServiceConfig { PhoneAuthEnabled = true, ProtectionLinkMode = "or" };
        var ex = Assert.Throws<InvalidOperationException>(() => phoneOnly.Validate());
        Assert.Contains("only enabled method", ex.Message);

        // Phone-only with "and" is rejected by the phone rule above.
        var andPhoneOnly = new ServiceConfig { PhoneAuthEnabled = true, ProtectionLinkMode = "and" };
        Assert.Throws<InvalidOperationException>(() => andPhoneOnly.Validate());

        // "and" with exactly one primary is rejected too: "and"
        // means both, per the endpoint rule.
        var andOnePrimary = new ServiceConfig { PassphraseProtected = "x", ProtectionLinkMode = "and" };
        var andEx = Assert.Throws<InvalidOperationException>(() => andOnePrimary.Validate());
        Assert.Contains("both Passphrase and YubiKey", andEx.Message);

        // "and" with no methods at all is rejected as well.
        var andNone = new ServiceConfig { ProtectionLinkMode = "and" };
        Assert.Throws<InvalidOperationException>(() => andNone.Validate());

        // Legitimate states still load: fresh configs (no methods,
        // linkMode "or"), single primaries under "or", both primaries
        // under "and", and a primary plus phone under "or".
        new ServiceConfig().Validate();
        new ServiceConfig { PassphraseProtected = "x" }.Validate();
        new ServiceConfig { GeneratedSecretMode = true }.Validate();
        new ServiceConfig { PassphraseProtected = "x", PassphraseYubiKey = true, ProtectionLinkMode = "and" }.Validate();
        new ServiceConfig { PassphraseProtected = "x", PhoneAuthEnabled = true }.Validate();
    }

    // ---- Typed passphrase vs YubiKey-only mode ----

    [Fact]
    public void SetPassphrase_SetInGeneratedMode_Refused()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        var (lines, log) = Capture();
        var result = DashboardApi.SetPassphrase(config, ConfigPath(), "new-pass-123",
            useYubiKey: false, yubiKeyProvider: null, log: log, proofPassphrase: secret);
        Assert.Equal(400, result.Status);
        Assert.Contains("YubiKey-only mode is active", ErrorMessage(result));
        Assert.True(config.GeneratedSecretMode);
        Assert.DoesNotContain(lines, l => l.Contains("passphrase-set=true"));
    }

    [Fact]
    public void SetPassphrase_ClearWipesUnlockFields_Audits_ClearsWrapAndPhone()
    {
        // C-2 claims clearing "wipes the whole unlock configuration".
        // It wipes six fields (asserted) and, since the 2026-10-27
        // follow-up, the added-passphrase wrap and the phone method
        // as well — after clearing, no method remains and the profile
        // reports none.
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        config.PassphraseYubiKey = true;
        config.YubiKeySlotSecretProtected = "slot-sentinel";
        config.PassphraseWrapProtected = "wrap-sentinel";
        config.PassphraseWrapSaltB64 = "salt-sentinel";
        config.PassphraseWrapIterations = 600_000;
        config.PhoneAuthEnabled = true;
        config.PhoneTotpSecretProtected = "totp-sentinel";
        config.PendingPhoneTotpSecretProtected = "pending-sentinel";
        var (lines, log) = Capture();

        var (status, _) = DashboardApi.SetPassphrase(config, ConfigPath(), "",
            useYubiKey: false, yubiKeyProvider: null, log: log, proofPassphrase: secret);
        Assert.Equal(200, status);
        Assert.Contains("AUDIT security.mode ok passphrase-cleared=true", lines);
        Assert.Null(config.PassphraseProtected);
        Assert.False(config.PassphraseYubiKey);
        Assert.False(config.GeneratedSecretMode);
        Assert.Null(config.YubiKeyChallenge);
        Assert.Null(config.PassphraseYubiKeyWrapped);
        Assert.Null(config.YubiKeySlotSecretProtected);
        // The wrap and the phone method are cleared with it:
        Assert.Null(config.PassphraseWrapProtected);
        Assert.Null(config.PassphraseWrapSaltB64);
        Assert.Equal(0, config.PassphraseWrapIterations);
        Assert.False(config.PhoneAuthEnabled);
        Assert.Null(config.PhoneTotpSecretProtected);
        Assert.Null(config.PendingPhoneTotpSecretProtected);
        // The env var above is proof scaffolding, not stored state:
        // HasConfiguredPassphrase counts it, so restore the ambient
        // value before asserting the cleared stored profile reports
        // no methods (Dispose restores it too).
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, _savedPassphrase);
        Assert.False(DashboardApi.GetProtectionProfile(config).PassphraseEnabled);
        Assert.False(DashboardApi.GetProtectionProfile(config).PhoneEnabled);
    }

    // ---- Mode-change audit records (Linux-runnable) ----

    [Fact]
    public void AddPassphraseWrap_AuditsModeChange()
    {
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        var (lines, log) = Capture();
        var (status, _) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null, log: log, proofPassphrase: secret);
        Assert.Equal(200, status);
        Assert.Contains("AUDIT security.mode ok passphrase-added=true", lines);
    }

    [Fact]
    public void ProfileToggleOff_ClearsWrap_AuditsModeChange()
    {
        // The wrap-clearing itself is covered by PassphraseWrapTests
        // .ProfileToggle_MapsToWrap; this pins the audit record and
        // the end-to-end flow (add via the endpoint, toggle off via
        // the profile endpoint with the added passphrase as proof).
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        var (lines, log) = Capture();
        var (addStatus, _) = DashboardApi.AddPassphraseWrap(config, ConfigPath(), AddedPass,
            yubiKeyProvider: null, log: log, proofPassphrase: secret);
        Assert.Equal(200, addStatus);
        var request = new ProtectionProfileRequest
        {
            PassphraseEnabled = false,
            YubiKeyEnabled = true,
            PhoneEnabled = false,
            LinkMode = "or",
            ProofPassphrase = AddedPass,
        };
        var (status, _) = DashboardApi.SetProtectionProfile(config, ConfigPath(), request, log);
        Assert.Equal(200, status);
        Assert.Null(config.PassphraseWrapProtected);
        Assert.Contains("AUDIT security.mode ok passphrase=False yubikey=True phone=False link=or", lines);
    }

    // ---- No silent fallback to plaintext ----

    [Fact]
    public void SaveLoad_BlobsPreserved_NoPlaintextInFile_UndecryptableBlobIsNotPassphrase()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "test-pass-123");
        string path = ConfigPath();
        var config = new ServiceConfig
        {
            PassphraseProtected = new string('Z', 120),
            PassphraseYubiKeyWrapped = "yk-wrapped-sentinel",
            YubiKeyChallenge = "yk-challenge-sentinel",
        };
        config.Save(path);
        string text = File.ReadAllText(path);
        Assert.Contains(new string('Z', 120), text);
        Assert.DoesNotContain("test-pass-123", text);
        var loaded = ServiceConfig.Load(path);
        Assert.Equal(new string('Z', 120), loaded.PassphraseProtected);
        Assert.Equal("yk-wrapped-sentinel", loaded.PassphraseYubiKeyWrapped);

        // An undecryptable blob is never used as the passphrase: the
        // resolver reports a problem and yields null, not the blob.
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, null);
        var broken = new ServiceConfig { PassphraseProtected = "garbage-not-a-dpapi-blob" };
        Assert.Null(BackupScheduler.GetPassphrase(broken));
    }

    // ---- Windows-only mode-change audits (DPAPI / hardware path) ----

    [Fact]
    public void SetPassphrase_Set_AuditsPassphraseSet_WindowsOnly()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; passphrase-set success path is Windows-only.");
        var config = new ServiceConfig();
        var (lines, log) = Capture();
        var (status, _) = DashboardApi.SetPassphrase(config, ConfigPath(), "new-pass-123",
            useYubiKey: false, yubiKeyProvider: null, log: log);
        Assert.Equal(200, status);
        Assert.Contains("AUDIT security.mode ok passphrase-set=true", lines);
        Assert.False(string.IsNullOrEmpty(config.PassphraseProtected));
    }

    [Fact]
    public void LinkYubiKey_AuditsYubikeyLinked_WindowsOnly()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; YubiKey-link success path is Windows-only.");
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "link-pass-123");
        var config = new ServiceConfig();
        var (lines, log) = Capture();
        var (status, _) = DashboardApi.LinkYubiKey(config, ConfigPath(), 2,
            new SimulatedYubiKeyProvider(), log, proofPassphrase: "link-pass-123");
        Assert.Equal(200, status);
        Assert.Contains("AUDIT security.mode ok yubikey-linked=true", lines);
        Assert.True(config.PassphraseYubiKey);
    }

    [Fact]
    public void SetupGeneratedSecret_AuditsGeneratedSecret_WindowsOnly()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; generated-secret setup success path is Windows-only.");
        var config = new ServiceConfig();
        var (lines, log) = Capture();
        var (status, _) = DashboardApi.SetupGeneratedSecret(config, ConfigPath(), 2,
            new SimulatedYubiKeyProvider(), log);
        Assert.Equal(200, status);
        Assert.Contains("AUDIT security.mode ok generated-secret=true", lines);
        Assert.True(config.GeneratedSecretMode);
    }

    [Fact]
    public void VerifyPhoneTotp_Promotion_AuditsPhoneAdded_WindowsOnly()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; phone promotion success path is Windows-only.");
        // Verify promotes the pending secret to live; it does NOT
        // enable the method — the caller enables phone via the
        // profile step after a successful verify (VerifyPhoneTotp's
        // documented contract). The fixture therefore carries a
        // stored passphrase: phone is secondary-only, so the profile
        // step needs a primary, and its proof is the typed
        // passphrase against the DPAPI copy.
        string path = ConfigPath();
        var config = new ServiceConfig
        {
            PassphraseProtected = FileKeep.Core.Dpapi.Protect("test-pass"),
        };
        var (lines, log) = Capture();
        var (setupStatus, setupPayload) = DashboardApi.SetupPhoneTotp(config, path, log,
            proofPassphrase: "test-pass");
        Assert.Equal(200, setupStatus);
        string secret = Assert.IsType<PhoneSetupDto>(setupPayload).Secret.Replace(" ", "");
        var (verifyStatus, verifyPayload) = DashboardApi.VerifyPhoneTotp(
            config, CurrentTotpCode(secret), path, log, proofPassphrase: "test-pass");
        Assert.Equal(200, verifyStatus);
        Assert.True(Assert.IsType<OkDto>(verifyPayload).Ok);
        Assert.Null(config.PendingPhoneTotpSecretProtected);
        Assert.Contains("AUDIT security.mode ok phone-added=true", lines);
        var (profileStatus, _) = DashboardApi.SetProtectionProfile(config, path,
            new ProtectionProfileRequest
            {
                PassphraseEnabled = true, YubiKeyEnabled = false,
                PhoneEnabled = true, LinkMode = "or",
                ProofPassphrase = "test-pass",
            }, log);
        Assert.Equal(200, profileStatus);
        Assert.True(config.PhoneAuthEnabled);
    }
}
