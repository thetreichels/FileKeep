using System.Security.AccessControl;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Log-file restriction (security review rec 3). The Windows ACL
/// branch is covered by a Windows-only test below (laptop
/// validation); the Unix branch and the best-effort contract are
/// proven here.
/// </summary>
public sealed class LogFileSecurityTests : IDisposable
{
    private readonly string _dir;

    public LogFileSecurityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fk-logsec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void UnixFile_RestrictedToOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Unix file modes do not apply on Windows (ACL branch needs laptop validation).");
        string path = Path.Combine(_dir, "service.log");
        File.WriteAllText(path, "line\n");
        LogFileSecurity.RestrictLogFileBestEffort(path);
        UnixFileMode mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void WindowsFile_RestrictedToSystemAndAdmins()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("ACL branch needs Windows (laptop validation).");
        string path = Path.Combine(_dir, "service.log");
        File.WriteAllText(path, "line\n");
        LogFileSecurity.RestrictLogFileBestEffort(path);
        var security = new FileInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
            targetType: typeof(System.Security.Principal.NTAccount));
        // Protected DACL: exactly the two explicit rules, no
        // inherited rules surviving.
        Assert.Equal(2, rules.Count);
        var byIdentity = rules.Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .ToDictionary(r => r.IdentityReference.Value, r => r);
        var system = byIdentity[@"NT AUTHORITY\SYSTEM"];
        Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, system.AccessControlType);
        Assert.Equal(System.Security.AccessControl.FileSystemRights.FullControl, system.FileSystemRights);
        var admins = byIdentity[@"BUILTIN\Administrators"];
        Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, admins.AccessControlType);
        Assert.Equal(System.Security.AccessControl.FileSystemRights.FullControl, admins.FileSystemRights);
    }

    [Fact]
    public void MissingFile_NoThrow()
    {
        // Best-effort contract: a missing file is left alone, never
        // an exception (startup must not fail over log hardening).
        LogFileSecurity.RestrictLogFileBestEffort(Path.Combine(_dir, "does-not-exist.log"));
    }
}
