using System.Text.RegularExpressions;

namespace FileKeep.Core.Security;

/// <summary>
/// Single scrub function for strings destined for service.log, startup
/// errors, or audit records. This is a shape-based backstop, not a
/// guarantee: code must still never log a secret deliberately. It
/// fails closed in one direction only — a false positive redacts a
/// benign token, a false negative never invents one.
/// </summary>
/// <remarks>
/// Rules, in order:
/// <list type="number">
/// <item>Exact occurrences of caller-supplied known values (length at
/// least 8, so short common substrings are never nuked) are replaced.
/// This is the only rule that catches short secrets; shape rules
/// cannot see those.</item>
/// <item>Maximal runs of exactly 52 characters from [A-Z2-7] (the
/// canonical generated-secret shape: 32 bytes as ungrouped uppercase
/// base32) are replaced.</item>
/// <item>Maximal runs of exactly 32 characters from [A-Z2-7] (the TOTP
/// secret shape: 20 bytes as base32) are replaced. Word boundaries
/// keep these from matching inside longer tokens, and the restricted
/// alphabet keeps uppercase hex GUIDs (which contain 0, 1, 8, 9)
/// readable.</item>
/// <item>Maximal base64 runs ([A-Za-z0-9+/] with optional trailing =)
/// of length 64 or more are replaced, except runs that are pure hex
/// (64-character lowercase/uppercase hex chunk IDs stay readable — a
/// DPAPI blob that is pure hex for 64+ characters is negligible).
/// This catches DPAPI blobs, which carry DPAPI overhead on top of the
/// plaintext and are long base64 in practice.</item>
/// </list>
/// File paths survive by construction: Windows paths break runs on
/// backslash and colon, Unix paths break on dots in filenames. A very
/// long dotless directory-only path could be redacted; that is a
/// cosmetic log blemish, accepted in the fail-closed direction.
/// </remarks>
public static partial class SecretScrubber
{
    private const string Redacted = "[REDACTED]";

    /// <summary>Scrubs secret-shaped content from <paramref name="input"/>.</summary>
    public static string? Scrub(string? input) => Scrub(input, Array.Empty<string?>());

    /// <summary>
    /// Scrubs secret-shaped content plus exact occurrences of
    /// <paramref name="knownSecrets"/>.
    /// </summary>
    public static string? Scrub(string? input, params string?[] knownSecrets)
    {
        if (string.IsNullOrEmpty(input))
            return input;
        string result = input;
        foreach (string? known in knownSecrets)
        {
            if (!string.IsNullOrEmpty(known) && known.Length >= 8)
                result = result.Replace(known, Redacted, StringComparison.Ordinal);
        }
        result = Base32SecretRegex().Replace(result, Redacted);
        result = TotpSecretRegex().Replace(result, Redacted);
        result = LongBase64Regex().Replace(result, m =>
            IsPureHex(m.Value) ? m.Value : Redacted);
        return result;
    }

    private static bool IsPureHex(string value)
    {
        foreach (char c in value)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
                return false;
        }
        return value.Length > 0;
    }

    [GeneratedRegex(@"\b[A-Z2-7]{52}\b", RegexOptions.None)]
    private static partial Regex Base32SecretRegex();

    [GeneratedRegex(@"\b[A-Z2-7]{32}\b", RegexOptions.None)]
    private static partial Regex TotpSecretRegex();

    [GeneratedRegex(@"[A-Za-z0-9+/]{64,}={0,2}", RegexOptions.None)]
    private static partial Regex LongBase64Regex();
}
