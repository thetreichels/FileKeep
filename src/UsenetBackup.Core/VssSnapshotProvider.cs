using System.Runtime.InteropServices;

namespace UsenetBackup.Core;

/// <summary>
/// Snapshot provider backed by the Windows Volume Shadow Copy Service (VSS).
/// Takes a shadow copy of the volume containing the source directory and
/// exposes it as a read-only path of the form
/// <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\...</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows only; requires administrator rights.</b> On any other OS the
/// constructor throws <see cref="PlatformNotSupportedException"/>. The COM
/// interop below is declared against <c>vssapi.dll</c> /
/// <c>IVssBackupComponents</c> from the Windows SDK (<c>vss.h</c>).
/// </para>
/// <para>
/// <b>Validation note:</b> the <see cref="IVssBackupComponents"/> vtable order
/// must be re-verified against the SDK's <c>vss.h</c> on a Windows build
/// machine before trusting snapshots in production. Only the slots actually
/// called (up to <c>GetSnapshotProperties</c>) are load-bearing; every
/// predecessor is declared in SDK order so the vtable lines up.
/// </para>
/// <para>
/// The snapshot flow used here is the minimal writer-independent one:
/// InitializeForBackup → SetContext → StartSnapshotSet → AddToSnapshotSet →
/// DoSnapshotSet. Snapshots are created non-persistent
/// (<c>VSS_CTX_BACKUP</c>) and are explicitly deleted on dispose.
/// </para>
/// </remarks>
public sealed class VssSnapshotProvider : ISnapshotProvider
{
    private IVssBackupComponents? _backup;
    private Guid _snapshotId;
    private bool _disposed;

    /// <summary>
    /// Takes a VSS snapshot of the volume containing <paramref name="sourceDir"/>.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    /// <exception cref="UnauthorizedAccessException">VSS requires administrator rights.</exception>
    public VssSnapshotProvider(string sourceDir)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "VSS snapshots require Windows. Use NullSnapshotProvider (live read) on other platforms.");

        string fullSource = Path.GetFullPath(sourceDir);
        string? volumeRoot = Path.GetPathRoot(fullSource);
        if (string.IsNullOrEmpty(volumeRoot))
            throw new ArgumentException($"Cannot determine the volume for '{fullSource}'.", nameof(sourceDir));

        int hr = Native.CreateVssBackupComponents(out IVssBackupComponents? backup);
        Marshal.ThrowExceptionForHR(hr);
        _backup = backup ?? throw new InvalidOperationException("CreateVssBackupComponents returned null.");

        try
        {
            // HRESULT-returning COM methods throw on failure via the interface declaration.
            _backup.InitializeForBackup(null);
            _backup.SetContext(Native.VSS_CTX_BACKUP);
            _backup.StartSnapshotSet(out Guid snapshotSetId);
            _backup.AddToSnapshotSet(volumeRoot, Guid.Empty, out _snapshotId);

            _backup.DoSnapshotSet(out IVssAsync? async);
            try
            {
                if (async is null)
                    throw new InvalidOperationException("DoSnapshotSet returned null async handle.");
                async.Wait();
                async.QueryStatus(out int statusHr, out _);
                Marshal.ThrowExceptionForHR(statusHr);
            }
            finally
            {
                if (async is not null)
                    Marshal.ReleaseComObject(async);
            }

            _backup.GetSnapshotProperties(_snapshotId, out VSS_SNAPSHOT_PROP prop);
            string deviceObject;
            try
            {
                deviceObject = prop.m_pwszSnapshotDeviceObject
                    ?? throw new InvalidOperationException("VSS returned no snapshot device object.");
            }
            finally
            {
                Native.VssFreeSnapshotProperties(ref prop);
            }

            // Map the source dir onto the shadow copy: strip the volume root
            // (e.g. "C:\") and re-root under the snapshot device object.
            string relative = Path.GetRelativePath(volumeRoot, fullSource);
            SnapshotRoot = relative == "."
                ? deviceObject
                : Path.Combine(deviceObject, relative);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    public string SnapshotRoot { get; }

    public bool IsSnapshot => true;

    public string Name => "vss";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cleanup();
        GC.SuppressFinalize(this);
    }

    private void Cleanup()
    {
        var backup = Interlocked.Exchange(ref _backup, null);
        if (backup is null)
            return;
        try
        {
            if (_snapshotId != Guid.Empty)
            {
                try
                {
                    backup.DeleteSnapshots(
                        _snapshotId,
                        Native.VSS_OBJECT_SNAPSHOT,
                        true,
                        out _,
                        out _);
                }
                catch
                {
                    // Best effort: with VSS_CTX_BACKUP, VSS also auto-deletes
                    // non-persistent snapshots when the requester goes away.
                }
                _snapshotId = Guid.Empty;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(backup);
        }
    }

    // ------------------------------------------------------------------
    // VSS native interop (vssapi.dll). See the class remarks about vtable
    // validation before production use on Windows.
    // ------------------------------------------------------------------

    private static class Native
    {
        public const int VSS_CTX_BACKUP = 0;
        public const int VSS_OBJECT_SNAPSHOT = 3;

        [DllImport("vssapi.dll", PreserveSig = true)]
        public static extern int CreateVssBackupComponents(
            [MarshalAs(UnmanagedType.Interface)] out IVssBackupComponents ppBackup);

        [DllImport("vssapi.dll")]
        public static extern void VssFreeSnapshotProperties(ref VSS_SNAPSHOT_PROP pProp);
    }

    [ComImport]
    [Guid("507c37b4-cf5b-4e95-b0af-14ebbc97677a")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssAsync
    {
        void Cancel();
        void Wait(int dwMilliseconds = unchecked((int)0xFFFFFFFF));
        void QueryStatus(out int phrResult, out int pReserved);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct VSS_SNAPSHOT_PROP
    {
        public Guid m_SnapshotId;
        public Guid m_SnapshotSetId;
        public int m_lSnapshotsCount;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? m_pwszSnapshotDeviceObject;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? m_pwszOriginalVolumeName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? m_pwszOriginatingMachine;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? m_pwszServiceMachine;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? m_pwszExposedName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? m_pwszExposedPath;
        public Guid m_ProviderId;
        public int m_lSnapshotAttributes;
        public long m_tsCreationTimestamp; // FILETIME as long
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? m_pwszVolumeName;
    }

    // IVssBackupComponents vtable, in Windows SDK (vss.h) declaration order.
    // Slots 3..22 (IUnknown is 0..2). Only slots up to GetSnapshotProperties
    // are called; the rest of the interface is intentionally not declared.
    // Slots marked (V) were added in Vista+ VSS; their presence shifts every
    // later slot, so re-verify against vss.h if the SDK ever changes.
    [ComImport]
    [Guid("665c1d5f-c218-414d-afa3-555c19c5f57e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssBackupComponents
    {
        // 3
        void GetWriterComponentsCount(out uint pcComponents);
        // 4
        void GetWriterComponents(uint iWriter,
            [MarshalAs(UnmanagedType.Interface)] out object ppWriter);
        // 5 — null XML = no stored writer metadata document.
        void InitializeForBackup([MarshalAs(UnmanagedType.BStr)] string? bstrXML);
        // 6
        void SetBackupState(
            [MarshalAs(UnmanagedType.Bool)] bool bSelectComponents,
            [MarshalAs(UnmanagedType.Bool)] bool bBackupBootableSystemState,
            int backupType,
            [MarshalAs(UnmanagedType.Bool)] bool bPartialFileSupport);
        // 7
        void GatherWriterMetadata([MarshalAs(UnmanagedType.Interface)] out IVssAsync ppAsync);
        // 8
        void GetWriterMetadata(uint iWriter, out Guid pidInstance,
            [MarshalAs(UnmanagedType.Interface)] out object ppMetadata);
        // 9
        void FreeWriterMetadata();
        // 10
        void SetBackupSucceeded(Guid writerId, Guid instanceId,
            [MarshalAs(UnmanagedType.Bool)] bool bSucceeded);
        // 11
        void SetFileRestoreStatus(Guid writerId, Guid instanceId, int status);
        // 12
        void SetRangesFilePath(Guid writerId, Guid instanceId, Guid id,
            [MarshalAs(UnmanagedType.LPWStr)] string wszRangesFilePath);
        // 13
        void PreRestore([MarshalAs(UnmanagedType.Interface)] out IVssAsync ppAsync);
        // 14
        void PostRestore([MarshalAs(UnmanagedType.Interface)] out IVssAsync ppAsync);
        // 15
        void SetContext(int lContext);
        // 16
        void StartSnapshotSet(out Guid pSnapshotSetId);
        // 17 — ProviderId = Guid.Empty selects the default provider.
        void AddToSnapshotSet(
            [MarshalAs(UnmanagedType.LPWStr)] string pwszVolumeName,
            Guid providerId,
            out Guid pidSnapshot);
        // 18
        void DoSnapshotSet([MarshalAs(UnmanagedType.Interface)] out IVssAsync ppAsync);
        // 19
        void DeleteSnapshots(Guid sourceObjectId, int eSourceObjectType,
            [MarshalAs(UnmanagedType.Bool)] bool bForceDelete,
            out int plDeletedSnapshots, out Guid pNondeletedSnapshotID);
        // 20
        void ImportSnapshots([MarshalAs(UnmanagedType.Interface)] out IVssAsync ppAsync);
        // 21 (V)
        void BreakSnapshotSet(Guid snapshotSetId);
        // 22
        void GetSnapshotProperties(Guid snapshotId, out VSS_SNAPSHOT_PROP pProp);
    }
}
