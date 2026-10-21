using QRCoder;
using UsenetBackup.Core;
using UsenetBackup.Core.Service;
using UsenetBackup.Service.Security;

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
            PassphraseEnabled = !config.GeneratedSecretMode && HasConfiguredPassphrase(config),
            YubiKeyEnabled = config.GeneratedSecretMode || config.PassphraseYubiKey,
            PhoneEnabled = config.PhoneAuthEnabled,
            YubiKeyOnly = config.GeneratedSecretMode,
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
    /// In generated-secret (YubiKey-only) mode there is no user
    /// passphrase, so the passphrase rules do not apply; YubiKey
    /// enablement is accepted only when the setup data produced by
    /// generated-secret setup is actually present.
    /// </summary>
    public static (int Status, object Payload) SetProtectionProfile(ServiceConfig config,
        string configPath, ProtectionProfileRequest request, Action<string>? log = null)
    {
        string linkMode = (request.LinkMode ?? "or").Trim().ToLowerInvariant();
        if (linkMode is not ("or" or "and"))
            return (400, new ErrorDto("linkMode must be \"or\" or \"and\"."));

        if (config.GeneratedSecretMode)
        {
            bool setupPresent = !string.IsNullOrEmpty(config.YubiKeyChallenge) &&
                                !string.IsNullOrEmpty(config.PassphraseYubiKeyWrapped);
            if (request.YubiKeyEnabled && !setupPresent)
                return (400, new ErrorDto("YubiKey is not set up. Complete YubiKey setup first."));
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

    /// <summary>
    /// Starts phone-authenticator enrollment: generates a fresh 160-bit TOTP
    /// secret, stores it DPAPI-protected, and returns the otpauth URI, the
    /// grouped base32 secret for manual entry, and a QR-code SVG for
    /// scanning. The phone method is NOT enabled by this call — the wizard
    /// must verify a code first, then save the profile with PhoneEnabled.
    /// </summary>
    public static (int Status, object Payload) SetupPhoneTotp(ServiceConfig config,
        string configPath, Action<string>? log = null)
    {
        try
        {
            byte[] secret = TotpCrypto.GenerateSecret();
            string base32;
            try
            {
                base32 = TotpCrypto.ToBase32(secret);
                string protected32 = UsenetBackup.Core.Dpapi.Protect(base32);
                config.PhoneTotpSecretProtected = protected32;
                config.Save(configPath);
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

            log?.Invoke("dashboard: phone authenticator enrollment started (new TOTP secret)");
            return (200, new PhoneSetupDto(uri, grouped, qrSvg));
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Verifies a 6-digit TOTP code against the enrolled secret (RFC 6238,
    /// ±1 step skew). Returns (200, ok) — the caller enables the phone
    /// method via <c>SetProtectionProfile</c> after a successful verify.
    /// </summary>
    public static (int Status, object Payload) VerifyPhoneTotp(ServiceConfig config, string? code)
    {
        if (string.IsNullOrWhiteSpace(config.PhoneTotpSecretProtected))
            return (400, new ErrorDto("No phone authenticator secret is enrolled. Start setup first."));
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length != TotpCrypto.CodeDigits)
            return (200, new OkDto(false));
        try
        {
            string base32 = UsenetBackup.Core.Dpapi.Unprotect(config.PhoneTotpSecretProtected);
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
            bool ok;
            try
            {
                ok = TotpCrypto.ValidateCode(secret, code, DateTimeOffset.UtcNow);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            }
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
