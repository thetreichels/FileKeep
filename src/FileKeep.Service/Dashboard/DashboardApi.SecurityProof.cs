using System.Security.Cryptography;
using System.Text;
using FileKeep.Core;
using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service.Security;

namespace FileKeep.Service;

/// <summary>
/// DashboardApi — proof of an existing unlock method for security-config
/// changes (security fix phase 1, findings C-1/C-2/C-3).
///
/// The bug class: every endpoint that changes the unlock-method set
/// (protection profile, passphrase save/clear, phone enrollment, YubiKey
/// link/spare/generated setup, key labels) mutated the config with no
/// evidence the caller knows any existing method. The dashboard has no
/// authentication and its CSRF token is served to anyone who fetches the
/// page, so any local process could turn the YubiKey gate off, wipe the
/// whole unlock config, or enroll its own phone and unlock as "verified".
///
/// The rule (the product's own design invariant): once at least one
/// unlock method is enabled, changing security settings requires a FRESH
/// proof carried by the request itself —
///   (a) the current passphrase, or
///   (b) the typed recovery secret in generated-secret (YubiKey-only)
///       mode, or
///   (c) a fresh YubiKey touch that unwraps the stored blob, or
///   (d) a valid TOTP code from the phone authenticator, checked
///       against the LIVE secret only through <see cref="TotpGuard"/>
///       (rate-limited, replay-protected).
/// (d) is the 2026-10-25 reversal of the phase-1 rule, at Joel's order:
/// TOTP now works as a proof like passphrase and YubiKey — a code
/// alone proves, no second method needed at use time in the Windows
/// app. What did NOT change: TOTP can still only ever be a SECONDARY
/// configured method (the profile endpoint refuses a phone-only
/// profile, because USB recovery needs Passphrase or YubiKey); an
/// already-unlocked restore session is still NOT a proof — the check
/// below deliberately never consults session state, and performing it
/// never touches one — and USB recovery still never uses TOTP. When
/// NO method is enabled yet (first-time setup) no proof is required,
/// so the setup wizard's first calls keep working proof-free.
/// </summary>
public static partial class DashboardApi
{
    /// <summary>The refusal message every gated operation returns without valid proof.</summary>
    public const string SecurityProofRequiredMessage =
        "Changing security settings requires proof of an existing unlock method.";

    /// <summary>
    /// True when at least one unlock method is currently enabled,
    /// judged by the protection profile the server itself reports
    /// (passphrase configured, YubiKey on — generated-secret mode
    /// included — or phone on). A merely staged/enrolled-but-disabled
    /// phone secret is not an enabled method.
    /// </summary>
    public static bool HasEnabledUnlockMethod(ServiceConfig config)
    {
        var profile = GetProtectionProfile(config);
        return profile.PassphraseEnabled || profile.YubiKeyEnabled || profile.PhoneEnabled;
    }

    /// <summary>
    /// The shared gate for security-config operations. Returns null
    /// when the operation may proceed — either no method is enabled yet
    /// (first-time setup) or the request carried a valid fresh proof —
    /// and the (403, error) result to return verbatim when it may not.
    /// Runs before any config mutation and outside the config lock: a
    /// touch proof waits on hardware and must never hold JobsLock.
    /// </summary>
    public static (int Status, object Payload)? CheckSecurityChangeProof(
        ServiceConfig config, IYubiKeyProvider? yubiKeyProvider,
        string? proofPassphrase, bool proofYubiKey, Action<string>? log = null,
        string? proofTotp = null)
    {
        if (!HasEnabledUnlockMethod(config))
            return null;

        // (a)/(b): knowledge proof — the current passphrase, or the
        // typed recovery secret in generated-secret mode.
        if (!string.IsNullOrEmpty(proofPassphrase) &&
            VerifyCurrentSecretProof(config, proofPassphrase))
            return null;

        // (e): knowledge proof — the added passphrase in YubiKey-only
        // mode. Unwrapping the passphrase wrap proves the method,
        // even though the wrapping passphrase is not the root secret
        // itself. (The typed recovery secret already proves via (a);
        // this branch covers only the added passphrase.)
        if (!string.IsNullOrEmpty(proofPassphrase) &&
            TryUnwrapPassphraseWrap(config, proofPassphrase, out _))
            return null;

        // (c): possession proof — a fresh touch unwraps the stored
        // blob, exactly the mechanism UnlockRestoreSession uses. The
        // unwrapped value is discarded here: this is a proof check,
        // not an unlock, and no restore session is created or altered.
        if (proofYubiKey && yubiKeyProvider is not null &&
            (config.PassphraseYubiKey || config.GeneratedSecretMode) &&
            !string.IsNullOrEmpty(config.YubiKeyChallenge) &&
            !string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped))
        {
            try
            {
                _ = YubiKeySetup.Unlock(yubiKeyProvider,
                    config.YubiKeyChallenge, config.PassphraseYubiKeyWrapped,
                    config.YubiKeySlot);
                return null;
            }
            catch
            {
                // No device, no touch in time, or the blob did not
                // unwrap: not a proof. Fall through to the refusal.
            }
        }

        // (d): possession proof — a valid TOTP code from the phone
        // authenticator, live secret only, rate-limited and
        // replay-protected by the guard. A code from a pending
        // re-enrollment secret proves nothing and is never accepted
        // here; neither is a code offered through the passphrase field
        // (that field is still passphrase-only by design).
        if (!string.IsNullOrEmpty(proofTotp) &&
            TotpGuard.TryValidateLiveCode(config, proofTotp, log))
            return null;

        log?.Invoke("dashboard: security settings change refused — " +
            "no valid proof of an existing unlock method was supplied");
        AuditLog.Record(log, AuditLog.AuthFailure, AuditLog.Fail, "proof=security-change");
        return (403, new ErrorDto(SecurityProofRequiredMessage));
    }

    /// <summary>
    /// Verifies a typed proof against the currently configured secret.
    /// The current secret is resolved through the same path the
    /// scheduler and the link step use (<see cref="BackupScheduler.GetPassphrase"/>:
    /// environment variable, then the DPAPI-stored copy) — never a
    /// plaintext comparison against stored material. In
    /// generated-secret mode the candidate first goes through
    /// <see cref="GeneratedSecret.Normalize"/>, the same normalization
    /// the typed-secret restore unlock applies, so the grouped /
    /// lowercase / hyphenated sheet forms all prove. The comparison is
    /// constant-time. When no secret resolves (e.g. YubiKey set up with
    /// no stored copy and no env value), a typed proof cannot verify —
    /// the touch proof remains available.
    /// </summary>
    private static bool VerifyCurrentSecretProof(ServiceConfig config, string candidate)
    {
        string? current = BackupScheduler.GetPassphrase(config);
        if (string.IsNullOrEmpty(current))
            return false;
        string normalized = config.GeneratedSecretMode
            ? GeneratedSecret.Normalize(candidate)
            : candidate;
        byte[] a = Encoding.UTF8.GetBytes(normalized);
        byte[] b = Encoding.UTF8.GetBytes(current);
        try
        {
            return a.Length == b.Length &&
                   CryptographicOperations.FixedTimeEquals(a, b);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(a);
            CryptographicOperations.ZeroMemory(b);
        }
    }
}
