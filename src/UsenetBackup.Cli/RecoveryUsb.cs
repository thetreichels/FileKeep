using System.Runtime.InteropServices;

namespace UsenetBackup.Cli;

/// <summary>
/// Writes a FileKeep WinPE recovery ISO to a USB drive (raw disk write).
/// Windows only, requires administrator rights.
///
/// Drive enumeration is shared with the service dashboard via
/// UsenetBackup.Core.Recovery.UsbDrives. Only the destructive write lives
/// here in the CLI.
///
/// Safety model: only USB-attached drives are eligible targets. The caller
/// must confirm by typing the physical drive number; --yes skips the prompt
/// for scripting (use with care).
/// </summary>
internal static class RecoveryUsb
{
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    /// <summary>
    /// Lists USB-attached physical drives (shared Core implementation).
    /// </summary>
    public static List<UsenetBackup.Core.Recovery.UsbDrives.UsbDrive> ListUsbDrives() =>
        UsenetBackup.Core.Recovery.UsbDrives.List();

    /// <summary>
    /// Raw-writes the ISO to the physical drive. Delegates to the shared Core
    /// writer. The drive is opened exclusively; the volume must not be mounted
    /// (Windows will prompt to format afterwards — that's expected).
    /// </summary>
    public static void WriteIso(string devicePath, string isoPath, Action<long, long> progress) =>
        UsenetBackup.Core.Recovery.RawDiskWriter.WriteIso(devicePath, isoPath, progress);
}
