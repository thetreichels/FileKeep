using UsenetBackup.Core.Security;
using UsenetBackup.Core.Service;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// RestoreSession behavior and the DashboardApi YubiKey session gate.
/// </summary>
public sealed class RestoreSessionTests
{
    [Fact]
    public void Session_StartsLocked()
    {
        var s = new RestoreSession();
        Assert.False(s.IsUnlocked);
        Assert.Null(s.GetPassphrase());
    }

    [Fact]
    public void Session_Unlock_HoldsPassphrase()
    {
        var s = new RestoreSession();
        s.Unlock("s3cret");
        Assert.True(s.IsUnlocked);
        Assert.Equal("s3cret", s.GetPassphrase());
    }

    [Fact]
    public void Session_Lock_WipesPassphrase()
    {
        var s = new RestoreSession();
        s.Unlock("s3cret");
        s.Lock();
        Assert.False(s.IsUnlocked);
        Assert.Null(s.GetPassphrase());
    }

    [Fact]
    public void Session_ExpiresAfterIdleTimeout()
    {
        var old = RestoreSession.IdleTimeout;
        try
        {
            RestoreSession.IdleTimeout = TimeSpan.FromMilliseconds(50);
            var s = new RestoreSession();
            s.Unlock("s3cret");
            Assert.True(s.IsUnlocked);
            Thread.Sleep(120);
            Assert.False(s.IsUnlocked);
            Assert.Null(s.GetPassphrase());
        }
        finally { RestoreSession.IdleTimeout = old; }
    }

    [Fact]
    public void GetRestorePassphrase_NoYubiKey_UsesEnvOrDpapi()
    {
        var config = new ServiceConfig { PassphraseYubiKey = false };
        // No env var, no DPAPI value -> null.
        Assert.Null(DashboardApi.GetRestorePassphrase(config, null));
        Assert.Null(DashboardApi.GetRestorePassphrase(config, new RestoreSession()));
    }

    [Fact]
    public void GetRestorePassphrase_YubiKeyMode_RequiresSession()
    {
        var config = new ServiceConfig { PassphraseYubiKey = true };
        var locked = new RestoreSession();
        // Locked (or absent) session -> null even though a session object exists.
        Assert.Null(DashboardApi.GetRestorePassphrase(config, locked));
        Assert.Null(DashboardApi.GetRestorePassphrase(config, null));

        var unlocked = new RestoreSession();
        unlocked.Unlock("from-yubikey");
        Assert.Equal("from-yubikey", DashboardApi.GetRestorePassphrase(config, unlocked));
    }

    [Fact]
    public void UnlockRestoreSession_FullFlow_SimulatedKey()
    {
        var provider = new SimulatedYubiKeyProvider();
        var config = new ServiceConfig();
        var session = new RestoreSession();

        // Not in YubiKey mode yet -> 400.
        var (s0, _) = DashboardApi.UnlockRestoreSession(config, session, provider);
        Assert.Equal(400, s0);

        // Simulate a completed setup.
        var (challenge, wrapped, _secret) = YubiKeySetup.Setup(provider, "repo-pass", slot: 2);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_secret);
        config.PassphraseYubiKey = true;
        config.YubiKeyChallenge = challenge;
        config.PassphraseYubiKeyWrapped = wrapped;

        // Locked session gates restores.
        Assert.Null(DashboardApi.GetRestorePassphrase(config, session));

        // Unlock via (simulated) touch.
        var (s1, _) = DashboardApi.UnlockRestoreSession(config, session, provider);
        Assert.Equal(200, s1);
        Assert.True(session.IsUnlocked);
        Assert.Equal("repo-pass", DashboardApi.GetRestorePassphrase(config, session));

        // Lock again.
        var (s2, _) = DashboardApi.LockRestoreSession(session);
        Assert.Equal(200, s2);
        Assert.False(session.IsUnlocked);
        Assert.Null(DashboardApi.GetRestorePassphrase(config, session));
    }

    [Fact]
    public void UnlockRestoreSession_WrongKey_Fails400()
    {
        var provider = new SimulatedYubiKeyProvider();
        var (challenge, wrapped, _secret) = YubiKeySetup.Setup(provider, "repo-pass", slot: 2);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_secret);
        // Attacker swaps in a different key (reprogrammed slot).
        provider.ProgramSlot(2, System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));

        var config = new ServiceConfig
        {
            PassphraseYubiKey = true,
            YubiKeyChallenge = challenge,
            PassphraseYubiKeyWrapped = wrapped,
        };
        var (status, _) = DashboardApi.UnlockRestoreSession(config, new RestoreSession(), provider);
        Assert.Equal(400, status);
    }

    [Fact]
    public void GetYubiKeyStatus_ReportsSimulatedDevice()
    {
        var (status, payload) = DashboardApi.GetYubiKeyStatus(new SimulatedYubiKeyProvider());
        Assert.Equal(200, status);
        string json = System.Text.Json.JsonSerializer.Serialize(payload);
        Assert.Contains("detected", json);
    }
}
