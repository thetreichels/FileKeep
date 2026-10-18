using UsenetBackup.Core;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

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
        var prop = result.Payload.GetType().GetProperty("error");
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
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = false,
            PhoneEnabled = false, LinkMode = "and",
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
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob", PassphraseYubiKey = true };
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = false, YubiKeyEnabled = true,
            PhoneEnabled = false, LinkMode = "or",
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("Clear it", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_RejectsYubiKeyEnabledWithoutSetup()
    {
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob" };
        var result = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = true,
            PhoneEnabled = false, LinkMode = "or",
        });
        Assert.Equal(400, result.Status);
        Assert.Contains("not set up", ErrorMessage(result));
    }

    [Fact]
    public void SetProtectionProfile_SavesValidProfile()
    {
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob", PassphraseYubiKey = true };
        string path = ConfigPath();
        var (status, payload) = DashboardApi.SetProtectionProfile(config, path, new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = true,
            PhoneEnabled = true, LinkMode = "and",
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
        });
        Assert.Equal(200, status);
        Assert.False(config.PassphraseYubiKey);
        Assert.Equal("or", config.ProtectionLinkMode);
    }

    [Fact]
    public void SetProtectionProfile_NormalizesLinkModeCase()
    {
        var config = new ServiceConfig { PassphraseProtected = "dpapi-blob", PassphraseYubiKey = true };
        var (status, payload) = DashboardApi.SetProtectionProfile(config, ConfigPath(), new ProtectionProfileRequest
        {
            PassphraseEnabled = true, YubiKeyEnabled = true,
            PhoneEnabled = false, LinkMode = "AND",
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
        new ServiceConfig
        {
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
}
