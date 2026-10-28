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
/// (owner read/write). On Windows, managed ACL application needs
/// the System.IO.FileSystem.AccessControl package
/// (<c>File.SetAccessControl</c> is not in the net10.0 shared
/// framework), and that package is deliberately NOT added by this
/// change — it would be a new runtime dependency for one helper,
/// and it cannot be restored or verified from the Linux sandbox.
/// The Windows branch is therefore a no-op today, flagged for a
/// parent decision (add the package, or accept default ACLs on
/// Windows log files). Do not reintroduce a P/Invoke ACL writer
/// here without laptop validation: a wrong DACL could lock the
/// service out of its own log.
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
                // No-op today: see the class doc comment (AccessControl
                // package not referenced). Best-effort by design.
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
