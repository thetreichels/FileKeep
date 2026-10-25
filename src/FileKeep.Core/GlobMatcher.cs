using System.Text.RegularExpressions;

namespace FileKeep.Core;

/// <summary>
/// Minimal glob matcher for selective restore. Supports '*'
/// (any run of non-separator chars), '**' (any run of chars, separators
/// included; '**/' also matches zero directories) and '?' (one
/// non-separator char). Patterns match manifest-relative paths, which
/// always use '/' separators.
/// </summary>
internal static class GlobMatcher
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> _cache = new();

    public static bool IsMatch(string path, string pattern, bool ignoreCase)
    {
        var regex = _cache.GetOrAdd((ignoreCase ? "i:" : "s:") + pattern,
            key => new Regex(ToRegex(pattern),
                RegexOptions.Compiled | RegexOptions.CultureInvariant |
                (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                TimeSpan.FromSeconds(5)));
        return regex.IsMatch(path);
    }

    private static string ToRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                    {
                        sb.Append("(.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i += 1;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}
