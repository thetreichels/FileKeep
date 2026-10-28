using System.Text.RegularExpressions;

namespace FileKeep.Service;

/// <summary>
/// Sanitizes startup-error text before it reaches service.log or
/// stderr (security review rec 3). Exception messages are useful
/// precisely because they name paths and failure kinds — those are
/// kept — but a message can also sweep up secret-shaped material
/// (a DPAPI blob, a generated secret, a token) if some subsystem
/// interpolates it, and service.log lives in a machine-wide
/// directory. Sanitize therefore redacts, pattern-based:
/// <list type="bullet">
/// <item><description>long base64-alphabet runs (32+ characters):
/// DPAPI blobs, generated secrets, TOTP secrets, and long tokens
/// are all continuous runs in that alphabet; ordinary words, paths
/// (separators break runs), and GUIDs (hyphens break runs) are
/// shorter or broken, so they survive;</description></item>
/// <item><description>values after secret-named keys
/// (<c>password=</c>, <c>secret:</c>, …): the key is kept, the
/// value is replaced.</description></item>
/// </list>
/// Deliberate limits, documented here rather than hidden: an
/// arbitrary typed passphrase cannot be recognized by pattern, so
/// it is never safe to interpolate user secrets into exception
/// messages at all — no call site in this codebase does, and this
/// sanitizer is the backstop for blob/token-shaped material, not a
/// license to log secrets. A single-token filename of 32+ characters
/// will also be redacted (it is indistinguishable from a token);
/// the surrounding directory still identifies the file.
/// </summary>
public static partial class StartupErrorSanitizer
{
    /// <summary>Minimum length of a base64-alphabet run treated as secret-shaped.</summary>
    public const int MinSecretRunLength = 32;

    // A continuous run of base64-alphabet characters, with optional
    // padding. Greedy: a longer run is one token, not several.
    [GeneratedRegex("[A-Za-z0-9+/]{32,}={0,2}", RegexOptions.Compiled)]
    private static partial Regex SecretRun();

    // password/passphrase/secret/token/api-key, then ':' or '=', then
    // a quoted or unquoted value. The key and separator are preserved.
    [GeneratedRegex("(?i)(password|passphrase|secret|totp|api[_-]?key)(\\s*[:=]\\s*)(\"[^\"]*\"|[^\\s,;]+)", RegexOptions.Compiled)]
    private static partial Regex SecretKeyValue();

    /// <summary>
    /// Returns <paramref name="message"/> with secret-shaped content
    /// replaced by <c>[redacted]</c>. Null or empty input passes
    /// through as an empty string; everything else is preserved.
    /// </summary>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return "";
        string scrubbed = SecretKeyValue().Replace(message, "$1$2[redacted]");
        return SecretRun().Replace(scrubbed, "[redacted]");
    }
}
