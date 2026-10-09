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
    /// Raw-writes the ISO to the physical drive. The drive is opened
    /// exclusively; the volume must not be mounted (Windows will prompt to
    /// format afterwards — that's expected).
    /// </summary>
    public static void WriteIso(string devicePath, string isoPath, Action<long, long> progress)
    {
        long isoSize = new FileInfo(isoPath).Length;
        using var iso = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024);

        IntPtr h = CreateFileW(devicePath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE)
            throw new IOException(
                $"Cannot open {devicePath} for writing (error {Marshal.GetLastWin32Error()}). " +
                "Run as Administrator and ensure the drive is not in use.");
        using var drive = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, ownsHandle: true),
            FileAccess.Write, bufferSize: 1024 * 1024);
        byte[] buf = new byte[1024 * 1024];
        long written = 0;
        int read;
        while ((read = iso.Read(buf, 0, buf.Length)) > 0)
        {
            drive.Write(buf, 0, read);
            written += read;
            progress(written, isoSize);
        }
        drive.Flush(flushToDisk: true);
    }
}
