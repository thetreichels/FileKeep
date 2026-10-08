using System.Diagnostics;

namespace UsenetBackup.Core.Recovery;

/// <summary>
/// Manages a VSS shadow copy via the native FileKeepVss.exe helper.
/// Implements ISnapshotProvider for the "vss" provider.
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
/// </summary>
public sealed class VssSnapshot : ISnapshotProvider, IDisposable
{
    private readonly string _helperPath;
    private readonly int _timeoutSecs;
    private Process? _process;
    private bool _finalized;
    private bool _disposed;

    /// <param name="helperPath">Full path to FileKeepVss.exe.</param>
    /// <param name="timeoutSecs">Seconds to hold the snapshot.</param>
    public VssSnapshot(string helperPath, int timeoutSecs = 3600)
    {
        _helperPath = helperPath ?? throw new ArgumentNullException(nameof(helperPath));
        _timeoutSecs = timeoutSecs;
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
    public void Create(string volume)
    {
        if (_process is not null)
            throw new InvalidOperationException("Snapshot already created.");

        if (!File.Exists(_helperPath))
            throw new FileNotFoundException(
                $"VSS helper not found: {_helperPath}. " +
                "VSS snapshot requires FileKeepVss.exe.", _helperPath);

        var psi = new ProcessStartInfo
        {
            FileName = _helperPath,
            Arguments = $"--volume \"{volume}\" --timeout {_timeoutSecs}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start VSS helper.");

        // Read the snapshot path from stdout (first line)
        string? path = _process.StandardOutput.ReadLine();
        if (string.IsNullOrWhiteSpace(path))
        {
            string err = _process.StandardError.ReadToEnd();
            _process.WaitForExit(5000);
            throw new InvalidOperationException(
                $"VSS helper failed to create snapshot: {err.Trim()} " +
                $"(exit code {_process.ExitCode})");
        }

        // Check if the process already exited (indicates failure after
        // printing something unexpected)
        if (_process.HasExited && _process.ExitCode != 0)
        {
            string err = _process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"VSS helper exited with code {_process.ExitCode}: {err.Trim()}");
        }

        SnapshotPath = path.Trim();
    }

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
                    string err;
                    try { err = proc.StandardError.ReadToEnd(); }
                    catch { err = string.Empty; }
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

/// <summary>
/// Snapshot provider abstraction. Implementations: none (live files),
/// backup-privilege (live files with privilege), vss (shadow copy).
/// </summary>
public interface ISnapshotProvider : IDisposable
{
    /// <summary>Provider identifier: "none", "backup-privilege", or "vss".</summary>
    string ProviderId { get; }

    /// <summary>Translates a volume path to the snapshot-accessible path.</summary>
    string TranslatePath(string volumePath);
}
