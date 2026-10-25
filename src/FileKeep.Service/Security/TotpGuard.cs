using System.Security.Cryptography;
using FileKeep.Core;
using FileKeep.Core.Service;

namespace FileKeep.Service.Security;

/// <summary>
/// Single choke point for every TOTP code check in the service: the
/// security-config proof gate, the phone restore unlock, and phone
/// enrollment verification. Two hardenings live here and nowhere else,
/// because they became load-bearing the day TOTP became a full proof
/// (2026-10-25): before that day a code only unlocked a restore session
/// the caller already needed a stored passphrase copy for, and no code
/// check stood between an attacker and the security config.
///
/// 1. Rate limiting. A 6-digit code with ±1 step skew leaves 3 valid
///    codes per window out of 1,000,000; against an unauthenticated
///    loopback API with no limit, that is brute-forceable in well under
///    a day at modest request rates. After
///    <see cref="MaxConsecutiveFailures"/> consecutive failures, all
///    TOTP checks refuse for <see cref="LockoutDuration"/>; a success
///    resets the counter. The lockout gates TOTP checks only — passphrase
///    and YubiKey proofs keep working through it, so it cannot be used
///    to lock the user out of security settings entirely.
/// 2. Replay protection. A code observed once (shoulder, capture) must
///    not authorize a second operation inside its window. The last
///    accepted time step is remembered, and a code from that step or
///    earlier is refused. Consequence the user accepted with the change:
///    one code authorizes one proof — a second gated change inside the
///    same 30-second window needs the next code.
///
/// State is in-memory and per service process: a service restart resets
/// the counter and the replay record. That is the standard trade-off
/// against persisting lockout state (which would let anyone lock the
/// user out permanently by failing codes on purpose); a restart is a
/// privileged local operation, not something the loopback attacker has.
///
/// The live secret only, never a pending re-enrollment secret: a code
/// from a just-issued secret proves nothing about an existing method
/// (the SF-17 rule). Enrollment verification checks the pending secret
/// through the rate-limit primitives directly and seeds the replay
/// record on promotion, so the enrollment code itself cannot then be
/// replayed as a proof.
/// </summary>
public static class TotpGuard
{
    /// <summary>Consecutive failures before the lockout trips.</summary>
    public const int MaxConsecutiveFailures = 5;

    /// <summary>How long TOTP checks refuse once the lockout trips.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private static readonly object _sync = new();
    private static int _consecutiveFailures;
    private static DateTimeOffset? _lockedUntil;
    private static long _lastAcceptedStep = -1;

    /// <summary>
    /// Test hook: overrides DPAPI unprotection of the stored secret.
    /// Null means production behavior (<c>Dpapi.Unprotect</c>). Tests
    /// only — never set outside the test assembly (the sandbox has no
    /// DPAPI, so tests run through this hook with the blob itself as
    /// the base32 secret). Internal: the test assembly is a friend
    /// (see the Service csproj's InternalsVisibleTo).
    /// </summary>
    internal static Func<string, string>? UnprotectForTests { get; set; }

    /// <summary>Resets the limiter and replay record. Tests only.</summary>
    internal static void ResetForTests()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _lockedUntil = null;
            _lastAcceptedStep = -1;
        }
    }

    /// <summary>True while the lockout is active. An expired lockout clears itself.</summary>
    public static bool IsLockedOut
    {
        get
        {
            lock (_sync)
            {
                if (_lockedUntil is not null && _lockedUntil <= DateTimeOffset.UtcNow)
                {
                    _lockedUntil = null;
                    _consecutiveFailures = 0;
                }
                return _lockedUntil is not null;
            }
        }
    }

    /// <summary>Records a failed code check; trips the lockout at the threshold.</summary>
    public static void ReportFailure(Action<string>? log = null)
    {
        lock (_sync)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= MaxConsecutiveFailures && _lockedUntil is null)
            {
                _lockedUntil = DateTimeOffset.UtcNow + LockoutDuration;
                log?.Invoke("dashboard: TOTP checks locked out for " +
                    $"{LockoutDuration.TotalMinutes} minutes after {MaxConsecutiveFailures} failed codes");
            }
        }
    }

    /// <summary>Records a successful code check; clears failures and any lockout.</summary>
    public static void ReportSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _lockedUntil = null;
        }
    }

    /// <summary>
    /// Records an accepted time step for replay protection (used by
    /// enrollment promotion to seed the record with the enrollment
    /// code's step). Returns false when the step was already accepted.
    /// </summary>
    public static bool AcceptStep(long step)
    {
        lock (_sync)
        {
            if (step <= _lastAcceptedStep)
                return false;
            _lastAcceptedStep = step;
            return true;
        }
    }

    /// <summary>
    /// Validates a code against the LIVE phone secret only. Returns false
    /// (without counting a failure) when no phone method is configured,
    /// the secret cannot be read (DPAPI is machine+user bound — a profile
    /// restored onto another PC or user matches NO code, the P2-R4 rule,
    /// instead of throwing a bare 500), or the code is malformed; counts
    /// a failure when a well-formed code simply does not match, or when
    /// it replays an already-accepted step.
    /// </summary>
    public static bool TryValidateLiveCode(ServiceConfig config, string? code,
        Action<string>? log = null)
    {
        if (IsLockedOut)
        {
            log?.Invoke("dashboard: TOTP code refused — checks are locked out after failed attempts");
            return false;
        }
        if (config.PhoneAuthEnabled is false ||
            string.IsNullOrWhiteSpace(config.PhoneTotpSecretProtected) ||
            string.IsNullOrWhiteSpace(code))
            return false;

        string base32;
        try
        {
            base32 = UnprotectForTests is not null
                ? UnprotectForTests(config.PhoneTotpSecretProtected!)
                : Dpapi.Unprotect(config.PhoneTotpSecretProtected!);
        }
        catch
        {
            return false;
        }

        byte[] secret;
        try { secret = TotpCrypto.FromBase32(base32); }
        finally { base32 = new string('\0', base32.Length); }
        try
        {
            long? step = TotpCrypto.MatchStep(secret, code, DateTimeOffset.UtcNow);
            if (step is null)
            {
                ReportFailure(log);
                return false;
            }
            if (!AcceptStep(step.Value))
            {
                // A replayed code is a failed attempt, not a match.
                ReportFailure(log);
                log?.Invoke("dashboard: TOTP code refused — that code was already used");
                return false;
            }
            ReportSuccess();
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
