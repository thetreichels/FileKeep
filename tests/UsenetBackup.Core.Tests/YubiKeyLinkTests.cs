using UsenetBackup.Core.Security;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// DashboardApi.LinkYubiKey — the security wizard's "Link your YubiKey"
/// step: wraps the already-configured passphrase with a YubiKey so a
/// protection-profile save with YubiKeyEnabled passes validation.
/// Success-path tests need DPAPI (Windows only) and skip elsewhere;
/// the YubiKey itself is the simulated provider throughout.
/// </summary>
public sealed class YubiKeyLinkTests : IDisposable
{
    private readonly string _workDir;
    private readonly string? _savedPassphrase;

    public YubiKeyLinkTests()
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

    /// <summary>A provider with no key attached.</summary>
    private sealed class NoDeviceProvider : IYubiKeyProvider
    {
        public IReadOnlyList<YubiKeyInfo> FindDevices() => Array.Empty<YubiKeyInfo>();
        public byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout) =>
            throw new InvalidOperationException("No YubiKey detected. Plug one in and try again.");
        public void ProgramHmacSha1Slot(int slot, byte[] secret, bool requireTouch) =>
            throw new InvalidOperationException("No YubiKey detected. Plug one in and try again.");
    }

    [Fact]
    public void LinkYubiKey_NoProvider_Rejects400()
    {
        var result = DashboardApi.LinkYubiKey(new ServiceConfig(), ConfigPath(), 2, null);
        Assert.Equal(400, result.Status);
        Assert.Contains("unavailable", ErrorMessage(result));
    }

    [Fact]
    public void LinkYubiKey_NoPassphrase_Rejects400()
    {
        var result = DashboardApi.LinkYubiKey(
            new ServiceConfig(), ConfigPath(), 2, new SimulatedYubiKeyProvider());
        Assert.Equal(400, result.Status);
        Assert.Contains("No passphrase", ErrorMessage(result));
    }

    [Fact]
    public void LinkYubiKey_BadSlot_Rejects400()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "env-pass");
        var result = DashboardApi.LinkYubiKey(
            new ServiceConfig(), ConfigPath(), 3, new SimulatedYubiKeyProvider());
        Assert.Equal(400, result.Status);
        Assert.Contains("slot", ErrorMessage(result));
    }

    [Fact]
    public void LinkYubiKey_NoDevice_Rejects400()
    {
        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "env-pass");
        var result = DashboardApi.LinkYubiKey(
            new ServiceConfig(), ConfigPath(), 2, new NoDeviceProvider());
        Assert.Equal(400, result.Status);
        Assert.Contains("No YubiKey detected", ErrorMessage(result));
    }

    [Fact]
    public void LinkYubiKey_Success_SetsUpProfileGateAndUnlock()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; link success path is Windows-only.");

        Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "wizard-pass-123");
        var provider = new SimulatedYubiKeyProvider();
        var config = new ServiceConfig();
        string path = ConfigPath();

        var (status, payload) = DashboardApi.LinkYubiKey(config, path, 2, provider);
        Assert.Equal(200, status);
        var ok = Assert.IsType<OkYubiKeyDto>(payload);
        Assert.True(ok.Ok);
        Assert.True(ok.Yubikey);

        // Setup state persisted: YubiKey mode on, all setup fields present.
        Assert.True(config.PassphraseYubiKey);
        Assert.False(string.IsNullOrEmpty(config.YubiKeyChallenge));
        Assert.False(string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped));
        Assert.False(string.IsNullOrEmpty(config.YubiKeySlotSecretProtected));
        Assert.Equal(2, config.YubiKeySlot);
        // The passphrase came only from the environment: no DPAPI copy is
        // created behind the user's back (env-only stays env-only).
        Assert.Null(config.PassphraseProtected);

        var reloaded = ServiceConfig.Load(path);
        Assert.True(reloaded.PassphraseYubiKey);

        // The O3 gate: a profile save with YubiKeyEnabled now passes
        // (it was rejected with "YubiKey is not set up" before the link).
        var (pStatus, _) = DashboardApi.SetProtectionProfile(config, path, new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = true,
            PhoneEnabled = false, LinkMode = "or",
        });
        Assert.Equal(200, pStatus);

        // Full circle: the linked key unlocks a restore session and
        // recovers the exact passphrase that was wrapped.
        var session = new RestoreSession();
        var (uStatus, _) = DashboardApi.UnlockRestoreSession(config, session, provider);
        Assert.Equal(200, uStatus);
        Assert.Equal("wizard-pass-123", DashboardApi.GetRestorePassphrase(config, session));
    }

    [Fact]
    public void SetPassphrase_WithYubiKey_NoProvider_Rejects400()
    {
        var result = DashboardApi.SetPassphrase(
            new ServiceConfig(), ConfigPath(), "typed-pass", useYubiKey: true, null);
        Assert.Equal(400, result.Status);
        Assert.Contains("unavailable", ErrorMessage(result));
    }

    [Fact]
    public void SetPassphrase_WithYubiKey_SetsUpAndStoresCopy()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "DPAPI credential storage requires Windows; SetPassphrase setup path is Windows-only.");

        // Regression guard for the SetupYubiKeyOnConfig extraction: the
        // Settings save-with-YubiKey path must keep its exact behavior —
        // setup runs AND the DPAPI passphrase copy is stored.
        var provider = new SimulatedYubiKeyProvider();
        var config = new ServiceConfig();
        var (status, payload) = DashboardApi.SetPassphrase(
            config, ConfigPath(), "typed-pass", useYubiKey: true, provider);
        Assert.Equal(200, status);
        Assert.True(Assert.IsType<OkYubiKeyDto>(payload).Yubikey);
        Assert.True(config.PassphraseYubiKey);
        Assert.False(string.IsNullOrEmpty(config.PassphraseProtected));
        Assert.False(string.IsNullOrEmpty(config.YubiKeySlotSecretProtected));
    }
}
