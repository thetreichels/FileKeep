namespace UsenetBackup.Core;

/// <summary>
/// A point-in-time, read-only view of a source tree, used so a backup reads
/// consistent data even while the live tree is changing.
/// </summary>
/// <remarks>
/// <para>
/// Implementations: <see cref="NullSnapshotProvider"/> reads the live tree
/// directly (no snapshot; works everywhere). <see cref="VssSnapshotProvider"/>
/// uses the Windows Volume Shadow Copy Service (Windows only, admin rights
/// required).
/// </para>
/// <para>
/// The provider is owned by the caller: create it, run the backup against
/// <see cref="SnapshotRoot"/>, then dispose it to release the snapshot.
/// </para>
/// </remarks>
public interface ISnapshotProvider : IDisposable
{
    /// <summary>
    /// Root directory of the consistent view. Enumerate files relative to this
    /// path exactly as you would the live source directory.
    /// </summary>
    string SnapshotRoot { get; }

    /// <summary>True when this is a real point-in-time snapshot.</summary>
    bool IsSnapshot { get; }

    /// <summary>
    /// Short name recorded in the manifest and logs, e.g. "vss" or "none".
    /// </summary>
    string Name { get; }
}

/// <summary>
/// Passthrough provider: no snapshot is taken, the live tree is read directly.
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

    public void Dispose()
    {
        // Nothing to release.
    }
}
