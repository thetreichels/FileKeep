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
        return (200, new
        {
            configured = fromEnv || stored,
            source = fromEnv ? "environment" : stored ? "stored" : "none",
            yubikey = stored && config.PassphraseYubiKey,
        });
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
        if (string.IsNullOrEmpty(passphrase))
        {
            config.PassphraseProtected = null;
            config.PassphraseYubiKey = false;
            config.YubiKeyChallenge = null;
            config.PassphraseYubiKeyWrapped = null;
            config.YubiKeySlotSecretProtected = null;
        }
        else if (useYubiKey)
        {
            if (yubiKeyProvider is null)
                return (400, new { error = "YubiKey support is unavailable in this build." });
            try
            {
                var (challenge, wrapped, slotSecret) = YubiKeySetup.Setup(
                    yubiKeyProvider, passphrase, config.YubiKeySlot);
                try
                {
                    config.PassphraseProtected = Dpapi.Protect(passphrase);
                    config.YubiKeyChallenge = challenge;
                    config.PassphraseYubiKeyWrapped = wrapped;
                    config.YubiKeySlotSecretProtected = Dpapi.Protect(
                        Convert.ToBase64String(slotSecret));
                    config.PassphraseYubiKey = true;
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(slotSecret);
                }
            }
            catch (InvalidOperationException ex)
            {
                return (400, new { error = ex.Message });
            }
            catch (TimeoutException)
            {
                return (400, new { error = "Timed out waiting for a YubiKey touch. Try again and touch the key when it flashes." });
            }
            catch (Exception ex)
            {
                return (400, new { error = "YubiKey setup failed: " + ex.Message });
            }
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
            return (200, new { ok = true, yubikey = useYubiKey && !string.IsNullOrEmpty(passphrase) });
        }
        catch (Exception ex)
        {
            return (400, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Lists attached YubiKeys for the setup UI.
    /// </summary>
    public static (int Status, object Payload) GetYubiKeyStatus(IYubiKeyProvider? provider)
    {
        if (provider is null)
            return (200, new { detected = false, devices = Array.Empty<string>() });
        IReadOnlyList<YubiKeyInfo> devices;
        try { devices = provider.FindDevices(); }
        catch { devices = Array.Empty<YubiKeyInfo>(); }
        return (200, new
        {
            detected = devices.Count > 0,
            devices = devices.Select(d => d.Description).ToArray(),
        });
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
            return (400, new { error = "YubiKey support is unavailable in this build." });
        if (!config.PassphraseYubiKey || string.IsNullOrEmpty(config.YubiKeySlotSecretProtected))
            return (400, new { error = "YubiKey is not set up. Complete setup first before programming a spare." });
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
            return (200, new { ok = true });
        }
        catch (TimeoutException)
        {
            return (400, new { error = "Timed out waiting for a YubiKey touch. Try again and touch the key when it flashes." });
        }
        catch (Exception ex)
        {
            return (400, new { error = $"Failed to program spare key: {ex.Message}" });
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
            return (400, new { error = "YubiKey mode is not enabled for this passphrase." });
        if (string.IsNullOrEmpty(config.YubiKeyChallenge) ||
            string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped))
            return (400, new { error = "YubiKey was not set up completely. Re-save the passphrase with YubiKey enabled." });
        if (provider is null)
            return (400, new { error = "YubiKey support is unavailable in this build." });
        try
        {
            string passphrase = YubiKeySetup.Unlock(provider,
                config.YubiKeyChallenge, config.PassphraseYubiKeyWrapped, config.YubiKeySlot);
            session.Unlock(passphrase);
            log?.Invoke("dashboard: restore session unlocked via YubiKey");
            return (200, new { ok = true });
        }
        catch (TimeoutException)
        {
            return (400, new { error = "Timed out waiting for a YubiKey touch. Try again and touch the key when it flashes." });
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (400, new { error = "The YubiKey touch succeeded but the passphrase did not decrypt. Wrong key, or the slot was reprogrammed after setup." });
        }
        catch (InvalidOperationException ex)
        {
            return (400, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return (400, new { error = "Unlock failed: " + ex.Message });
        }
    }

    /// <summary>Locks the restore session, wiping the held passphrase.</summary>
    public static (int Status, object Payload) LockRestoreSession(RestoreSession session)
    {
        session.Lock();
        return (200, new { ok = true });
    }

    /// <summary>Reports whether YubiKey mode is on and the session is unlocked.</summary>
    public static (int Status, object Payload) GetRestoreSessionStatus(
        ServiceConfig config, RestoreSession session) =>
        (200, new { yubikey = config.PassphraseYubiKey, unlocked = session.IsUnlocked });

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
