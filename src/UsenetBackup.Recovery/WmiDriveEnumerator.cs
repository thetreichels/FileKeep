using System.Management;
using UsenetBackup.Core.Recovery;

namespace UsenetBackup.Recovery;

/// <summary>
/// Lists physical drives via WMI (Win32_DiskDrive). Windows-only;
/// works in WinRE where WMI is available.
/// </summary>
public sealed class WmiDriveEnumerator : IDriveEnumerator
{
    public IReadOnlyList<PhysicalDriveInfo> ListDrives()
    {
        int systemDriveIndex = GetSystemDriveIndex();
        var result = new List<PhysicalDriveInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Model, Size, SerialNumber FROM Win32_DiskDrive");
        foreach (ManagementObject mo in searcher.Get())
        {
            string deviceId = mo["DeviceID"]?.ToString() ?? "";
            // DeviceID looks like \\.\PHYSICALDRIVE0 — extract the index.
            int index = -1;
            string upper = deviceId.ToUpperInvariant();
            const string prefix = @"\\.\PHYSICALDRIVE";
            if (upper.StartsWith(prefix, StringComparison.Ordinal))
                int.TryParse(upper[prefix.Length..], out index);
            if (index < 0)
                continue;

            string model = (mo["Model"]?.ToString() ?? "Unknown drive").Trim();
            ulong size = 0;
            ulong.TryParse(mo["Size"]?.ToString(), out size);
            string serial = (mo["SerialNumber"]?.ToString() ?? "").Trim();

            result.Add(new PhysicalDriveInfo(
                DevicePath: $@"\\.\PhysicalDrive{index}",
                Index: index,
                Model: model,
                SizeBytes: size,
                Serial: serial,
                IsSystemDrive: index == systemDriveIndex));
        }
        return result.OrderBy(d => d.Index).ToList();
    }

    /// <summary>
    /// Finds the physical drive index hosting the Windows installation,
    /// via the LogicalDisk→Partition→DiskDrive WMI associations. Returns -1
    /// if it cannot be determined (fail-open: no drive is flagged).
    /// </summary>
    private static int GetSystemDriveIndex()
    {
        try
        {
            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string? driveLetter = Path.GetPathRoot(windowsDir)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(driveLetter))
                return -1;

            // LogicalDisk (C:) -> Partition
            using var ldToPart = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{driveLetter}'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (ManagementObject part in ldToPart.Get())
            {
                // Partition -> DiskDrive
                using var partToDisk = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{{part.Path.RelativePath}}} " +
                    "WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                foreach (ManagementObject disk in partToDisk.Get())
                {
                    string? deviceId = disk["DeviceID"]?.ToString();
                    if (deviceId is null)
                        continue;
                    string upper = deviceId.ToUpperInvariant();
                    const string prefix = @"\\.\PHYSICALDRIVE";
                    if (upper.StartsWith(prefix, StringComparison.Ordinal) &&
                        int.TryParse(upper[prefix.Length..], out int index))
                        return index;
                }
            }
        }
        catch
        {
            // Best effort: if we can't determine it, don't flag any drive.
        }
        return -1;
    }
}
