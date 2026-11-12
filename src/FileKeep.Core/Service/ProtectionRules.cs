namespace FileKeep.Core.Service;

/// <summary>
/// Shared protection-method combination rules (audit D8): the
/// method-combination checks formerly duplicated between
/// <c>ServiceConfigValidator</c> (config load) and
/// <c>DashboardApi.SetProtectionProfile</c> (wizard save).
/// Both enforcement sites stay and both call this predicate; only the
/// error text is shared, so the two sites can never drift apart again.
/// Callers pass already-derived booleans — how "passphrase enabled" is
/// computed differs per site (stored fields vs. request toggles) and
/// must not be re-derived here.
/// </summary>
public static class ProtectionRules
{
    /// <summary>Validates a protection-method combination. Returns the error
    /// message when invalid, else null. Rules: phone is secondary-only
    /// (USB recovery cannot use TOTP); "and" requires both primaries.
    /// The endpoint's extra all-off rule ("At least one of Passphrase or
    /// YubiKey must stay enabled") is deliberately NOT here: a fresh
    /// config with no method at all still loads fine at the validator,
    /// so that rule stays a standalone endpoint check.</summary>
    public static string? ValidateCombination(
        bool passphraseEnabled, bool yubiKeyEnabled, bool phoneEnabled, LinkKind linkMode)
    {
        if (linkMode == LinkKind.And && (!passphraseEnabled || !yubiKeyEnabled))
            return "\"and\" link mode requires both Passphrase and YubiKey to be enabled.";
        if (phoneEnabled && !passphraseEnabled && !yubiKeyEnabled)
            return "Phone authenticator cannot be the only enabled method. Enable Passphrase or YubiKey first.";
        return null;
    }
}
