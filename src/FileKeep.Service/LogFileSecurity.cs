using System.Security.AccessControl;
using System.Security.Principal;

namespace FileKeep.Service;

/// <summary>
/// Restricts log files created by the service (security review
/// rec 3). service.log and the startup-error fallback file live in
/// machine-wide locations, and their default ACLs let any local user
/// read them — including the audit records added for security
/// events. Restriction is file-scoped, not directory-scoped: the
/// ProgramData directory also holds the offline-folders catalog and
/// job state, whose access must not change.
/// <para>
/// What is implemented here: on Unix, the file mode is set to 0600
/// (owner read/write). On Windows, a protected DACL is applied via
/// the System.IO.FileSystem.AccessControl package (<c>
/// File.SetAccessControl</c> is not in the net10.0 shared
/// framework): SYSTEM and Administrators get FullControl, no
/// inherited rules survive. SYSTEM must stay on the DACL — the
/// service runs as LocalSystem and writes the log itself; a DACL
/// without it would lock the service out of its own log. The
/// package API is cross-platform, so this compiles on Linux too;
/// the branch only executes on Windows (runtime guard below), and
/// the DACL itself is proven by the Windows-only test on the
/// laptop, not in the sandbox.
/// </para>
/// Best-effort throughout: any failure is swallowed — a log file
/// that cannot be restricted must still be writable, never a
/// startup failure.
/// </summary>
public static class LogFileSecurity
{
    /// <summary>
    /// Restricts <paramref name="path"/> as described above. The
    /// file must already exist; a missing file is left alone.
    /// Never throws.
    /// </summary>
    public static void RestrictLogFileBestEffort(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            if (OperatingSystem.IsWindows())
            {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(
                    isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    new NTAccount(@"NT AUTHORITY\SYSTEM"),
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    new NTAccount(@"BUILTIN\Administrators"),
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
                return;
            }
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort: see the class doc comment.
        }
    }
}
