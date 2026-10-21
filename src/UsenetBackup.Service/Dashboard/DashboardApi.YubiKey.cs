using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Security;
using UsenetBackup.Core.Service;

namespace UsenetBackup.Service;

/// <summary>
/// DashboardApi — Passphrase status, YubiKey setup/status and the restore-session
/// endpoints. Restores in YubiKey mode require an unlocked session.
/// </summary>
public static partial class DashboardApi
{
    /// <summary>
    /// Reports whether a repository passphrase is available (env var or
    /// stored) and how it's protected. Never returns the passphrase itself.
    /// </summary>
    public static (int Status, object Payload) GetPassphraseStatus(ServiceConfig config)
    {
        bool fromEnv = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar));
        bool stored = !string.IsNullOrEmpty(config.PassphraseProtected);
        return (200, new PassphraseStatusDto(
            fromEnv || stored,
            fromEnv ? "environment" : stored ? "stored" : "none",
            stored && config.PassphraseYubiKey));
    }

    /// <summary>
    /// Stores (or clears) the repository passphrase.
    /// A null/empty passphrase clears the stored value and disables YubiKey mode.
    /// With <paramref name="useYubiKey"/>, the key's OTP slot is programmed
    /// (two touches: program + wrap) and the passphrase is stored wrapped
    /// under the challenge-response-derived KEK; a DPAPI copy is still kept
    /// so scheduled backups run unattended. Restores then require a touch
    /// to open a session.
    /// </summary>
    public static (int Status, object Payload) SetPassphrase(ServiceConfig config,
        string configPath, string? passphrase, bool useYubiKey,
        IYubiKeyProvider? yubiKeyProvider, Action<string>? log = null)
    {
        if (!string.IsNullOrEmpty(passphrase) && config.GeneratedSecretMode)
            return (400, new ErrorDto("YubiKey-only mode is active: the repository secret was generated, not typed. Clear the generated secret before setting a passphrase."));
        if (string.IsNullOrEmpty(passphrase))
        {
            config.PassphraseProtected = null;
            config.PassphraseYubiKey = false;
            config.GeneratedSecretMode = false;
            config.YubiKeyChallenge = null;
            config.PassphraseYubiKeyWrapped = null;
            config.YubiKeySlotSecretProtected = null;
        }
        else if (useYubiKey)
        {
            if (yubiKeyProvider is null)
                return (400, new ErrorDto("YubiKey support is unavailable in this build."));
            var error = SetupYubiKeyOnConfig(config, yubiKeyProvider, passphrase,
                config.YubiKeySlot, storePassphraseCopy: true);
            if (error is not null)
                return error.Value;
        }
        else
        {
            config.PassphraseProtected = Dpapi.Protect(passphrase);
            config.PassphraseYubiKey = false;
            config.YubiKeyChallenge = null;
            config.PassphraseYubiKeyWrapped = null;
        }
        try
        {
            config.Save(configPath);
            log?.Invoke("dashboard: repository passphrase " +
                (string.IsNullOrEmpty(passphrase) ? "cleared" :
                 useYubiKey ? "saved (DPAPI + YubiKey-wrapped)" : "saved (DPAPI-encrypted)"));
            return (200, new OkYubiKeyDto(true, useYubiKey && !string.IsNullOrEmpty(passphrase)));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Runs YubiKey setup (program the slot with a fresh secret, then wrap
    /// <paramref name="passphrase"/> under the challenge-response-derived
    /// KEK — two touches) and writes the resulting challenge / wrapped
    /// passphrase / slot secret onto <paramref name="config"/>. Does NOT
    /// save the config. Returns null on success, or the error result.
    /// Shared by <see cref="SetPassphrase"/> (passphrase typed in Settings)
    /// and <see cref="LinkYubiKey"/> (the wizard's link step, where the
    /// passphrase was already saved by an earlier step).
    /// </summary>
    private static (int Status, object Payload)? SetupYubiKeyOnConfig(
        ServiceConfig config, IYubiKeyProvider yubiKeyProvider, string passphrase,
        int slot, bool storePassphraseCopy)
    {
        try
        {
            var (challenge, wrapped, slotSecret) = YubiKeySetup.Setup(
                yubiKeyProvider, passphrase, slot);
            try
            {
                if (storePassphraseCopy)
                    config.PassphraseProtected = Dpapi.Protect(passphrase);
                config.YubiKeyChallenge = challenge;
                config.PassphraseYubiKeyWrapped = wrapped;
                config.YubiKeySlotSecretProtected = Dpapi.Protect(
                    Convert.ToBase64String(slotSecret));
                config.YubiKeySlot = slot;
                config.PassphraseYubiKey = true;
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(slotSecret);
            }
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
        catch (TimeoutException)
        {
            return (400, new ErrorDto("Timed out waiting for a YubiKey touch. Try again and touch the key when it flashes."));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto("YubiKey setup failed: " + ex.Message));
        }
    }

    /// <summary>
    /// Links a YubiKey to the already-configured repository passphrase:
    /// the same setup as <see cref="SetPassphrase"/> with the YubiKey
    /// option, except the passphrase is resolved server-side (environment
    /// variable or the stored DPAPI copy) instead of being supplied by the
    /// caller. This is the security wizard's "Link your YubiKey" step —
    /// the wizard saved the passphrase in its own step, so it is never
    /// sent back through the browser. On success the config has YubiKey
    /// mode fully set up (<see cref="ServiceConfig.PassphraseYubiKey"/>),
    /// so a protection-profile save with YubiKeyEnabled passes validation.
    /// An existing DPAPI passphrase copy is left untouched; when the
    /// passphrase comes only from the environment variable, no copy is
    /// created (env-only mode stays env-only).
    /// </summary>
    public static (int Status, object Payload) LinkYubiKey(ServiceConfig config,
        string configPath, int? slot, IYubiKeyProvider? yubiKeyProvider,
        Action<string>? log = null)
    {
        if (yubiKeyProvider is null)
            return (400, new ErrorDto("YubiKey support is unavailable in this build."));
        int useSlot = slot ?? config.YubiKeySlot;
        if (useSlot is not 1 and not 2)
            return (400, new ErrorDto("YubiKey slot must be 1 or 2."));
        string? passphrase = BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(passphrase))
            return (400, new ErrorDto("No passphrase is configured. Set a repository passphrase first."));
        var error = SetupYubiKeyOnConfig(config, yubiKeyProvider, passphrase,
            useSlot, storePassphraseCopy: false);
        if (error is not null)
            return error.Value;
        try
        {
            config.Save(configPath);
            log?.Invoke("dashboard: YubiKey linked to the repository passphrase");
            return (200, new OkYubiKeyDto(true, true));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Sets up passphrase-free YubiKey-only mode: generates a fresh
    /// repository secret (see <see cref="GeneratedSecret"/>), runs the
    /// standard YubiKey setup on it (program + wrap — two touches), and
    /// stores a DPAPI copy so scheduled backups run unattended, exactly
    /// like a typed passphrase. Generation is once-ever: it is rejected
    /// while any passphrase or secret is already configured (including a
    /// previously linked key's wrapped blob). The generated secret is
    /// returned exactly once, in this response, in its grouped display
    /// form for the recovery sheet; no endpoint ever returns it again
    /// and it is never logged.
    /// </summary>
    public static (int Status, object Payload) SetupGeneratedSecret(ServiceConfig config,
        string configPath, int? slot, IYubiKeyProvider? yubiKeyProvider,
        Action<string>? log = null)
    {
        if (yubiKeyProvider is null)
            return (400, new ErrorDto("YubiKey support is unavailable in this build."));
        int useSlot = slot ?? config.YubiKeySlot;
        if (useSlot is not 1 and not 2)
            return (400, new ErrorDto("YubiKey slot must be 1 or 2."));
        if (HasConfiguredPassphrase(config) ||
            !string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped))
            return (400, new ErrorDto("A passphrase or secret is already configured. Clear it before generating a new secret."));
        string secret = GeneratedSecret.Generate();
        var error = SetupYubiKeyOnConfig(config, yubiKeyProvider, secret,
            useSlot, storePassphraseCopy: true);
        if (error is not null)
            return error.Value;
        config.GeneratedSecretMode = true;
        try
        {
            config.Save(configPath);
            log?.Invoke("dashboard: generated secret set up (YubiKey-only mode)");
            return (200, new YubiKeySetupGeneratedDto(true, true,
                GeneratedSecret.FormatForDisplay(secret)));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Opens a restore session by typing the recovery-sheet secret
    /// instead of touching the YubiKey. Generated-secret (YubiKey-only)
    /// mode only: in passphrase mode there is deliberately no typed
    /// unlock on the running system, and this endpoint must not become
    /// one. The typed value is normalized to canonical form and held in
    /// the same session the touch path fills. There is no verifier, so
    /// the secret is accepted unverified — a wrong secret fails closed
    /// at the first chunk decryption, exactly like the recovery wizard.
    /// </summary>
    public static (int Status, object Payload) UnlockRestoreSessionWithSecret(
        ServiceConfig config, RestoreSession session, string? secret,
        Action<string>? log = null)
    {
        if (!config.GeneratedSecretMode)
            return (400, new ErrorDto("Typed-secret unlock is only available in YubiKey-only mode."));
        string canonical = GeneratedSecret.Normalize(secret);
        if (canonical.Length == 0)
            return (400, new ErrorDto("A recovery secret is required."));
        session.Unlock(canonical);
        log?.Invoke("dashboard: restore session unlocked via recovery secret");
        return (200, new OkDto(true));
    }

    /// <summary>
    /// Lists attached YubiKeys for the setup UI.
    /// </summary>
    public static (int Status, object Payload) GetYubiKeyStatus(IYubiKeyProvider? provider)
    {
        if (provider is null)
            return (200, new YubiKeyStatusDto(false, Array.Empty<string>()));
        IReadOnlyList<YubiKeyInfo> devices;
        try { devices = provider.FindDevices(); }
        catch { devices = Array.Empty<YubiKeyInfo>(); }
        return (200, new YubiKeyStatusDto(
            devices.Count > 0,
            devices.Select(d => d.Description).ToArray()));
    }

    /// <summary>
    /// Programs a spare YubiKey with the stored slot secret so it works
    /// interchangeably with the original key. Requires YubiKey mode to be
    /// set up (the secret is stored at setup time).
    /// </summary>
    public static (int Status, object Payload) ProgramSpareKey(ServiceConfig config,
        string configPath, IYubiKeyProvider? yubiKeyProvider, Action<string>? log = null)
    {
        if (yubiKeyProvider is null)
            return (400, new ErrorDto("YubiKey support is unavailable in this build."));
        if (!config.PassphraseYubiKey || string.IsNullOrEmpty(config.YubiKeySlotSecretProtected))
            return (400, new ErrorDto("YubiKey is not set up. Complete setup first before programming a spare."));
        try
        {
            string secretB64 = Dpapi.Unprotect(config.YubiKeySlotSecretProtected);
            byte[] secret = Convert.FromBase64String(secretB64);
            try
            {
                YubiKeySetup.ProgramSpareKey(yubiKeyProvider, secret, config.YubiKeySlot);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            }
            log?.Invoke("dashboard: programmed spare YubiKey");
            return (200, new OkDto(true));
        }
        catch (TimeoutException)
        {
            return (400, new ErrorDto("Timed out waiting for a YubiKey touch. Try again and touch the key when it flashes."));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto($"Failed to program spare key: {ex.Message}" ));
        }
    }

    /// <summary>
    /// Opens a restore session: touches the YubiKey, unwraps the passphrase
    /// and holds it in <paramref name="session"/> memory. Only meaningful
    /// when the config has YubiKey mode enabled.
    /// </summary>
    public static (int Status, object Payload) UnlockRestoreSession(ServiceConfig config,
        RestoreSession session, IYubiKeyProvider? provider, Action<string>? log = null)
    {
        if (!config.PassphraseYubiKey)
            return (400, new ErrorDto("YubiKey mode is not enabled for this passphrase."));
        if (string.IsNullOrEmpty(config.YubiKeyChallenge) ||
            string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped))
            return (400, new ErrorDto("YubiKey was not set up completely. Re-save the passphrase with YubiKey enabled."));
        if (provider is null)
            return (400, new ErrorDto("YubiKey support is unavailable in this build."));
        try
        {
            string passphrase = YubiKeySetup.Unlock(provider,
                config.YubiKeyChallenge, config.PassphraseYubiKeyWrapped, config.YubiKeySlot);
            session.Unlock(passphrase);
            log?.Invoke("dashboard: restore session unlocked via YubiKey");
            return (200, new OkDto(true));
        }
        catch (TimeoutException)
        {
            return (400, new ErrorDto("Timed out waiting for a YubiKey touch. Try again and touch the key when it flashes."));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (400, new ErrorDto("The YubiKey touch succeeded but the passphrase did not decrypt. Wrong key, or the slot was reprogrammed after setup."));
        }
        catch (InvalidOperationException ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto("Unlock failed: " + ex.Message));
        }
    }

    /// <summary>Locks the restore session, wiping the held passphrase.</summary>
    public static (int Status, object Payload) LockRestoreSession(RestoreSession session)
    {
        session.Lock();
        return (200, new OkDto(true));
    }

    /// <summary>
    /// Reports whether YubiKey mode is on, the session is unlocked, and
    /// whether this is generated-secret (YubiKey-only) mode — the restore
    /// view reads all three from this one payload to decide which unlock
    /// affordances to render (typed-secret unlock exists only in
    /// generated mode).
    /// </summary>
    public static (int Status, object Payload) GetRestoreSessionStatus(
        ServiceConfig config, RestoreSession session) =>
        (200, new YubiKeyUnlockDto(config.PassphraseYubiKey, session.IsUnlocked,
            config.GeneratedSecretMode));

    /// <summary>
    /// Resolves the passphrase for restore-side operations. When YubiKey
    /// mode is on, only the unlocked session passphrase is used (null when
    /// locked); otherwise the environment/DPAPI value. Backup-creation
    /// endpoints use <see cref="BackupScheduler.GetPassphrase"/> directly
    /// and never consult the session.
    /// </summary>
    public static string? GetRestorePassphrase(ServiceConfig config, RestoreSession? session) =>
        config.PassphraseYubiKey ? session?.GetPassphrase() : BackupScheduler.GetPassphrase(config);
}
