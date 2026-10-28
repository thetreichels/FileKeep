using System.Runtime.InteropServices;
using System.Text;

namespace FileKeep.Core.Service;

/// <summary>
/// Canonicalizes backup source paths for duplicate/nesting comparison
/// (see <see cref="ServiceConfig.ValidateNoNestedSources"/>).
///
/// The lexical step (<see cref="Path.GetFullPath"/>) resolves
/// <c>.</c>, <c>..</c>, and alternate separators, but it is blind to
/// filesystem aliases: a symlink or junction pointing at an already
/// listed folder, and a Windows 8.3 short path naming the same
/// directory, would both slip past a lexical comparison and back the
/// same files up twice (or nest a tree inside itself). Canonicalize
/// therefore resolves, best-effort and in order:
/// <list type="bullet">
/// <item><description>symlinks, junctions, and other reparse points
/// via <see cref="File.ResolveLinkTarget"/> (final target);</description></item>
/// <item><description>Windows 8.3 short-path components via
/// <c>GetLongPathName</c> (Windows only).</description></item>
/// </list>
/// Any step that cannot run (the path does not exist yet, the
/// platform does not support it, a native call fails) falls back to
/// the previous step's result instead of failing: a source that does
/// not exist yet is still caught by the lexical check, and
/// validation must never fail a load because an optional resolution
/// was unavailable. Resolution applies only to the source-nesting
/// check this helper serves — not to repo paths or destinations.
/// </summary>
public static class PathCanonicalization
{
    /// <summary>
    /// Returns the canonical form of <paramref name="path"/> for
    /// duplicate/nesting comparison: fully qualified, link- and
    /// short-path-resolved where possible, with trailing separators
    /// trimmed. Throws only when even the lexical step fails (an
    /// invalid path); resolution failures fall back silently.
    /// </summary>
    public static string Canonicalize(string path)
    {
        string lexical = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string resolved = ResolveLinkTargetBestEffort(lexical);
        string expanded = ExpandShortPathBestEffort(resolved);
        return expanded.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveLinkTargetBestEffort(string path)
    {
        // ResolveLinkTarget throws when the path does not exist, so
        // resolve the longest EXISTING ancestor and re-append the
        // remainder lexically: a symlinked parent with a
        // not-yet-created leaf (the common backup-source shape) must
        // still resolve, or a nested-via-symlink source would slip
        // past as a merely lexical path.
        try
        {
            string current = path;
            var suffix = new Stack<string>();
            while (!File.Exists(current) && !Directory.Exists(current))
            {
                string? parent = Path.GetDirectoryName(current);
                if (parent is null || parent == current)
                    return path;
                suffix.Push(Path.GetFileName(current));
                current = parent;
            }
            // Null when the ancestor is not itself a link; the
            // FullName is fully resolved (intermediate links
            // included) when a target exists.
            string resolved = File.ResolveLinkTarget(current, returnFinalTarget: true)?.FullName ?? current;
            foreach (string part in suffix)
                resolved = Path.Combine(resolved, part);
            return Path.GetFullPath(resolved);
        }
        catch
        {
            return path;
        }
    }

    private static string ExpandShortPathBestEffort(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;
        try
        {
            // Two-call pattern: the first call with a zero buffer
            // returns the required size (0 = the path has no short
            // form, or does not exist — either way, keep it as-is).
            uint needed = GetLongPathName(path, null, 0);
            if (needed == 0)
                return path;
            var sb = new StringBuilder((int)needed);
            uint written = GetLongPathName(path, sb, needed);
            return written == 0 ? path : sb.ToString();
        }
        catch
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(
        string lpszShortPath, StringBuilder? lpszLongPath, uint cchBuffer);
}
