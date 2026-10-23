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
    public void Session_TypedSecretUnlock_StartsUnverified_UntilProven()
    {
        // MU-15: the typed recovery secret has no verifier — any
        // non-empty string unlocks the session — so it must not
        // present as verified until a restore proves it.
        var s = new RestoreSession();
        s.UnlockUnverified("some-typed-secret");
        Assert.True(s.IsUnlocked);
        Assert.False(s.IsVerified);

        s.MarkVerified();
        Assert.True(s.IsVerified);

        s.Lock();
        Assert.False(s.IsUnlocked);
        Assert.False(s.IsVerified);

        // The by-construction paths (YubiKey unwrap) start verified.
        s.Unlock("unwrapped");
        Assert.True(s.IsVerified);
    }

    [Fact]
    public void SessionStatus_ReportsVerifiedAndPhoneEnabled()
    {
        var config = new ServiceConfig
        {
            PassphraseYubiKey = true,
            GeneratedSecretMode = true,
            PhoneAuthEnabled = true,
        };
        var s = new RestoreSession();
        var (_, payload) = DashboardApi.GetRestoreSessionStatus(config, s);
        var dto = Assert.IsType<YubiKeyUnlockDto>(payload);
        Assert.True(dto.PhoneEnabled);
        Assert.False(dto.Unlocked);
        Assert.False(dto.Verified);

        // Typed-secret unlock flips Unlocked but NOT Verified.
        var (status, _) = DashboardApi.UnlockRestoreSessionWithSecret(config, s, "ABCD2345");
        Assert.Equal(200, status);
        var (_, payload2) = DashboardApi.GetRestoreSessionStatus(config, s);
        var dto2 = Assert.IsType<YubiKeyUnlockDto>(payload2);
        Assert.True(dto2.Unlocked);
        Assert.False(dto2.Verified);
    }

    [Fact]
    public void PhoneUnlock_VerifiedCode_ReleasesStoredPassphrase()
    {
        // MU-15: the phone method must actually unlock something — a
        // verified TOTP code releases the machine's DPAPI-held
        // passphrase into the session.
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("DPAPI requires Windows");

        string dir = Path.Combine(Path.GetTempPath(), "ub-phone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "service.json");
        try
        {
            var config = new ServiceConfig
            {
                DashboardPort = 15789,
                PassphraseYubiKey = true,
                PhoneAuthEnabled = true,
                PassphraseProtected = UsenetBackup.Core.Dpapi.Protect("the-real-passphrase"),
            };
            // Enroll the phone (staged → promoted by a verified code).
            // YubiKey + phone are already enabled methods here, so the
            // enrollment carries proof of the existing passphrase —
            // resolvable from the stored DPAPI copy on Windows.
            var (ss, sp) = DashboardApi.SetupPhoneTotp(config, path,
                proofPassphrase: "the-real-passphrase");
            Assert.Equal(200, ss);
            string secret = Assert.IsType<PhoneSetupDto>(sp).Secret.Replace(" ", "");
            var (vs, okp) = DashboardApi.VerifyPhoneTotp(config, CurrentTotpCode(secret), path,
                proofPassphrase: "the-real-passphrase");
            Assert.Equal(200, vs);
            Assert.True(Assert.IsType<OkDto>(okp).Ok);

            var session = new RestoreSession();
            // Wrong code: refused, session stays locked.
            var (badStatus, _) = DashboardApi.UnlockRestoreSessionWithPhone(
                config, session, "000000");
            Assert.Equal(400, badStatus);
            Assert.False(session.IsUnlocked);

            // Right code: session unlocked with the STORED passphrase,
            // and verified (it came from the machine's own store).
            var (goodStatus, _) = DashboardApi.UnlockRestoreSessionWithPhone(
                config, session, CurrentTotpCode(secret));
            Assert.Equal(200, goodStatus);
            Assert.True(session.IsUnlocked);
            Assert.True(session.IsVerified);
            Assert.Equal("the-real-passphrase", session.GetPassphrase());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string CurrentTotpCode(string base32)
    {
        byte[] secret = UsenetBackup.Service.Security.TotpCrypto.FromBase32(base32);
        try
        {
            long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() /
                UsenetBackup.Service.Security.TotpCrypto.TimeStepSeconds;
            return UsenetBackup.Service.Security.TotpCrypto.ComputeCode(secret, step);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
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
        // Assert the reported VALUES, parsed as the DTO (S-F2): the old
        // version asserted the serialized JSON contained the property
        // NAME "detected", which is present even when nothing was
        // detected and the device list is empty.
        var dto = Assert.IsType<YubiKeyStatusDto>(payload);
        Assert.True(dto.Detected);
        Assert.Equal(new[] { "Simulated YubiKey" }, dto.Devices);
    }

    private sealed class ThrowingProvider : IYubiKeyProvider
    {
        public IReadOnlyList<YubiKeyInfo> FindDevices() =>
            throw new InvalidOperationException("HID stack unavailable (simulated)");
        public byte[] CalculateHmacSha1(byte[] challenge, int slot, TimeSpan timeout) =>
            throw new NotSupportedException();
        public void ProgramHmacSha1Slot(int slot, byte[] secret, bool requireTouch) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void GetYubiKeyStatus_ProviderFailure_IsAnErrorNotNoDevices()
    {
        // SF-23: a broken provider must surface as an error — reporting
        // "no devices" sends the user hunting for a key that is already
        // plugged in while the driver problem stays invisible.
        var (status, payload) = DashboardApi.GetYubiKeyStatus(new ThrowingProvider());
        Assert.Equal(500, status);
        string json = System.Text.Json.JsonSerializer.Serialize(payload);
        Assert.Contains("YubiKey detection failed", json);
        Assert.Contains("HID stack unavailable", json);
    }
}
