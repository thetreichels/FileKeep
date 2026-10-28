using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Log-file restriction (security review rec 3). The Windows ACL
/// branch cannot be proven in the Linux sandbox and is covered by
/// laptop validation; the Unix branch and the best-effort contract
/// are proven here.
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
    public void MissingFile_NoThrow()
    {
        // Best-effort contract: a missing file is left alone, never
        // an exception (startup must not fail over log hardening).
        LogFileSecurity.RestrictLogFileBestEffort(Path.Combine(_dir, "does-not-exist.log"));
    }
}
