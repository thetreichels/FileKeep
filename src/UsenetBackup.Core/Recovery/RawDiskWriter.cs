using System.Runtime.InteropServices;

namespace UsenetBackup.Core.Recovery;

/// <summary>
/// Raw-writes an image file to a physical disk device (Windows only).
/// Shared by the CLI recovery-usb command and the service dashboard.
/// Requires administrator rights.
/// </summary>
public static class RawDiskWriter
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
    /// Raw-writes <paramref name="imagePath"/> to <paramref name="devicePath"/>
    /// (e.g. \\.\PhysicalDrive2). Reports (bytesWritten, totalBytes).
    /// </summary>
    public static void WriteIso(string devicePath, string imagePath, Action<long, long>? progress = null)
    {
        long imageSize = new FileInfo(imagePath).Length;
        using var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
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
        while ((read = image.Read(buf, 0, buf.Length)) > 0)
        {
            drive.Write(buf, 0, read);
            written += read;
            progress?.Invoke(written, imageSize);
        }
        drive.Flush(flushToDisk: true);
    }
}
