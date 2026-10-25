using System.Runtime.InteropServices;

namespace FileKeep.Core;

/// <summary>
/// Reads the NTFS volume bitmap via FSCTL_GET_VOLUME_BITMAP to determine
/// which clusters are allocated. Used for used-space-only disk imaging:
/// only allocated clusters need to be backed up.
/// </summary>
public static class VolumeBitmapReader
{
    private const uint FSCTL_GET_VOLUME_BITMAP = 0x9006F;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartingLcnInputBuffer
    {
        public long StartingLcn;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        nint hDevice,
        uint dwIoControlCode,
        ref StartingLcnInputBuffer lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    /// <summary>
    /// Gets the allocated cluster ranges for a volume (e.g. "C:").
    /// Returns a list of (startCluster, clusterCount) tuples.
    /// Throws on failure; callers should fall back to zero-block skipping.
    /// </summary>
    public static List<(long StartCluster, long ClusterCount)> GetAllocatedRanges(string volumeLetter)
    {
        if (string.IsNullOrWhiteSpace(volumeLetter))
            throw new ArgumentException("Volume letter required.", nameof(volumeLetter));

        // Normalize to \\.\C: format
        string volume = volumeLetter.Trim().TrimEnd('\\');
        if (!volume.StartsWith(@"\\.\"))
        {
            if (volume.Length == 1 || (volume.Length == 2 && volume[1] == ':'))
                volume = @"\\.\" + volume.TrimEnd(':') + ":";
            else
                throw new ArgumentException($"Invalid volume: {volumeLetter}", nameof(volumeLetter));
        }

        nint hVolume = CreateFile(
            volume,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero,
            OPEN_EXISTING,
            0,
            nint.Zero);

        if (hVolume == -1)
            throw new IOException($"Cannot open volume {volume}: {Marshal.GetLastWin32Error()}");

        try
        {
            var ranges = new List<(long, long)>();
            long startingLcn = 0;

            // The bitmap can be large; read it in chunks.
            const int bufferSize = 1024 * 1024; // 1MB output buffer
            nint outBuffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                while (true)
                {
                    var inBuf = new StartingLcnInputBuffer { StartingLcn = startingLcn };
                    bool ok = DeviceIoControl(
                        hVolume,
                        FSCTL_GET_VOLUME_BITMAP,
                        ref inBuf,
                        (uint)Marshal.SizeOf<StartingLcnInputBuffer>(),
                        outBuffer,
                        bufferSize,
                        out uint bytesReturned,
                        nint.Zero);

                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        // ERROR_MORE_DATA (234) means the buffer was too small,
                        // but we loop with a larger starting LCN anyway.
                        if (err != 234 && err != 0)
                            throw new IOException($"FSCTL_GET_VOLUME_BITMAP failed: {err}");
                        if (bytesReturned < 16)
                            break;
                    }

                    if (bytesReturned < 16)
                        break;

                    long outStartingLcn = Marshal.ReadInt64(outBuffer, 0);
                    long bitmapSizeBytes = Marshal.ReadInt64(outBuffer, 8);
                    int bitmapBytes = (int)(bytesReturned - 16);

                    if (bitmapBytes <= 0)
                        break;

                    // Parse bitmap bits into ranges.
                    byte[] bitmap = new byte[bitmapBytes];
                    Marshal.Copy(outBuffer + 16, bitmap, 0, bitmapBytes);

                    long? rangeStart = null;
                    for (int i = 0; i < bitmapBytes * 8; i++)
                    {
                        bool allocated = (bitmap[i / 8] & (1 << (i % 8))) != 0;
                        long cluster = outStartingLcn + i;
                        if (allocated)
                        {
                            rangeStart ??= cluster;
                        }
                        else if (rangeStart.HasValue)
                        {
                            ranges.Add((rangeStart.Value, cluster - rangeStart.Value));
                            rangeStart = null;
                        }
                    }
                    if (rangeStart.HasValue)
                    {
                        long endCluster = outStartingLcn + bitmapBytes * 8;
                        ranges.Add((rangeStart.Value, endCluster - rangeStart.Value));
                    }

                    // Move to next chunk. If we got less than requested, we're done.
                    startingLcn = outStartingLcn + bitmapBytes * 8;
                    if (bitmapSizeBytes < bufferSize - 16)
                        break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(outBuffer);
            }

            return ranges;
        }
        finally
        {
            CloseHandle(hVolume);
        }
    }

    /// <summary>
    /// Checks if a byte range (offset, length) overlaps any allocated cluster.
    /// Used to decide whether a chunk needs to be backed up.
    /// </summary>
    public static bool IsRangeAllocated(
        List<(long StartCluster, long ClusterCount)> ranges,
        long offsetBytes,
        long lengthBytes,
        int bytesPerCluster)
    {
        long startCluster = offsetBytes / bytesPerCluster;
        long endCluster = (offsetBytes + lengthBytes - 1) / bytesPerCluster;

        foreach (var (rangeStart, rangeCount) in ranges)
        {
            long rangeEnd = rangeStart + rangeCount - 1;
            if (startCluster <= rangeEnd && endCluster >= rangeStart)
                return true;
        }
        return false;
    }
}
