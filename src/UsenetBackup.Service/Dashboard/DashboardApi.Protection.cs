using UsenetBackup.Core;
using UsenetBackup.Core.Service;

namespace UsenetBackup.Service;

/// <summary>
/// DashboardApi — protection profile (Passphrase / YubiKey / Phone):
/// reporting the current profile, saving it with the wizard's rules, and
/// registering extra YubiKey labels. The Phone method is always a
/// standalone fallback: it can never be the sole method, it is never
/// linkable, and it is running-system-only (never used in USB recovery).
/// </summary>
public static partial class DashboardApi
{
    /// <summary>Maximum registered YubiKey labels kept in the config.</summary>
    public const int MaxYubiKeyNames = 16;

    /// <summary>Maximum length of a single YubiKey label.</summary>
    public const int MaxYubiKeyNameLength = 64;

    /// <summary>
    /// True when a repository passphrase is configured (environment variable
    /// or DPAPI-stored copy), mirroring <see cref="GetPassphraseStatus"/>.
    /// </summary>
    public static bool HasConfiguredPassphrase(ServiceConfig config) =>
        !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar)) ||
        !string.IsNullOrEmpty(config.PassphraseProtected);

    /// <summary>Builds the current protection profile for the dashboard wizard.</summary>
    public static ProtectionProfileResponse GetProtectionProfile(ServiceConfig config) =>
        new()
        {
            PassphraseEnabled = HasConfiguredPassphrase(config),
            YubiKeyEnabled = config.PassphraseYubiKey,
            PhoneEnabled = config.PhoneAuthEnabled,
            LinkMode = string.IsNullOrEmpty(config.ProtectionLinkMode)
                ? "or" : config.ProtectionLinkMode,
            YubiKeys = new List<string>(config.YubiKeys),
        };

    /// <summary>
    /// Saves the protection profile from the wizard. The Passphrase toggle
    /// reflects reality: it cannot be turned on without a configured
    /// passphrase, and a configured passphrase cannot be switched off here
    /// (clearing a passphrase is destructive and stays on the passphrase
    /// settings). Enabling YubiKey requires YubiKey setup to be complete
    /// (programmed via the passphrase settings with a physical touch);
    /// disabling it only flips the mode flag so setup data survives.
    /// </summary>
    public static (int Status, object Payload) SetProtectionProfile(ServiceConfig config,
        string configPath, ProtectionProfileRequest request, Action<string>? log = null)
    {
        string linkMode = (request.LinkMode ?? "or").Trim().ToLowerInvariant();
        if (linkMode is not ("or" or "and"))
            return (400, new ErrorDto("linkMode must be \"or\" or \"and\"."));

        bool passphraseConfigured = HasConfiguredPassphrase(config);
        if (request.PassphraseEnabled && !passphraseConfigured)
            return (400, new ErrorDto("No passphrase is configured. Set a repository passphrase first."));
        if (!request.PassphraseEnabled && passphraseConfigured)
            return (400, new ErrorDto("A passphrase is configured. Clear it from the passphrase settings before turning Passphrase off."));
        if (request.YubiKeyEnabled && !config.PassphraseYubiKey)
            return (400, new ErrorDto("YubiKey is not set up. Complete YubiKey setup first."));
        if (linkMode == "and" && (!request.PassphraseEnabled || !request.YubiKeyEnabled))
            return (400, new ErrorDto("\"and\" link mode requires both Passphrase and YubiKey to be enabled."));
        if (!request.PassphraseEnabled && !request.YubiKeyEnabled)
        {
            if (request.PhoneEnabled)
                return (400, new ErrorDto("Phone authenticator cannot be the only enabled method. Enable Passphrase or YubiKey first."));
            return (400, new ErrorDto("At least one of Passphrase or YubiKey must stay enabled."));
        }

        config.PassphraseYubiKey = request.YubiKeyEnabled;
        config.PhoneAuthEnabled = request.PhoneEnabled;
        config.ProtectionLinkMode = linkMode;
        try
        {
            config.Save(configPath);
            log?.Invoke("dashboard: protection profile updated " +
                $"(passphrase={request.PassphraseEnabled}, yubikey={request.YubiKeyEnabled}, " +
                $"phone={request.PhoneEnabled}, link={linkMode})");
            return (200, GetProtectionProfile(config));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Registers a display name for another YubiKey ("Add another YubiKey"
    /// button). Labels only — the actual key programming is done with the
    /// same slot secret through <c>/api/yubikey/program-spare</c>, so any
    /// registered key works interchangeably.
    /// </summary>
    public static (int Status, object Payload) AddYubiKeyName(ServiceConfig config,
        string configPath, string? name, Action<string>? log = null)
    {
        string clean = (name ?? "").Trim();
        if (clean.Length == 0)
            return (400, new ErrorDto("A YubiKey name is required."));
        if (clean.Length > MaxYubiKeyNameLength)
            return (400, new ErrorDto($"YubiKey name is too long (max {MaxYubiKeyNameLength} characters)." ));
        if (config.YubiKeys.Any(n => n.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            return (400, new ErrorDto($"A YubiKey named \"{clean}\" is already registered." ));
        if (config.YubiKeys.Count >= MaxYubiKeyNames)
            return (400, new ErrorDto($"Too many YubiKeys registered (max {MaxYubiKeyNames})." ));
        config.YubiKeys.Add(clean);
        try
        {
            config.Save(configPath);
            log?.Invoke($"dashboard: registered YubiKey \"{clean}\"");
            return (200, new AddYubiKeyResponse { Ok = true, YubiKeys = new List<string>(config.YubiKeys) });
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }
}
