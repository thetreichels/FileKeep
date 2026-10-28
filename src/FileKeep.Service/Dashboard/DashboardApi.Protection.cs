using QRCoder;
using FileKeep.Core;
using FileKeep.Core.Security;
using FileKeep.Core.Service;
using FileKeep.Service.Security;

namespace FileKeep.Service;

/// <summary>
/// DashboardApi — protection profile (Passphrase / YubiKey / Phone):
/// reporting the current profile, saving it with the wizard's rules, and
/// registering extra YubiKey labels. The Phone method is a standalone
/// fallback: it is never AND-linkable, it is running-system-only
/// (never used in USB recovery), and it can only ever be a secondary
/// method — at least one of Passphrase or YubiKey must stay configured,
/// because USB recovery needs one of them. As a proof in the Windows
/// app, though, a TOTP code stands alone like any other proof (see the
/// security gate, 2026-10-25).
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
        SecretResolver.HasSecret(BackupScheduler.PassphraseEnvVar, config.PassphraseProtected);

    /// <summary>
    /// Builds the current protection profile for the dashboard wizard.
    /// In generated-secret (YubiKey-only) mode the profile reports
    /// Passphrase off and YubiKey on regardless of the stored copy: the
    /// generated secret occupies the passphrase slot internally, but it
    /// is not a user passphrase and must never be reported as one.
    /// </summary>
    public static ProtectionProfileResponse GetProtectionProfile(ServiceConfig config) =>
        new()
        {
            PassphraseEnabled = (!config.GeneratedSecretMode && HasConfiguredPassphrase(config)) ||
                                HasPassphraseWrap(config),
            YubiKeyEnabled = config.GeneratedSecretMode || config.PassphraseYubiKey,
            PhoneEnabled = config.PhoneAuthEnabled,
            YubiKeyOnly = config.GeneratedSecretMode,
            LinkMode = string.IsNullOrEmpty(config.ProtectionLinkMode)
                ? "or" : config.ProtectionLinkMode,
            YubiKeys = SnapshotYubiKeys(config),
        };

    // P2-C2: YubiKeys is a live list other handlers mutate; copying it
    // unlocked could throw mid-enumeration (a 500 on a plain GET).
    private static List<string> SnapshotYubiKeys(ServiceConfig config)
    {
        lock (config.JobsLock)
            return new List<string>(config.YubiKeys);
    }

    /// <summary>
    /// Saves the protection profile from the wizard. The Passphrase toggle
    /// reflects reality: it cannot be turned on without a configured
    /// passphrase, and a configured passphrase cannot be switched off here
    /// (clearing a passphrase is destructive and stays on the passphrase
    /// settings). Enabling YubiKey requires YubiKey setup to be complete
    /// (programmed via the passphrase settings with a physical touch);
    /// disabling it only flips the mode flag so setup data survives.
    /// In generated-secret (YubiKey-only) mode there is no user
    /// passphrase, so the passphrase rules do not apply; YubiKey
    /// enablement is accepted only when the setup data produced by
    /// generated-secret setup is actually present.
    /// </summary>
    public static (int Status, object Payload) SetProtectionProfile(ServiceConfig config,
        string configPath, ProtectionProfileRequest request, Action<string>? log = null,
        IYubiKeyProvider? yubiKeyProvider = null)
    {
        string linkMode = (request.LinkMode ?? "or").Trim().ToLowerInvariant();
        if (linkMode is not ("or" or "and"))
            return (400, new ErrorDto("linkMode must be \"or\" or \"and\"."));

        // Security fix (C-1): once any unlock method is enabled, saving
        // the profile — e.g. flipping the YubiKey gate off — requires a
        // fresh proof of an existing method carried by this request.
        // Outside the config lock: a touch proof waits on hardware.
        var proofRefusal = CheckSecurityChangeProof(config, yubiKeyProvider,
            request.ProofPassphrase, request.ProofYubiKey, log, request.ProofTotp);
        if (proofRefusal is not null)
            return proofRefusal.Value;

        // P2-C2: profile validation reads config fields that other
        // handlers mutate; the whole validate → mutate → save sequence
        // holds the config lock so the saved profile is a state one
        // request actually asked for.
        lock (config.JobsLock)
        {
        if (config.GeneratedSecretMode)
        {
            bool setupPresent = !string.IsNullOrEmpty(config.YubiKeyChallenge) &&
                                !string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped);
            if (request.YubiKeyEnabled && !setupPresent)
                return (400, new ErrorDto("YubiKey is not set up. Complete YubiKey setup first."));
            // In YubiKey-only mode the passphrase toggle maps to the
            // added-passphrase wrap, not to a configured passphrase:
            // on requires a wrap to exist (added via the add endpoint),
            // off clears it. The YubiKey and the recovery-sheet secret
            // keep working either way; only this method goes.
            if (request.PassphraseEnabled && !HasPassphraseWrap(config))
                return (400, new ErrorDto("No passphrase is added. Add a passphrase first — it wraps the generated secret."));
            if (!request.PassphraseEnabled && HasPassphraseWrap(config))
            {
                config.PassphraseWrapProtected = null;
                config.PassphraseWrapSaltB64 = null;
                config.PassphraseWrapIterations = 0;
            }
        }
        else
        {
            bool passphraseConfigured = HasConfiguredPassphrase(config);
            if (request.PassphraseEnabled && !passphraseConfigured)
                return (400, new ErrorDto("No passphrase is configured. Set a repository passphrase first."));
            if (!request.PassphraseEnabled && passphraseConfigured)
                return (400, new ErrorDto("A passphrase is configured. Clear it from the passphrase settings before turning Passphrase off."));
            if (request.YubiKeyEnabled && !config.PassphraseYubiKey)
                return (400, new ErrorDto("YubiKey is not set up. Complete YubiKey setup first."));
        }
        if (linkMode == "and" && (!request.PassphraseEnabled || !request.YubiKeyEnabled))
            return (400, new ErrorDto("\"and\" link mode requires both Passphrase and YubiKey to be enabled."));
        // The phone authenticator can only ever be a SECONDARY method:
        // USB recovery cannot use TOTP, so at least one of Passphrase
        // or YubiKey must always stay configured, or recovery would be
        // stranded with no usable method. This is a configuration rule
        // only — as a proof in the Windows app, a TOTP code stands
        // alone like any other proof (see the security gate).
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
            AuditLog.Record(log, AuditLog.SecurityMode, AuditLog.Ok,
                $"passphrase={request.PassphraseEnabled} yubikey={request.YubiKeyEnabled} " +
                $"phone={request.PhoneEnabled} link={linkMode}");
            return (200, GetProtectionProfile(config));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
        }
    }

    /// <summary>
    /// Registers a display name for another YubiKey ("Add another YubiKey"
    /// button). Labels only — the actual key programming is done with the
    /// same slot secret through <c>/api/yubikey/program-spare</c>, so any
    /// registered key works interchangeably.
    /// </summary>
    public static (int Status, object Payload) AddYubiKeyName(ServiceConfig config,
        string configPath, string? name, Action<string>? log = null,
        string? proofPassphrase = null, bool proofYubiKey = false,
        IYubiKeyProvider? yubiKeyProvider = null, string? proofTotp = null)
    {
        // Security fix (C-1 class): registering a key label changes the
        // unlock-method set, so it takes the same proof as any other
        // security-config change once a method is enabled.
        var proofRefusal = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proofRefusal is not null)
            return proofRefusal.Value;

        string clean = (name ?? "").Trim();
        if (clean.Length == 0)
            return (400, new ErrorDto("A YubiKey name is required."));
        if (clean.Length > MaxYubiKeyNameLength)
            return (400, new ErrorDto($"YubiKey name is too long (max {MaxYubiKeyNameLength} characters)." ));
        lock (config.JobsLock)
        {
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

    /// <summary>
    /// Starts phone-authenticator enrollment: generates a fresh 160-bit TOTP
    /// secret, stages it DPAPI-protected as the PENDING secret, and returns
    /// the otpauth URI, the grouped base32 secret for manual entry, and a
    /// QR-code SVG for scanning. The phone method is NOT enabled by this
    /// call — the wizard must verify a code first, then save the profile
    /// with PhoneEnabled. SF-17: the live secret is deliberately NOT
    /// touched here — re-enrollment used to overwrite it immediately, so
    /// abandoning the wizard silently invalidated the authenticator that
    /// already worked. Only a verified code promotes the pending secret
    /// (see <see cref="VerifyPhoneTotp"/>).
    /// </summary>
    public static (int Status, object Payload) SetupPhoneTotp(ServiceConfig config,
        string configPath, Action<string>? log = null,
        string? proofPassphrase = null, bool proofYubiKey = false,
        IYubiKeyProvider? yubiKeyProvider = null, string? proofTotp = null)
    {
        // Security fix (C-3): starting a phone enrollment hands the
        // caller a fresh TOTP secret; once a method is enabled that is
        // only allowed with proof of an existing method, or anyone
        // could enroll their own phone and unlock with it.
        var proofRefusal = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proofRefusal is not null)
            return proofRefusal.Value;

        try
        {
            byte[] secret = TotpCrypto.GenerateSecret();
            string base32;
            try
            {
                base32 = TotpCrypto.ToBase32(secret);
                string protected32 = FileKeep.Core.Dpapi.Protect(base32);
                lock (config.JobsLock)
                {
                    config.PendingPhoneTotpSecretProtected = protected32;
                    config.Save(configPath);
                }
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            }

            string uri = TotpCrypto.BuildOtpAuthUri(base32);
            string qrSvg;
            using (var gen = new QRCodeGenerator())
            using (var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M))
                qrSvg = QrMatrixToSvg(data.ModuleMatrix, pixelsPerModule: 6);

            // Group the secret for readability: "JBSW Y3DP EHPK 3PXP".
            string grouped = string.Join(" ",
                Enumerable.Range(0, (base32.Length + 3) / 4)
                    .Select(i => base32.Substring(i * 4,
                        Math.Min(4, base32.Length - i * 4))));

            log?.Invoke("dashboard: phone authenticator enrollment started (new TOTP secret staged, pending verification)");
            return (200, new PhoneSetupDto(uri, grouped, qrSvg));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Verifies a 6-digit TOTP code (RFC 6238, ±1 step skew). When an
    /// enrollment is in progress the code is checked against the PENDING
    /// secret, and success promotes it to the live secret (SF-17) —
    /// persisted immediately when <paramref name="configPath"/> is
    /// supplied. With no enrollment pending, the code is checked against
    /// the live secret. Returns (200, ok) — the caller enables the phone
    /// method via <c>SetProtectionProfile</c> after a successful verify.
    /// </summary>
    public static (int Status, object Payload) VerifyPhoneTotp(ServiceConfig config, string? code,
        string? configPath = null, Action<string>? log = null,
        string? proofPassphrase = null, bool proofYubiKey = false,
        IYubiKeyProvider? yubiKeyProvider = null, string? proofTotp = null)
    {
        // Security fix (C-3): a verified code PROMOTES the pending
        // secret to live — the step that completes a takeover — so it
        // takes the same proof as setup. Note the TOTP code itself is
        // the enrollment artifact being verified, never the proof: a
        // code from the just-issued secret proves nothing about an
        // existing method.
        var proofRefusal = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proofRefusal is not null)
            return proofRefusal.Value;

        bool hasPending = !string.IsNullOrWhiteSpace(config.PendingPhoneTotpSecretProtected);
        string? target = hasPending
            ? config.PendingPhoneTotpSecretProtected
            : config.PhoneTotpSecretProtected;
        if (string.IsNullOrWhiteSpace(target))
            return (400, new ErrorDto("No phone authenticator secret is enrolled. Start setup first."));
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length != TotpCrypto.CodeDigits)
            return (200, new OkDto(false));
        // Every code check runs through the guard's limiter (see
        // TotpGuard): enrollment verification is a code check too, and
        // an attacker guessing enrollment codes must hit the same wall
        // as one guessing proofs or unlocks. A lockout answers like any
        // wrong code — plain false, no lockout state leaked to the page.
        if (TotpGuard.IsLockedOut)
        {
            log?.Invoke("dashboard: phone code refused — TOTP checks are locked out after failed attempts");
            AuditLog.Record(log, AuditLog.AuthFailure, AuditLog.Fail, "proof=totp-lockout");
            return (200, new OkDto(false));
        }
        try
        {
            string base32 = FileKeep.Core.Dpapi.Unprotect(target);
            byte[] secret;
            try
            {
                secret = TotpCrypto.FromBase32(base32);
            }
            finally
            {
                // Don't leave the base32 lying around longer than needed.
                base32 = new string('\0', base32.Length);
            }
            long? matchedStep;
            try
            {
                matchedStep = TotpCrypto.MatchStep(secret, code, DateTimeOffset.UtcNow);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            }
            bool ok = matchedStep is not null;
            if (!ok)
            {
                TotpGuard.ReportFailure(log);
                AuditLog.Record(log, AuditLog.AuthFailure, AuditLog.Fail, "proof=totp");
                return (200, new OkDto(false));
            }
            if (!hasPending && !TotpGuard.AcceptStep(matchedStep.Value))
            {
                // Live-secret replay: a code already used for a proof
                // or unlock cannot verify again inside its window.
                TotpGuard.ReportFailure(log);
                log?.Invoke("dashboard: phone code refused — that code was already used");
                AuditLog.Record(log, AuditLog.AuthFailure, AuditLog.Fail, "proof=totp-replay");
                return (200, new OkDto(false));
            }
            if (ok && hasPending)
            {
                // SF-17: verified — the staged secret becomes the live
                // one. Until this moment the previously enrolled
                // authenticator kept working, and a failed or abandoned
                // enrollment never touched it.
                // P2-C2: promotion + save under the config lock (with
                // P2-R3's rollback on save failure inside it).
                lock (config.JobsLock)
                {
                string? previousLive = config.PhoneTotpSecretProtected;
                string? pending = config.PendingPhoneTotpSecretProtected;
                config.PhoneTotpSecretProtected = pending;
                config.PendingPhoneTotpSecretProtected = null;
                if (configPath is not null)
                {
                    try { config.Save(configPath); }
                    catch (Exception ex)
                    {
                        // P2-R3: roll the promotion back. Leaving it in
                        // memory while the disk kept the old secret
                        // diverged the two permanently: a retry verified
                        // against the NEW secret, reported success, and
                        // never re-saved — so the next restart silently
                        // reverted to the old authenticator.
                        config.PhoneTotpSecretProtected = previousLive;
                        config.PendingPhoneTotpSecretProtected = pending;
                        return (400, new ErrorDto(
                            "The code was correct, but the new phone secret could not be saved: " + ex.Message));
                    }
                }
                log?.Invoke("dashboard: phone authenticator secret promoted after verified enrollment");
                AuditLog.Record(log, AuditLog.SecurityMode, AuditLog.Ok, "phone-added=true");
                }
                // Promotion succeeded: seed the replay record with the
                // enrollment code's step, so that code cannot then be
                // replayed as a proof or unlock inside its window.
                TotpGuard.AcceptStep(matchedStep.Value);
            }
            TotpGuard.ReportSuccess();
            return (200, new OkDto(ok));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Renders a QR module matrix as a standalone SVG (white background,
    /// black modules, 4-module quiet zone). Built by hand so we don't depend
    /// on QRCoder's renderer classes, which aren't in every target build.
    /// </summary>
    private static string QrMatrixToSvg(System.Collections.Generic.List<System.Collections.BitArray> matrix, int pixelsPerModule)
    {
        int modules = matrix.Count;
        const int quiet = 4;
        int total = modules + quiet * 2;
        int size = total * pixelsPerModule;
        var sb = new System.Text.StringBuilder(size * 4);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" ");
        sb.Append($"viewBox=\"0 0 {size} {size}\" width=\"{size}\" height=\"{size}\">");
        sb.Append($"<rect width=\"{size}\" height=\"{size}\" fill=\"#ffffff\"/>");
        sb.Append("<path fill=\"#000000\" d=\"");
        for (int y = 0; y < modules; y++)
        {
            var row = matrix[y];
            for (int x = 0; x < modules; x++)
            {
                if (row[x])
                {
                    int px = (x + quiet) * pixelsPerModule;
                    int py = (y + quiet) * pixelsPerModule;
                    sb.Append($"M{px},{py}h{pixelsPerModule}v{pixelsPerModule}h-{pixelsPerModule}Z");
                }
            }
        }
        sb.Append("\"/></svg>");
        return sb.ToString();
    }
}
