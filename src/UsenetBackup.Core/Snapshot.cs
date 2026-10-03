namespace UsenetBackup.Core;

/// <summary>
/// A read-only view of a source tree, used so a backup can read files that
/// might otherwise be locked or changing.
/// </summary>
/// <remarks>
/// <para>
/// Implementations: <see cref="NullSnapshotProvider"/> reads the live tree
/// directly (no special handling; works everywhere).
/// <see cref="BackupPrivilegeSnapshotProvider"/> uses Windows backup privilege
/// (<c>SE_BACKUP_NAME</c>) to open files that are exclusively locked by other
/// processes (Windows only, admin rights required). It reads the live files,
/// not a point-in-time copy.
/// </para>
/// <para>
/// The provider is owned by the caller: create it, run the backup against
/// <see cref="SnapshotRoot"/>, then dispose it to release any resources.
/// </para>
/// </remarks>
public interface ISnapshotProvider : IDisposable
{
    /// <summary>
    /// Root directory of the readable view. Enumerate files relative to this
    /// path exactly as you would the live source directory.
    /// </summary>
    string SnapshotRoot { get; }

    /// <summary>True when this provider does more than a live passthrough read.</summary>
    bool IsSnapshot { get; }

    /// <summary>
    /// Short name recorded in the manifest and logs, e.g. "backup-privilege" or "none".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Opens a file for reading through this provider. Providers with special
    /// access (e.g. backup privilege) override the default to bypass locks.
    /// </summary>
    FileStream OpenRead(string fullPath);
}

/// <summary>
/// Passthrough provider: no special handling, the live tree is read directly.
/// This is the default and works on every platform.
/// </summary>
public sealed class NullSnapshotProvider : ISnapshotProvider
{
    public NullSnapshotProvider(string sourceDir)
    {
        SnapshotRoot = Path.GetFullPath(sourceDir);
    }

    public string SnapshotRoot { get; }

    public bool IsSnapshot => false;

    public string Name => "none";

    public FileStream OpenRead(string fullPath)
    {
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}
