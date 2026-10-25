using System.Diagnostics;

namespace FileKeep.Core.Recovery;

/// <summary>
/// Manages a VSS shadow copy via the native FileKeepVss.exe helper.
/// Implements <see cref="FileKeep.Core.ISnapshotProvider"/> for the "vss" provider.
///
/// The helper is a separate process so the managed backup engine never
/// touches COM. If the helper exits non-zero, the snapshot is considered
/// failed and the caller must fail closed (no fallback to live copy).
///
/// VSS backup protocol: the engine MUST call <see cref="Complete"/> after
/// successfully copying data from the snapshot, or <see cref="Abort"/> on
/// failure. These map to the helper's BackupComplete / AbortBackup calls,
/// which finalize the VSS writer session. If neither is called before
/// disposal, <see cref="Dispose"/> sends abort (fail-safe): an
/// un-finalized writer session leaves writers (SQL Server, etc.) dangling
/// with un-truncated logs.
///
/// Usage:
/// <code>
/// using var vss = new VssSnapshot(helperPath);
/// vss.Create("C:", sourceDir);   // throws on failure — never falls back to live
/// var manifest = repo.BackupDirectory(sourceDir, vss);  // reads via SnapshotRoot
/// vss.Complete();                // BackupComplete + delete snapshot
/// </code>
/// </summary>
public sealed class VssSnapshot : FileKeep.Core.ISnapshotProvider
{
    private readonly string _helperPath;
    private readonly int _timeoutSecs;
    private readonly TimeSpan _createWaitTimeout;
    private Process? _process;
    private Task<string>? _stderrTask;
    private bool _finalized;
    private bool _disposed;
    private string? _sourceDir;

    /// <param name="helperPath">Full path to FileKeepVss.exe.</param>
    /// <param name="timeoutSecs">Seconds to hold the snapshot.</param>
    /// <param name="createTimeoutSecs">Seconds to wait for the helper to
    /// report its snapshot before giving up and stopping it (NS-10).</param>
    public VssSnapshot(string helperPath, int timeoutSecs = 3600, int createTimeoutSecs = 120)
    {
        _helperPath = helperPath ?? throw new ArgumentNullException(nameof(helperPath));
        _timeoutSecs = timeoutSecs;
        _createWaitTimeout = TimeSpan.FromSeconds(createTimeoutSecs);
    }

    /// <summary>
    /// The snapshot device path (e.g., \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3).
    /// Valid only after <see cref="Create"/> succeeds and before
    /// <see cref="Complete"/>/<see cref="Abort"/>/<see cref="Dispose"/>.
    /// </summary>
    public string? SnapshotPath { get; private set; }

    /// <inheritdoc/>
    public string ProviderId => "vss";

    /// <summary>
    /// Creates the shadow copy. Throws on failure — never returns a
    /// live path as a fallback.
    /// </summary>
    /// <param name="volume">Volume to snapshot (e.g., "C:").</param>
    /// <param name="sourceDir">
    /// Source directory being backed up. Stored so <see cref="SnapshotRoot"/>
    /// can return the snapshot-equivalent path for the backup engine.
    /// </param>
    public void Create(string volume, string sourceDir)
    {
        if (_process is not null)
            throw new InvalidOperationException("Snapshot already created.");
        ArgumentException.ThrowIfNullOrEmpty(sourceDir);

        if (!File.Exists(_helperPath))
            throw new FileNotFoundException(
                $"VSS helper not found: {_helperPath}. " +
                "VSS snapshot requires FileKeepVss.exe.", _helperPath);

        var psi = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // ArgumentList avoids quoting pitfalls (a "C:\" volume root would
        // mangle CommandLineToArgvW if quoted in an Arguments string).
        psi.ArgumentList.Add("--volume");
        psi.ArgumentList.Add(volume);
        psi.ArgumentList.Add("--timeout");
        psi.ArgumentList.Add(_timeoutSecs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start VSS helper.");

        // NS-10: drain stderr CONCURRENTLY — a helper that fills its
        // stderr pipe before printing the path used to deadlock the
        // blocking stdout read below. And bound the wait for the path:
        // a hung helper used to block Create forever and then leak.
        var stderrTask = _process.StandardError.ReadToEndAsync();
        // P2-R6: keep the drain reachable for SignalAndWait — it used
        // to be a Create-local, orphaned on success, and the Complete
        // path's synchronous ReadToEnd then collided with this async
        // reader and lost the helper's error text exactly on failure.
        _stderrTask = stderrTask;
        string? path;
        try
        {
            path = _process.StandardOutput.ReadLineAsync()
                .WaitAsync(_createWaitTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            // Stop the helper first: stderr reaches EOF once it dies, so
            // the drain below returns what it printed instead of waiting
            // out its full grace period against a live process.
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
            catch { /* best effort */ }
            string err = DrainStderr(stderrTask);
            AbandonProcess();
            throw new InvalidOperationException(
                $"VSS helper did not report a snapshot within {_createWaitTimeout.TotalSeconds:0} " +
                $"seconds and was stopped. {err.Trim()}".Trim());
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _process.WaitForExit(5000);
            int? exitCode = _process.HasExited ? _process.ExitCode : null;
            string err = DrainStderr(stderrTask);
            AbandonProcess();
            throw new InvalidOperationException(
                $"VSS helper failed to create snapshot: {err.Trim()} " +
                $"(exit code {(exitCode?.ToString() ?? "unknown — helper had to be stopped")})");
        }

        // Check if the process already exited (indicates failure after
        // printing something unexpected)
        if (_process.HasExited && _process.ExitCode != 0)
        {
            int exitCode = _process.ExitCode;
            string err = DrainStderr(stderrTask);
            AbandonProcess();
            throw new InvalidOperationException(
                $"VSS helper exited with code {exitCode}: {err.Trim()}");
        }

        SnapshotPath = path.Trim();
        _sourceDir = Path.GetFullPath(sourceDir);
    }

    private static string DrainStderr(Task<string> stderrTask)
    {
        try { return stderrTask.Wait(TimeSpan.FromSeconds(5)) ? stderrTask.Result : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Kills (if still running) and disposes a helper whose Create failed,
    /// and clears <see cref="_process"/> so the instance is not poisoned
    /// with a zombie "already created" state (NS-10).
    /// </summary>
    private void AbandonProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
        try { _process?.WaitForExit(5000); } catch { /* best effort */ }
        try { _process?.Dispose(); } catch { /* best effort */ }
        _process = null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The readable root for the backup engine: the source directory as
    /// seen through the shadow copy. Valid only after <see cref="Create"/>
    /// succeeds.
    /// </remarks>
    public string SnapshotRoot =>
        SnapshotPath is null || _sourceDir is null
            ? throw new InvalidOperationException("Snapshot not created.")
            : TranslatePath(_sourceDir);

    /// <inheritdoc/>
    public bool IsSnapshot => true;

    /// <inheritdoc/>
    public string Name => "vss";

    /// <inheritdoc/>
    /// <remarks>
    /// Files inside a shadow copy are ordinary readable files; no special
    /// access is needed beyond what the snapshot itself provides.
    /// </remarks>
    public FileStream OpenRead(string fullPath) => File.OpenRead(fullPath);

    /// <summary>
    /// Signals successful backup completion. The helper calls VSS
    /// BackupComplete (finalizing the writer session), deletes the
    /// snapshot, and exits 0. Throws if the helper reports failure.
    /// </summary>
    public void Complete()
    {
        SignalAndWait("complete", expectSuccess: true);
    }

    /// <summary>
    /// Signals backup failure/abandonment. The helper calls VSS
    /// AbortBackup (releasing writers), deletes the snapshot, and exits.
    /// Best-effort: does not throw on helper errors since the caller is
    /// already handling a failure.
    /// </summary>
    public void Abort()
    {
        try
        {
            SignalAndWait("abort", expectSuccess: false);
        }
        catch
        {
            // Best effort — the backup already failed; don't mask it.
        }
    }

    private void SignalAndWait(string signal, bool expectSuccess)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VssSnapshot));
        if (_finalized)
            return;
        _finalized = true;

        Process? proc = _process;
        _process = null;
        SnapshotPath = null;

        if (proc is null)
            return;

        try
        {
            if (!proc.HasExited)
            {
                try
                {
                    proc.StandardInput.WriteLine(signal);
                    proc.StandardInput.Close();
                }
                catch
                {
                    // Pipe broken — helper already gone.
                }

                if (!proc.WaitForExit(30_000))
                {
                    try { proc.Kill(); } catch { /* already gone */ }
                    throw new TimeoutException(
                        "VSS helper did not exit within 30 seconds of " +
                        $"the '{signal}' signal.");
                }

                if (expectSuccess && proc.ExitCode != 0)
                {
                    // P2-R6: read the text from Create's async drain —
                    // a synchronous ReadToEnd here collides with that
                    // reader and throws, swallowing the helper's error.
                    string err = _stderrTask is not null
                        ? DrainStderr(_stderrTask)
                        : string.Empty;
                    throw new InvalidOperationException(
                        $"VSS helper failed on '{signal}': {err.Trim()} " +
                        $"(exit code {proc.ExitCode})");
                }
            }
        }
        finally
        {
            proc.Dispose();
        }
    }

    /// <summary>
    /// Translates a volume-relative path to its snapshot equivalent.
    /// E.g., C:\Windows\System32 → \\?\GLOBALROOT\...\HarddiskVolumeShadowCopy3\Windows\System32
    /// </summary>
    public string TranslatePath(string volumePath)
    {
        if (SnapshotPath is null)
            throw new InvalidOperationException("Snapshot not created.");

        // volumePath like "C:\Windows" → strip "C:" prefix, join with snapshot
        string relative = volumePath;
        if (relative.Length >= 2 && relative[1] == ':')
            relative = relative.Substring(2);
        relative = relative.TrimStart('\\', '/');

        return Path.Combine(SnapshotPath, relative);
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Fail-safe: if the engine never called Complete(), the backup did
        // not succeed — abort the VSS writer session rather than leaving
        // writers dangling. This must run BEFORE _disposed is set:
        // SignalAndWait rejects calls once the object is marked disposed,
        // so setting the flag first would silently swallow the abort.
        if (!_finalized)
        {
            Abort();
        }
        _disposed = true;
    }
}
