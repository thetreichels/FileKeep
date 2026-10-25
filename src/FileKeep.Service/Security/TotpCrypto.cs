using System.Security.Cryptography;
using System.Text;

namespace FileKeep.Service.Security;

/// <summary>
/// RFC 6238 TOTP (time-based one-time passwords) for the phone
/// authenticator method. HMAC-SHA1, 30-second time steps, 6-digit codes.
/// The phone method is running-system-only and OR-only — it never gates
/// USB recovery and is never AND-linked — and it can only ever be a
/// secondary method: at least one of Passphrase or YubiKey must stay
/// configured, because USB recovery needs one of them (enforced in
/// <c>DashboardApi.SetProtectionProfile</c>). As a proof in the Windows
/// app, though, a TOTP code stands alone like passphrase and YubiKey
/// since 2026-10-25 (Joel's order).
/// </summary>
public static class TotpCrypto
{
    /// <summary>TOTP time step in seconds (RFC 6238 default).</summary>
    public const int TimeStepSeconds = 30;

    /// <summary>Number of digits in the displayed code.</summary>
    public const int CodeDigits = 6;

    /// <summary>Recommended secret length in bytes (160 bits).</summary>
    public const int SecretLength = 20;

    /// <summary>Clock-skew tolerance: codes from ± this many steps are accepted.</summary>
    public const int AllowedSkewSteps = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Generates a fresh cryptographically random TOTP secret.</summary>
    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    /// <summary>Base32-encodes a secret (no padding), as used in otpauth URIs.</summary>
    public static string ToBase32(byte[] secret)
    {
        if (secret is null || secret.Length == 0)
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        var sb = new StringBuilder((secret.Length * 8 + 4) / 5);
        int buffer = secret[0];
        int bitsLeft = 8;
        int index = 1;
        while (bitsLeft > 0 || index < secret.Length)
        {
            if (bitsLeft < 5)
            {
                if (index < secret.Length)
                {
                    buffer = (buffer << 8) | secret[index++];
                    bitsLeft += 8;
                }
                else
                {
                    buffer <<= (5 - bitsLeft);
                    bitsLeft = 5;
                }
            }
            sb.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
            bitsLeft -= 5;
        }
        return sb.ToString();
    }

    /// <summary>Decodes a base32 secret (padding optional, case-insensitive).</summary>
    public static byte[] FromBase32(string base32)
    {
        if (string.IsNullOrWhiteSpace(base32))
            throw new ArgumentException("Secret must not be empty.", nameof(base32));
        string clean = base32.Trim().TrimEnd('=').ToUpperInvariant();
        var result = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0;
        int bitsLeft = 0;
        foreach (char c in clean)
        {
            int val = Base32Alphabet.IndexOf(c);
            if (val < 0)
                throw new ArgumentException($"Invalid base32 character '{c}'.", nameof(base32));
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                result.Add((byte)(buffer >> (bitsLeft - 8)));
                bitsLeft -= 8;
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Builds the <c>otpauth://</c> URI for QR-code enrollment.
    /// </summary>
    public static string BuildOtpAuthUri(string base32Secret, string accountName = "FileKeep")
    {
        if (string.IsNullOrWhiteSpace(base32Secret))
            throw new ArgumentException("Secret must not be empty.", nameof(base32Secret));
        string label = Uri.EscapeDataString("FileKeep:" + accountName);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer=FileKeep" +
               $"&digits={CodeDigits}&period={TimeStepSeconds}";
    }

    /// <summary>
    /// Validates a 6-digit code against the secret at <paramref name="now"/>,
    /// accepting ±<see cref="AllowedSkewSteps"/> time steps for clock skew.
    /// Comparison is constant-time; intermediate codes are wiped.
    /// </summary>
    public static bool ValidateCode(byte[] secret, string code, DateTimeOffset now) =>
        MatchStep(secret, code, now) is not null;

    /// <summary>
    /// Like <see cref="ValidateCode"/>, but returns the matched time step
    /// instead of a boolean, so callers doing replay protection can tell
    /// a fresh code from one already used (see <see cref="TotpGuard"/>).
    /// Returns null when nothing matches. Scans from the earliest
    /// accepted step upward, so a skewed (older-step) code reports its
    /// own step, not the current one.
    /// </summary>
    public static long? MatchStep(byte[] secret, string code, DateTimeOffset now)
    {
        if (secret is null || secret.Length == 0 || string.IsNullOrWhiteSpace(code))
            return null;
        string clean = code.Trim();
        if (clean.Length != CodeDigits || !clean.All(char.IsDigit))
            return null;

        long step = now.ToUnixTimeSeconds() / TimeStepSeconds;
        for (long s = step - AllowedSkewSteps; s <= step + AllowedSkewSteps; s++)
        {
            string expected = ComputeCode(secret, s);
            bool match = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(clean));
            if (match)
                return s;
        }
        return null;
    }

    /// <summary>
    /// Computes the 6-digit TOTP code for a secret at a given time step
    /// (RFC 4226 dynamic truncation over HMAC-SHA1).
    /// </summary>
    public static string ComputeCode(byte[] secret, long timestep)
    {
        if (secret is null || secret.Length == 0)
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        byte[] counter = new byte[8];
        long t = timestep;
        for (int i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(t & 0xFF);
            t >>= 8;
        }
        byte[] hash;
        using (var hmac = new HMACSHA1(secret))
            hash = hmac.ComputeHash(counter);
        try
        {
            int offset = hash[hash.Length - 1] & 0x0F;
            int binary = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);
            int otp = binary % (int)Math.Pow(10, CodeDigits);
            return otp.ToString($"D{CodeDigits}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(counter);
        }
    }
}
