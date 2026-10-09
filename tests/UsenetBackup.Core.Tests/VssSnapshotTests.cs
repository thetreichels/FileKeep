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
            snapshot.Create("C:");
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
            snapshot.Create("C:");
            snapshot.Complete(); // Must not throw on exit 0.
        }

        Assert.Equal("complete", ReadSignal());
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
            snapshot.Create("C:");
            var ex = Assert.Throws<InvalidOperationException>(() => snapshot.Complete());
            Assert.Contains("4", ex.Message);
        }

        Assert.Equal("complete", ReadSignal());
    }

    [Fact]
    public void Abort_SendsAbortExplicitly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ConfigureStub(exitCode: 3);

        using (var snapshot = new VssSnapshot(_stubPath, timeoutSecs: 30))
        {
            snapshot.Create("C:");
            snapshot.Abort(); // Explicit abort; Dispose afterwards is a no-op.
        }

        Assert.Equal("abort", ReadSignal());
    }

    [Fact]
    public void Dispose_AfterComplete_DoesNotSignalAgain()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ConfigureStub(exitCode: 0);

        using (var snapshot = new VssSnapshot(_stubPath, timeoutSecs: 30))
        {
            snapshot.Create("C:");
            snapshot.Complete();
        }

        // Only one signal should have been recorded (from Complete()).
        Assert.Equal("complete", ReadSignal());
    }
}
