using System.Security.Cryptography;
using System.Text;

namespace FileKeep.Core.Security;

/// <summary>
/// The generated repository secret used by passphrase-free (YubiKey-only)
/// mode: 32 random bytes, base32-encoded. The canonical form — ungrouped
/// uppercase base32 — is the single string that is wrapped by the
/// YubiKey, DPAPI-stored for unattended backups, and fed to the
/// repository KDF: internally it occupies the passphrase slot, so all
/// existing passphrase machinery works on it verbatim. The grouped
/// display form exists only on the recovery sheet, and
/// <see cref="Normalize"/> maps anything a human types back (grouped,
/// lowercase, hyphenated) to the canonical form.
/// </summary>
public static class GeneratedSecret
{
    /// <summary>Entropy of a generated secret, in bytes (256-bit).</summary>
    public const int SecretLengthBytes = 32;

    /// <summary>Length of the canonical string: 32 bytes → 52 base32 characters, padding stripped.</summary>
    public const int CanonicalLength = 52;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Generates a fresh secret in canonical form.</summary>
    public static string Generate()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(SecretLengthBytes);
        try { return ToBase32(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>
    /// Groups a canonical secret for display on the recovery sheet:
    /// blocks of four characters separated by single spaces
    /// ("ABCD EFGH …"). Display only — this form is never stored and
    /// never fed to the KDF.
    /// </summary>
    public static string FormatForDisplay(string canonical)
    {
        if (string.IsNullOrEmpty(canonical)) return "";
        var sb = new StringBuilder(canonical.Length + canonical.Length / 4);
        for (int i = 0; i < canonical.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append(' ');
            sb.Append(canonical[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Maps a typed-back secret to canonical form: removes spaces,
    /// hyphens and any other whitespace and uppercases, so the grouped
    /// sheet form, a lowercase copy, or a hyphenated copy all match the
    /// canonical string.
    /// </summary>
    public static string Normalize(string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return "";
        var sb = new StringBuilder(typed.Length);
        foreach (char c in typed)
        {
            if (c == '-' || char.IsWhiteSpace(c)) continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="value"/> is exactly a canonical secret:
    /// <see cref="CanonicalLength"/> characters, all in the base32
    /// alphabet (uppercase letters and digits 2–7). Used to decide
    /// whether a failed typed value is plausibly a mangled recovery-sheet
    /// secret worth one normalized retry — anything else (an ordinary
    /// passphrase, a wrong-length string) is not.
    /// </summary>
    public static bool IsCanonicalForm(string? value)
    {
        if (value is null || value.Length != CanonicalLength) return false;
        foreach (char c in value)
            if (Base32Alphabet.IndexOf(c) < 0) return false;
        return true;
    }

    /// <summary>RFC 4648 base32 encoding, uppercase, padding stripped.</summary>
    private static string ToBase32(byte[] bytes)
    {
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }
}
