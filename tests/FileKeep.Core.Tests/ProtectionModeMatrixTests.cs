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
/// Two honest splits are pinned, not fixed: (1) a phone-only profile
/// is refused by the endpoint but LOADS fine (load-time validation
/// checks only the linkMode string); (2) clearing the passphrase
/// wipes the six unlock fields but leaves the added-passphrase wrap
/// and the phone method in place. Both are reported as residual gaps;
/// the tests pin actual behavior so a future fix updates them.
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

    // ---- The load/endpoint split (residual gap, pinned not fixed) ----

    [Fact]
    public void Load_PhoneOnly_Accepted_EndpointRuleIsNotLoadEnforced()
    {
        // A phone-only profile LOADS: Validate checks the linkMode
        // string only, never the method combination. The endpoint
        // refusal is covered elsewhere (ProtectionProfileTests
        // .RejectsPhoneSole on a fresh config; SecurityProofTests
        // .Profile_PhoneOnly_RefusedEvenWithValidTotpProof on Windows
        // for a phone-only config with a valid TOTP proof). Do not
        // "fix" this test by adding load-time rejection without Joel's
        // go — that would be new enforcement, and a hand-edited
        // phone-only config that loads today would fail tomorrow.
        var phoneOnly = new ServiceConfig { PhoneAuthEnabled = true, ProtectionLinkMode = "or" };
        phoneOnly.Validate();
        var profile = DashboardApi.GetProtectionProfile(phoneOnly);
        Assert.True(profile.PhoneEnabled);
        Assert.False(profile.PassphraseEnabled);
        Assert.False(profile.YubiKeyEnabled);

        // Same split for the "and" rule: load accepts "and" with no
        // methods at all; only the endpoint requires both.
        var andAlone = new ServiceConfig { PhoneAuthEnabled = true, ProtectionLinkMode = "and" };
        andAlone.Validate();
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
    public void SetPassphrase_ClearWipesUnlockFields_Audits_LeavesWrapAndPhone()
    {
        // C-2 claims clearing "wipes the whole unlock configuration".
        // It wipes six fields (asserted). It does NOT clear the added-
        // passphrase wrap or the phone method — after clearing, the
        // profile still reports a passphrase method via the orphaned
        // wrap, even though its generated secret is gone. That is a
        // reported residual gap, pinned here so it cannot silently
        // change shape; the fix (clearing the wrap + phone on clear)
        // is a behavior change for Joel to approve, not this patch.
        string secret = GeneratedSecret.Generate();
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, secret);
        var config = GeneratedConfig();
        config.PassphraseYubiKey = true;
        config.YubiKeySlotSecretProtected = "slot-sentinel";
        config.PassphraseWrapProtected = "wrap-sentinel";
        config.PassphraseWrapSaltB64 = "salt-sentinel";
        config.PassphraseWrapIterations = 600_000;
        config.PhoneAuthEnabled = true;
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
        // Leftovers (residual gap, see header):
        Assert.Equal("wrap-sentinel", config.PassphraseWrapProtected);
        Assert.True(config.PhoneAuthEnabled);
        Assert.True(DashboardApi.GetProtectionProfile(config).PassphraseEnabled);
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
        string path = ConfigPath();
        var config = new ServiceConfig();
        var (lines, log) = Capture();
        var (setupStatus, setupPayload) = DashboardApi.SetupPhoneTotp(config, path);
        Assert.Equal(200, setupStatus);
        string secret = Assert.IsType<PhoneSetupDto>(setupPayload).Secret.Replace(" ", "");
        var (verifyStatus, verifyPayload) = DashboardApi.VerifyPhoneTotp(
            config, CurrentTotpCode(secret), path, log);
        Assert.Equal(200, verifyStatus);
        Assert.True(Assert.IsType<OkDto>(verifyPayload).Ok);
        Assert.True(config.PhoneAuthEnabled);
        Assert.Null(config.PendingPhoneTotpSecretProtected);
        Assert.Contains("AUDIT security.mode ok phone-added=true", lines);
    }
}
