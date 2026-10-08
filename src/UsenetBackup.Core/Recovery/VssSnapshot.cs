using System.Diagnostics;

namespace UsenetBackup.Core.Recovery;

/// <summary>
/// Manages a VSS shadow copy via the native FileKeepVss.exe helper.
/// Implements ISnapshotProvider for the "vss" provider.
///
/// The helper is a separate process so the managed backup engine never
/// touches COM. If the helper exits non-zero, the snapshot is considered
/// failed and the caller must fail closed (no fallback to live copy).
/// </summary>
public sealed class VssSnapshot : ISnapshotProvider, IDisposable
{
    private readonly string _helperPath;
    private readonly int _timeoutSecs;
    private Process? _process;
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
    /// Valid only after <see cref="Create"/> succeeds.
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
        _disposed = true;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    // Signal "done" by closing stdin (helper treats EOF as done)
                    try { _process.StandardInput.Close(); }
                    catch { /* already closed */ }

                    // Give it 30 seconds to clean up
                    if (!_process.WaitForExit(30_000))
                    {
                        _process.Kill();
                    }
                }
            }
            catch { /* best effort cleanup */ }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        SnapshotPath = null;
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
