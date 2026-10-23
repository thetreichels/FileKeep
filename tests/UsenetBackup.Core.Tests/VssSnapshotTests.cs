using UsenetBackup.Core.Recovery;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Regression tests for the VSS backup-protocol finalization.
/// These cover two real bugs found by fresh-eyes review:
///  1. Dispose() set _disposed before calling Abort(), so SignalAndWait
///     threw ObjectDisposedException (swallowed by Abort's catch-all) and
///     the helper never received the abort signal — writers left dangling.
///  2. The helper returned exit 0 when BackupComplete failed, so managed
///     Complete() reported success while writer finalization had failed.
///
/// The tests use a batch-file stub helper (Windows-only) that speaks the
/// helper protocol: print a fake snapshot path, read one stdin line, record
/// it to a file, exit with a configured code. Configuration travels via
/// environment variables inherited by the child process.
/// </summary>
public sealed class VssSnapshotTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _stubPath;
    private readonly string _signalFile;

    public VssSnapshotTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "vss-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _stubPath = Path.Combine(_workDir, "fake-vss-helper.bat");
        _signalFile = Path.Combine(_workDir, "signal.txt");

        // Stub helper protocol:
        //   stdout: fake snapshot path (first line, as the real helper does)
        //   stdin:  one signal line ("complete" or "abort")
        //   side effect: writes the received signal to the signal file
        //   exit code: from VSS_TEST_EXIT_CODE
        File.WriteAllText(_stubPath,
            "@echo off\r\n" +
            "echo \\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy999\r\n" +
            "set /p SIGNAL=\r\n" +
            "echo %SIGNAL%>\"%VSS_TEST_SIGNAL_FILE%\"\r\n" +
            "exit /b %VSS_TEST_EXIT_CODE%\r\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VSS_TEST_SIGNAL_FILE", null);
        Environment.SetEnvironmentVariable("VSS_TEST_EXIT_CODE", null);
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private void ConfigureStub(int exitCode)
    {
        Environment.SetEnvironmentVariable("VSS_TEST_SIGNAL_FILE", _signalFile);
        Environment.SetEnvironmentVariable("VSS_TEST_EXIT_CODE", exitCode.ToString());
        if (File.Exists(_signalFile))
            File.Delete(_signalFile);
    }

    private string ReadSignal()
    {
        // The stub writes the signal file just before exiting; allow a moment.
        for (int i = 0; i < 50 && !File.Exists(_signalFile); i++)
            Thread.Sleep(100);
        Assert.True(File.Exists(_signalFile), "Stub helper did not record a signal.");
        return File.ReadAllText(_signalFile).Trim();
    }

    [Fact]
    public void Dispose_WithoutComplete_SendsAbort()
    {
        // Regression test for bug 1: Dispose() must signal "abort" to the
        // helper when Complete() was never called. Previously _disposed was
        // set first, causing SignalAndWait to throw (swallowed), so the
        // helper never got the signal and VSS writers were left dangling.
        if (!OperatingSystem.IsWindows())
            return; // Stub is a Windows batch file.

        ConfigureStub(exitCode: 3);

        using (var snapshot = new VssSnapshot(_stubPath, timeoutSecs: 30))
        {
            snapshot.Create("C:", "C:\\test");
            Assert.NotNull(snapshot.SnapshotPath);
            // No Complete() — Dispose must fail-safe to abort.
        }

        Assert.Equal("abort", ReadSignal());
    }

    [Fact]
    public void Complete_SendsCompleteSignal()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ConfigureStub(exitCode: 0);

        using (var snapshot = new VssSnapshot(_stubPath, timeoutSecs: 30))
        {
            snapshot.Create("C:", "C:\\test");
            snapshot.Complete(); // Must not throw on exit 0.
        }

        Assert.Equal("complete", ReadSignal());
    }

    private string WriteStub(string name, string windowsBody, string unixBody)
    {
        string path = Path.Combine(_workDir, name);
        if (OperatingSystem.IsWindows())
        {
            // The NS-10 stubs were named without an extension
            // ("fail-stub"); Windows cannot execute an extensionless
            // batch file (Win32Exception at Process.Start). The class's
            // own .bat stub works — match it.
            path += ".bat";
            File.WriteAllText(path, windowsBody);
        }
        else
        {
            File.WriteAllText(path, "#!/bin/sh\n" + unixBody);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    [Fact]
    public void Create_HelperThatNeverReports_TimesOutAndIsStopped()
    {
        // NS-10: a hung helper used to block Create() forever on a
        // synchronous ReadLine, and the process leaked. Now the wait is
        // bounded and the helper is killed.
        string stub = WriteStub("hang-stub",
            "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n",
            "exec sleep 60\n");
        using var snapshot = new VssSnapshot(stub, timeoutSecs: 30, createTimeoutSecs: 2);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            snapshot.Create("C:", _workDir));
        sw.Stop();
        Assert.Contains("did not report a snapshot", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"Create took {sw.Elapsed}; the hung helper was not bounded/killed.");
        // The instance is not poisoned: a retry fails the same honest way,
        // not with "Snapshot already created."
        var ex2 = Assert.Throws<InvalidOperationException>(() =>
            snapshot.Create("C:", _workDir));
        Assert.Contains("did not report a snapshot", ex2.Message);
    }

    [Fact]
    public void Create_HelperFloodingStderr_StillReadsSnapshotPath()
    {
        // NS-10: stderr is drained concurrently; a helper that writes
        // more than a pipe buffer of stderr BEFORE the path must not
        // deadlock Create.
        string flood = OperatingSystem.IsWindows()
            ? string.Join("", Enumerable.Repeat("echo floodfloodfloodfloodfloodfloodfloodflood 1>&2\r\n", 3000))
            : string.Join("", Enumerable.Repeat("echo floodfloodfloodfloodfloodfloodfloodflood >&2\n", 3000));
        string stub = WriteStub("flood-stub",
            "@echo off\r\n" + flood +
            "echo \\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy999\r\n" +
            "set /p SIGNAL=\r\nexit /b 0\r\n",
            flood +
            "echo '\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy999'\n" +
            "read SIGNAL\nexit 0\n");
        using var snapshot = new VssSnapshot(stub, timeoutSecs: 30, createTimeoutSecs: 20);
        snapshot.Create("C:", _workDir);
        Assert.NotNull(snapshot.SnapshotPath);
        snapshot.Complete();
    }

    [Fact]
    public void Create_HelperFailsWithStderr_MessageCarriesErrorAndExitCode()
    {
        // NS-10: the failure branch used to ReadToEnd stderr (blocking),
        // ignore WaitForExit's result, and then throw from ExitCode —
        // masking the real error. Now: stderr text + exit code, and the
        // process is cleaned up.
        string stub = WriteStub("fail-stub",
            "@echo off\r\necho VSS_E_FAIL: no writers 1>&2\r\nexit /b 7\r\n",
            "echo 'VSS_E_FAIL: no writers' >&2\nexit 7\n");
        using var snapshot = new VssSnapshot(stub, timeoutSecs: 30, createTimeoutSecs: 20);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            snapshot.Create("C:", _workDir));
        Assert.Contains("VSS_E_FAIL", ex.Message);
        Assert.Contains("7", ex.Message);
    }

    [Fact]
    public void Complete_ThrowsWhenHelperExitsNonZero()
    {
        // Regression test for bug 2: when the helper reports failure
        // (e.g. exit 4 = BackupComplete failed), Complete() must throw
        // rather than silently reporting success.
        if (!OperatingSystem.IsWindows())
            return;

        ConfigureStub(exitCode: 4);

        using (var snapshot = new VssSnapshot(_stubPath, timeoutSecs: 30))
        {
            snapshot.Create("C:", "C:\\test");
            var ex = Assert.Throws<InvalidOperationException>(() => snapshot.Complete());
            Assert.Contains("4", ex.Message);
        }

        Assert.Equal("complete", ReadSignal());
    }

}
