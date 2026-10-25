using System.Runtime.InteropServices;
using System.Text;

namespace FileKeep.Core.Recovery;

/// <summary>
/// Enumerates all physical drives (Windows only) using the same
/// CreateFileW + DeviceIoControl technique as <see cref="UsbDrives"/>.
/// Used by the dashboard so disk pickers never require typing a device path.
/// </summary>
public static class PhysicalDrives
{
    public sealed record PhysicalDrive(
        int Number,
        string DevicePath,   // e.g. \\.\PhysicalDrive2
        string Model,        // e.g. "Samsung SSD 860 EVO 500GB"
        long SizeBytes,
        bool IsSystemDrive);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE = new(-1);

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x700A0;
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x560000;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;

        public STORAGE_PROPERTY_QUERY(uint propertyId, uint queryType)
        {
            PropertyId = propertyId;
            QueryType = queryType;
            AdditionalParameters = new byte[1];
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Lists all present physical drives, ordered by device number.
    /// Returns an empty list on non-Windows platforms.
    /// </summary>
    public static List<PhysicalDrive> List()
    {
        var result = new List<PhysicalDrive>();
        if (!OperatingSystem.IsWindows())
            return result;
        int systemDisk = GetSystemDiskNumber();
        for (int n = 0; n < 64; n++)
        {
            string path = $@"\\.\PhysicalDrive{n}";
            IntPtr h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE)
                continue;
            try
            {
                TryGetModel(h, out string model);
                TryGetSize(h, out long size);
                result.Add(new PhysicalDrive(n, path,
                    string.IsNullOrWhiteSpace(model) ? "Unknown drive" : model,
                    size, n == systemDisk));
            }
            finally
            {
                CloseHandle(h);
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the physical disk number hosting the Windows system volume,
    /// or -1 when it cannot be determined.
    /// </summary>
    private static int GetSystemDiskNumber()
    {
        try
        {
            string? root = Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrEmpty(root))
                return -1;
            // Volume path for IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, e.g. \\.\C:
            string volumePath = $@"\\.\{root.TrimEnd('\\', '/')}";
            IntPtr h = CreateFileW(volumePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE)
                return -1;
            try
            {
                IntPtr pOut = Marshal.AllocHGlobal(64);
                try
                {
                    if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                            IntPtr.Zero, 0, pOut, 64, out _, IntPtr.Zero))
                        return -1;
                    // VOLUME_DISK_EXTENTS: DWORD count, then DISK_EXTENT structs;
                    // DISK_EXTENT.DiskNumber is the first DWORD of each extent.
                    uint count = (uint)Marshal.ReadInt32(pOut, 0);
                    if (count == 0)
                        return -1;
                    return Marshal.ReadInt32(pOut, 8);
                }
                finally
                {
                    Marshal.FreeHGlobal(pOut);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryGetModel(IntPtr h, out string model)
    {
        model = "";
        var query = new STORAGE_PROPERTY_QUERY(0, 0);
        int querySize = Marshal.SizeOf(query);
        IntPtr pQuery = Marshal.AllocHGlobal(querySize);
        IntPtr pOut = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.StructureToPtr(query, pQuery, false);
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY,
                    pQuery, (uint)querySize, pOut, 1024, out _, IntPtr.Zero))
                return false;
            int productOffset = Marshal.ReadInt32(pOut, 4 + 4 + 1 + 1 + 1 + 1 + 4 + 4);
            int vendorOffset = Marshal.ReadInt32(pOut, 4 + 4 + 1 + 1 + 1 + 1 + 4);
            var sb = new StringBuilder();
            if (vendorOffset > 0)
                sb.Append(ReadAnsiString(pOut, vendorOffset).Trim());
            if (productOffset > 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(ReadAnsiString(pOut, productOffset).Trim());
            }
            model = sb.ToString();
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pQuery);
            Marshal.FreeHGlobal(pOut);
        }
    }

    private static string ReadAnsiString(IntPtr base_, int offset)
    {
        var bytes = new List<byte>();
        int i = offset;
        byte b;
        while ((b = Marshal.ReadByte(base_, i++)) != 0)
            bytes.Add(b);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static bool TryGetSize(IntPtr h, out long size)
    {
        size = 0;
        IntPtr pOut = Marshal.AllocHGlobal(32);
        try
        {
            if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                    IntPtr.Zero, 0, pOut, 32, out _, IntPtr.Zero))
                return false;
            size = Marshal.ReadInt64(pOut, 24);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pOut);
        }
    }
}
