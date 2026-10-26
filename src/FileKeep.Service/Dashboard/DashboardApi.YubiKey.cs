using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Core.Nntp;
using FileKeep.Core.Security;
using FileKeep.Core.Service;

namespace FileKeep.Service;

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
        IYubiKeyProvider? yubiKeyProvider, Action<string>? log = null,
        string? proofPassphrase = null, bool proofYubiKey = false,
        string? proofTotp = null)
    {
        // Security fix (C-2): clearing or replacing the passphrase wipes
        // or replaces the whole unlock configuration — once a method is
        // enabled it requires a fresh proof of an existing method.
        var proofRefusal = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proofRefusal is not null)
            return proofRefusal.Value;

        if (!string.IsNullOrEmpty(passphrase) && config.GeneratedSecretMode)
            return (400, new ErrorDto("YubiKey-only mode is active: the repository secret was generated, not typed. Clear the generated secret before setting a passphrase."));
        // P2-C2: field mutations + save under the config lock. The
        // YubiKey branch's hardware interaction stays OUTSIDE the lock
        // (a touch wait must not block other handlers); its config
        // writes lock inside SetupYubiKeyOnConfig.
        if (string.IsNullOrEmpty(passphrase))
        {
            lock (config.JobsLock)
            {
                config.PassphraseProtected = null;
                config.PassphraseYubiKey = false;
                config.GeneratedSecretMode = false;
                config.YubiKeyChallenge = null;
                config.PassphraseYubiKeyWrapped = null;
                config.YubiKeySlotSecretProtected = null;
            }
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
            lock (config.JobsLock)
            {
                config.PassphraseProtected = Dpapi.Protect(passphrase);
                config.PassphraseYubiKey = false;
                config.YubiKeyChallenge = null;
                config.PassphraseYubiKeyWrapped = null;
            }
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
        // SF-16: if this slot already has a working setup, fetch its
        // slot secret so a failed re-setup can roll the key back to it
        // (YubiKeySetup.Setup re-programs it on failure). Without this,
        // a missed wrap touch silently stranded the key: the stored
        // config kept the old challenge/wrapped pair the reprogrammed
        // key could no longer answer.
        byte[]? previousSecret = null;
        if (config.PassphraseYubiKey &&
            !string.IsNullOrEmpty(config.YubiKeySlotSecretProtected) &&
            config.YubiKeySlot == slot)
        {
            try
            {
                previousSecret = Convert.FromBase64String(
                    Dpapi.Unprotect(config.YubiKeySlotSecretProtected));
            }
            catch { /* undecryptable: proceed without rollback protection */ }
        }
        try
        {
            var (challenge, wrapped, slotSecret) = YubiKeySetup.Setup(
                yubiKeyProvider, passphrase, slot, previousSecret);
            try
            {
                lock (config.JobsLock)
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
        finally
        {
            if (previousSecret is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(previousSecret);
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
        Action<string>? log = null,
        string? proofPassphrase = null, bool proofYubiKey = false,
        string? proofTotp = null)
    {
        // Security fix (C-1 class): linking (or re-linking) a key
        // re-programs the slot and re-wraps the secret — a security
        // change requiring proof once a method is enabled. In the
        // wizard's own sequence the passphrase step just saved the
        // passphrase, and the page sends it back as this proof.
        var proofRefusal = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proofRefusal is not null)
            return proofRefusal.Value;

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
        Action<string>? log = null,
        string? proofPassphrase = null, bool proofYubiKey = false,
        string? proofTotp = null)
    {
        // Security fix (C-1 class): gated like every security-config
        // change. In practice this operation only ever runs when NO
        // method is enabled (it refuses while one is configured), so
        // the gate is a no-op on its real path — it stays for
        // uniformity with the rest of the surface.
        var proofRefusal = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proofRefusal is not null)
            return proofRefusal.Value;

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
        // MU-15: no verifier exists for the typed secret — the session
        // starts UNVERIFIED and only a successful restore proves it.
        session.UnlockUnverified(canonical);
        log?.Invoke("dashboard: restore session unlocked via recovery secret (unverified until first restore)");
        return (200, new OkDto(true));
    }

    /// <summary>
    /// Unlocks the restore session with a phone authenticator code
    /// (MU-15): the profile reported the phone enabled, but no endpoint
    /// ever consulted TOTP, so the phone unlocked nothing. Mechanism: a
    /// verified code is a presence proof, and on the running system the
    /// service holds a DPAPI-protected copy of the passphrase — the
    /// proof authorizes releasing that copy into the session (so the
    /// session starts verified). Without the DPAPI copy there is
    /// nothing to release and the unlock honestly refuses. Only the
    /// LIVE secret is consulted (a pending re-enrollment must not
    /// unlock restores), through the shared guard (rate-limited,
    /// replay-protected). The phone stays an OR method — this path
    /// exists only while YubiKey gating is on, per the protection
    /// design (running-system-only, never the sole configured method:
    /// USB recovery needs Passphrase or YubiKey). Restore gating
    /// itself is unchanged: it keys off YubiKey mode.
    /// </summary>
    /// <summary>
    /// Opens a restore session by typing the repository passphrase.
    /// Passphrase mode without YubiKey only: in generated-secret mode the
    /// typed-secret endpoint owns typed unlocks (unverified), and in
    /// YubiKey mode the touch path owns them — a typed passphrase must
    /// not bypass the key. The candidate is verified against the current
    /// secret (constant-time), so unlike the typed-secret path the
    /// session starts verified. When no stored or environment passphrase
    /// exists there is nothing to check against and the unlock refuses,
    /// exactly like restores themselves in that state.
    /// </summary>
    /// <summary>
    /// Whether a passphrase has been added to a YubiKey-only profile
    /// (see <see cref="AddPassphraseWrap"/>). The wrap alone is the
    /// marker: a salt without a blob, or a blob without a salt, is
    /// not a usable added passphrase.
    /// </summary>
    private static bool HasPassphraseWrap(ServiceConfig config) =>
        !string.IsNullOrEmpty(config.PassphraseWrapProtected) &&
        !string.IsNullOrEmpty(config.PassphraseWrapSaltB64);

    /// <summary>
    /// Tries to unwrap the added-passphrase wrap with a typed
    /// candidate. Returns false (without distinguishing why) when
    /// there is no wrap, the candidate is empty, the salt is
    /// malformed, or the GCM tag does not verify — a wrong added
    /// passphrase and a missing wrap are the same answer to a caller.
    /// The derived KEK is zeroed before returning.
    /// </summary>
    private static bool TryUnwrapPassphraseWrap(ServiceConfig config, string? candidate, out string? secret)
    {
        secret = null;
        if (string.IsNullOrEmpty(candidate) || !HasPassphraseWrap(config))
            return false;
        int iterations = config.PassphraseWrapIterations > 0
            ? config.PassphraseWrapIterations
            : BackupRepository.DefaultKdfIterations;
        byte[] kek;
        try
        {
            kek = KeyDerivation.DeriveKey(candidate,
                Convert.FromBase64String(config.PassphraseWrapSaltB64!),
                iterations);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or ArgumentOutOfRangeException)
        {
            return false;
        }
        try
        {
            secret = YubiKeyCrypto.Unwrap(config.PassphraseWrapProtected!, kek);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Adds a passphrase to a YubiKey-only (generated-secret) profile.
    /// The passphrase does NOT replace the root secret: it wraps it —
    /// a PBKDF2-HMAC-SHA512 KEK (fresh 32-byte salt, the repository
    /// default iteration count) encrypts the generated secret with
    /// AES-256-GCM, reusing YubiKeyCrypto's wrap format. The added
    /// passphrase then unlocks restores and proves security-config
    /// changes, exactly like any other method, while the YubiKey touch
    /// and the recovery-sheet secret keep working unchanged. Adding
    /// requires fresh proof of an existing method (the shared gate).
    /// Removing the added passphrase is the profile toggle off, which
    /// clears the wrap. In passphrase mode this endpoint refuses —
    /// the passphrase there already is the repository secret, so it
    /// is changed from the passphrase settings, not added.
    /// </summary>
    public static (int Status, object Payload) AddPassphraseWrap(
        ServiceConfig config, string configPath, string? passphrase,
        IYubiKeyProvider? yubiKeyProvider, Action<string>? log = null,
        string? proofPassphrase = null, bool proofYubiKey = false, string? proofTotp = null)
    {
        var proof = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proof is not null)
            return proof.Value;
        if (!config.GeneratedSecretMode)
            return (400, new ErrorDto(
                "Adding a passphrase is only available in YubiKey-only mode. " +
                "In passphrase mode the passphrase already is the repository secret — " +
                "change it from the passphrase settings."));
        if (HasPassphraseWrap(config))
            return (400, new ErrorDto(
                "A passphrase is already added. Turn Passphrase off in the profile " +
                "to remove it before adding a different one."));
        if (string.IsNullOrEmpty(passphrase))
            return (400, new ErrorDto("A passphrase is required."));
        if (!BackupScheduler.TryGetPassphrase(config, out string? root, out string? problem) ||
            string.IsNullOrEmpty(root))
            return (400, new ErrorDto(problem ??
                "Adding a passphrase needs the repository secret on this PC (stored copy " +
                "or environment) — unlock with your YubiKey or recovery secret instead."));
        byte[] salt = KeyDerivation.NewSalt();
        int iterations = BackupRepository.DefaultKdfIterations;
        byte[] kek = KeyDerivation.DeriveKey(passphrase, salt, iterations);
        string blob;
        try { blob = YubiKeyCrypto.Wrap(root, kek); }
        finally { CryptographicOperations.ZeroMemory(kek); }
        lock (config.JobsLock)
        {
            config.PassphraseWrapProtected = blob;
            config.PassphraseWrapSaltB64 = Convert.ToBase64String(salt);
            config.PassphraseWrapIterations = iterations;
        }
        try { config.Save(configPath); }
        catch (Exception ex) { return (400, new ErrorDto(ex.Message)); }
        log?.Invoke("dashboard: passphrase added to YubiKey-only profile (wraps generated secret)");
        return (200, new OkDto(true));
    }

    public static (int Status, object Payload) UnlockRestoreSessionWithPassphrase(
        ServiceConfig config, RestoreSession session, string? passphrase,
        Action<string>? log = null)
    {
        if (config.GeneratedSecretMode)
        {
            // YubiKey-only mode with an added passphrase: the typed
            // passphrase unwraps the generated secret into the session.
            // Without a wrap there is no passphrase to type — the sheet
            // secret and the touch are the unlocks there.
            if (!HasPassphraseWrap(config))
                return (400, new ErrorDto(
                    "Typed-passphrase unlock is only available in passphrase mode without YubiKey, " +
                    "or in YubiKey-only mode with an added passphrase."));
            if (string.IsNullOrEmpty(passphrase) ||
                !TryUnwrapPassphraseWrap(config, passphrase, out string? root) ||
                string.IsNullOrEmpty(root))
                return (400, new ErrorDto("That passphrase doesn't match the added passphrase."));
            session.Unlock(root);
            log?.Invoke("dashboard: restore session unlocked via added passphrase");
            return (200, new OkDto(true));
        }
        if (config.PassphraseYubiKey)
            return (400, new ErrorDto(
                "Typed-passphrase unlock is only available in passphrase mode without YubiKey."));
        if (string.IsNullOrEmpty(passphrase) || !VerifyCurrentSecretProof(config, passphrase))
            return (400, new ErrorDto("That passphrase doesn't match the configured repository passphrase."));
        session.Unlock(passphrase);
        log?.Invoke("dashboard: restore session unlocked via passphrase");
        return (200, new OkDto(true));
    }

    /// <summary>
    /// Opens a restore session with a phone authenticator code. Works in
    /// any mode with the phone method enabled: the code releases the
    /// machine's stored (or environment) passphrase into the session,
    /// which is what gated restores then use. A code carries no key
    /// material, so without a resolvable stored passphrase there is
    /// nothing to release and the unlock refuses.
    /// </summary>
    public static (int Status, object Payload) UnlockRestoreSessionWithPhone(
        ServiceConfig config, RestoreSession session, string? code,
        Action<string>? log = null)
    {
        if (!config.PhoneAuthEnabled || string.IsNullOrWhiteSpace(config.PhoneTotpSecretProtected))
            return (400, new ErrorDto("Phone authenticator unlock isn't set up for this profile."));
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length != Security.TotpCrypto.CodeDigits)
            return (400, new ErrorDto("Enter the 6-digit code from your authenticator app."));
        if (!Security.TotpGuard.TryValidateLiveCode(config, code.Trim(), log))
            return (400, new ErrorDto("That code doesn't match — check the authenticator and try again."));
        if (!BackupScheduler.TryGetPassphrase(config, out string? passphrase, out string? problem))
            return (400, new ErrorDto(problem ??
                "Phone unlock needs the stored passphrase copy on this PC, which isn't " +
                "available — unlock with your YubiKey or recovery secret instead."));
        session.Unlock(passphrase);
        log?.Invoke("dashboard: restore session unlocked via phone authenticator");
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
        catch (Exception ex)
        {
            // SF-23: a provider failure (broken HID/driver state) must
            // not be indistinguishable from "no key plugged in" — the
            // setup UI would tell the user to insert a key that is
            // already there while the real problem stays invisible.
            return (500, new ErrorDto("YubiKey detection failed: " + ex.Message));
        }
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
        string configPath, IYubiKeyProvider? yubiKeyProvider, Action<string>? log = null,
        string? proofPassphrase = null, bool proofYubiKey = false,
        string? proofTotp = null)
    {
        // Security fix (C-1 class): programming a spare clones the slot
        // secret onto another key, so it takes proof once a method is
        // enabled — like the rest of the security-config surface.
        var proofRefusal = CheckSecurityChangeProof(config, yubiKeyProvider,
            proofPassphrase, proofYubiKey, log, proofTotp);
        if (proofRefusal is not null)
            return proofRefusal.Value;

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
    /// Reports the restore-session state — the restore view reads this one
    /// payload to decide which unlock affordances to render. Typed-secret
    /// unlock exists only in generated mode; typed-passphrase unlock only
    /// in passphrase mode without YubiKey; phone unlock whenever the
    /// phone method is enabled.
    /// </summary>
    public static (int Status, object Payload) GetRestoreSessionStatus(
        ServiceConfig config, RestoreSession session) =>
        (200, new YubiKeyUnlockDto(config.PassphraseYubiKey, session.IsUnlocked,
            config.GeneratedSecretMode, session.IsVerified, config.PhoneAuthEnabled,
            HasEnabledUnlockMethod(config),
            GetProtectionProfile(config).PassphraseEnabled,
            HasPassphraseWrap(config)));

    /// <summary>
    /// Resolves the passphrase for restore-side operations. When any
    /// unlock method is enabled, restores are gated behind the session:
    /// only the unlocked session passphrase is used (null when locked),
    /// so a logged-in caller without the holder's authorization cannot
    /// restore. When no method is enabled, the environment/DPAPI value
    /// is used as before. Backup-creation endpoints use
    /// <see cref="BackupScheduler.GetPassphrase"/> directly and never
    /// consult the session — scheduled and manual backups never ask.
    /// </summary>
    public static string? GetRestorePassphrase(ServiceConfig config, RestoreSession? session) =>
        HasEnabledUnlockMethod(config) ? session?.GetPassphrase() : BackupScheduler.GetPassphrase(config);

    /// <summary>
    /// The 401 message for a locked restore session, listing the unlock
    /// methods this profile actually offers — a passphrase-only profile
    /// must not be told to touch a YubiKey it never set up.
    /// </summary>
    public static string RestoreSessionLockedMessage(ServiceConfig config)
    {
        var profile = GetProtectionProfile(config);
        var parts = new List<string>();
        if (profile.YubiKeyEnabled) parts.Add("touch your YubiKey");
        if (profile.PassphraseEnabled) parts.Add("enter your passphrase");
        if (config.GeneratedSecretMode) parts.Add("enter your recovery secret");
        if (profile.PhoneEnabled) parts.Add("enter your authenticator code");
        return parts.Count == 0
            ? "Restore session is locked. Unlock it first."
            : "Restore session is locked. Unlock it first (" + string.Join(", ", parts) + ").";
    }

    /// <summary>
    /// The honest "no usable passphrase" error for dashboard operations
    /// (MU-12): when a stored passphrase exists but no longer decrypts,
    /// the error says so (and how to fix it) instead of claiming none
    /// is configured.
    /// </summary>
    public static ErrorDto PassphraseUnavailableError(ServiceConfig config, string fallback)
    {
        BackupScheduler.TryGetPassphrase(config, out _, out string? problem);
        return new ErrorDto(problem ?? fallback);
    }
}
