using System.Runtime.InteropServices;
using System.Text;
using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Source-path canonicalization (security review rec 4): duplicate and
/// nesting detection must survive traversal spellings, alternate
/// separators, symlinks/junctions, and Windows 8.3 short paths — not
/// just lexically identical strings.
/// </summary>
public sealed class PathCanonicalizationTests : IDisposable
{
    private readonly string _root;

    public PathCanonicalizationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fk-canon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static BackupJobConfig Job(params string[] sources) =>
        new() { Name = "job", Sources = sources.ToList() };

    private static void AssertInvalidSource(BackupJobConfig job, string fragment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceConfig.ValidateNoNestedSources(job));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void TraversalDotDot_SameFolder_RejectedAsDuplicate()
    {
        string a = Path.Combine(_root, "a");
        Directory.CreateDirectory(a);
        AssertInvalidSource(Job(a, Path.Combine(a, "sub", "..")), "twice");
    }

    [Fact]
    public void TrailingSeparatorAndDot_SameFolder_RejectedAsDuplicate()
    {
        string a = Path.Combine(_root, "a");
        Directory.CreateDirectory(a);
        AssertInvalidSource(Job(a + Path.DirectorySeparatorChar, Path.Combine(a, ".")), "twice");
    }

    [Fact]
    public void NestedViaTraversal_RejectedAsNested()
    {
        string a = Path.Combine(_root, "a");
        Directory.CreateDirectory(a);
        AssertInvalidSource(Job(a, Path.Combine(a, "sub", "..", "sub2")), "inside");
    }

    [Fact]
    public void DistinctSiblings_Accepted()
    {
        string a = Path.Combine(_root, "a");
        string b = Path.Combine(_root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        ServiceConfig.ValidateNoNestedSources(Job(a, b));
    }

    [Fact]
    public void NonexistentPaths_FallBackToLexicalCheck()
    {
        // Nothing exists, so no link resolution is possible — the
        // lexical check must still catch nesting, and distinct paths
        // must still pass.
        string missing = Path.Combine(_root, "missing");
        AssertInvalidSource(Job(missing, Path.Combine(missing, "sub")), "inside");
        ServiceConfig.ValidateNoNestedSources(Job(
            Path.Combine(_root, "missing-a"), Path.Combine(_root, "missing-b")));
    }

    [Fact]
    public void InvalidPath_Rejected()
    {
        AssertInvalidSource(Job("a\0b"), "invalid source path");
    }

    [Fact]
    public void AlternateSeparators_SameFolder_RejectedAsDuplicate()
    {
        // On Windows, '/' is an alternate separator and GetFullPath
        // normalizes it; on Linux a backslash is a filename character,
        // so the two spellings are genuinely different paths there.
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Alternate separators are Windows-only.");
        string a = Path.Combine(_root, "a");
        Directory.CreateDirectory(a);
        string slashForm = a.Replace('\\', '/');
        AssertInvalidSource(Job(a, slashForm), "twice");
    }

    [Fact]
    public void SymlinkAlias_SameFolder_RejectedAsDuplicate()
    {
        SkipIfNoSymlinkPrivilege();
        string real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        string link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);
        AssertInvalidSource(Job(real, link), "twice");
    }

    [Fact]
    public void SymlinkNested_RejectedAsNested()
    {
        SkipIfNoSymlinkPrivilege();
        string real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        string link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);
        AssertInvalidSource(Job(real, Path.Combine(link, "sub")), "inside");
    }

    [Fact]
    public void ShortPathAlias_SameFolder_RejectedAsDuplicate()
    {
        // Windows-only: 8.3 short names do not exist on Linux, and
        // GetLongPathName is a kernel32 import. If short names are
        // disabled on the machine, GetShortPathName returns its input
        // unchanged and there is nothing to prove — skip honestly
        // rather than asserting a trivially identical pair.
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("8.3 short paths are Windows-only.");
        string longDir = Path.Combine(_root, "verylongdirectoryname");
        Directory.CreateDirectory(longDir);
        string shortDir = GetShortPathName(longDir);
        if (string.Equals(shortDir, longDir, StringComparison.OrdinalIgnoreCase))
            throw Xunit.Sdk.SkipException.ForSkip("8.3 short names are disabled on this machine.");
        AssertInvalidSource(Job(longDir, shortDir), "twice");
    }

    private static void SkipIfNoSymlinkPrivilege()
    {
        string probeDir = Path.Combine(Path.GetTempPath(), "fk-canon-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(probeDir);
            Directory.CreateSymbolicLink(Path.Combine(probeDir, "link"), probeDir);
        }
        catch (UnauthorizedAccessException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Creating symlinks requires privilege on this machine.");
        }
        finally
        {
            try { Directory.Delete(probeDir, recursive: true); } catch { }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(
        string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    private static string GetShortPathName(string path)
    {
        uint needed = GetShortPathName(path, null!, 0);
        if (needed == 0)
            return path;
        var sb = new StringBuilder((int)needed);
        uint written = GetShortPathName(path, sb, needed);
        return written == 0 ? path : sb.ToString();
    }
}
