using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsenetBackup.Core;

/// <summary>
/// Snapshot provider that uses Windows backup privilege (<c>SE_BACKUP_NAME</c>)
/// to read files that are exclusively locked by other processes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows only; requires administrator rights.</b> On any other OS the
/// constructor throws <see cref="PlatformNotSupportedException"/>.
/// </para>
/// <para>
/// This provider enables the <c>SE_BACKUP_NAME</c> privilege in the process
/// token and opens files with <c>FILE_FLAG_BACKUP_SEMANTICS</c>, which allows
/// reading files that are opened with <c>FileShare.None</c> by other processes
/// — the same mechanism used by <c>robocopy /b</c> (backup mode).
/// </para>
/// <para>
/// Note: unlike a true Volume Shadow Copy snapshot, this does not provide a
/// point-in-time frozen view; it reads the live files while bypassing locks.
/// The <c>--vss</c> flag name is kept for CLI compatibility.
/// </para>
/// </remarks>
public sealed class VssSnapshotProvider : ISnapshotProvider
{
    /// <summary>
    /// Enables backup privilege for the current process.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    /// <exception cref="UnauthorizedAccessException">Backup privilege could not be enabled (admin required).</exception>
    public VssSnapshotProvider(string sourceDir)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Backup privilege file access requires Windows. Use NullSnapshotProvider (live read) on other platforms.");

        SnapshotRoot = Path.GetFullPath(sourceDir);
        EnableBackupPrivilege();
    }

    public string SnapshotRoot { get; }

    public bool IsSnapshot => true;

    public string Name => "vss";

    /// <summary>
    /// Opens a file with backup semantics, bypassing exclusive locks held by
    /// other processes.
    /// </summary>
    public FileStream OpenRead(string fullPath)
    {
        SafeFileHandle handle = Native.CreateFile(
            fullPath,
            Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Native.OPEN_EXISTING,
            Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_SEQUENTIAL_SCAN,
            IntPtr.Zero);

        if (handle.IsInvalid)
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());

        return new FileStream(handle, FileAccess.Read);
    }

    public void Dispose()
    {
        // Privilege is process-wide; we leave it enabled (harmless).
    }

    private static void EnableBackupPrivilege()
    {
        const string SE_BACKUP_NAME = "SeBackupPrivilege";

        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY, out IntPtr token))
            throw new UnauthorizedAccessException("Failed to open process token for privilege adjustment.");

        try
        {
            if (!Native.LookupPrivilegeValue(null, SE_BACKUP_NAME, out Native.LUID luid))
                throw new UnauthorizedAccessException("SeBackupPrivilege not found (administrator rights required).");

            var tp = new Native.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new Native.LUID_AND_ATTRIBUTES[1]
            };
            tp.Privileges[0].Luid = luid;
            tp.Privileges[0].Attributes = Native.SE_PRIVILEGE_ENABLED;

            if (!Native.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                throw new UnauthorizedAccessException("Failed to adjust token privileges.");

            // AdjustTokenPrivileges returns success even if the privilege isn't held;
            // check GetLastError for ERROR_NOT_ALL_ASSIGNED.
            int err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_NOT_ALL_ASSIGNED)
                throw new UnauthorizedAccessException(
                    "SeBackupPrivilege could not be enabled (administrator rights required).");
            if (err != 0)
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint FILE_SHARE_DELETE = 0x00000004;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        public const int ERROR_NOT_ALL_ASSIGNED = 1300;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValue(
            string? lpSystemName,
            string lpName,
            out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState,
            uint bufferLength,
            IntPtr previousState,
            IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public LUID_AND_ATTRIBUTES[] Privileges;
        }
    }
}
