using System.Runtime.InteropServices;
using System.Text;

namespace UsenetBackup.Core.Recovery;

/// <summary>
/// Enumerates USB-attached physical drives (Windows only).
/// Shared by the CLI recovery-usb command and the service dashboard.
/// </summary>
public static class UsbDrives
{
    public sealed record UsbDrive(int Number, string DevicePath, string Model, long SizeBytes);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE = new(-1);

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x700A0;

    private enum STORAGE_BUS_TYPE : uint
    {
        Unknown = 0, Scsi = 1, Atapi = 2, Ata = 3, IEEE1394 = 4, Ssa = 5,
        Fibre = 6, Usb = 7, RAID = 8, iScsi = 9, Sas = 10, Sata = 11,
        Sd = 12, Mmc = 13, Virtual = 14, FileBackedVirtual = 15,
        Spaces = 16, Nvme = 17, SCN = 18, Ufs = 19,
    }

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

    public static List<UsbDrive> List()
    {
        var result = new List<UsbDrive>();
        for (int n = 0; n < 16; n++)
        {
            string path = $"\\\\.\\PhysicalDrive{n}";
            IntPtr h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE)
                continue;
            try
            {
                if (TryGetBusType(h, out var bus) && bus == STORAGE_BUS_TYPE.Usb)
                {
                    TryGetModel(h, out string model);
                    TryGetSize(h, out long size);
                    result.Add(new UsbDrive(n, path, model, size));
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }
        return result;
    }

    private static bool TryGetBusType(IntPtr h, out STORAGE_BUS_TYPE bus)
    {
        bus = STORAGE_BUS_TYPE.Unknown;
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
            uint busType = (uint)Marshal.ReadInt32(pOut, 4 + 4 + 1 + 1 + 1 + 1 + 4 + 4 + 4 + 4);
            bus = (STORAGE_BUS_TYPE)busType;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pQuery);
            Marshal.FreeHGlobal(pOut);
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
