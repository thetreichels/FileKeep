using System.Management;
using UsenetBackup.Core.Recovery;

namespace UsenetBackup.Recovery;

/// <summary>
/// Lists physical drives via WMI (Win32_DiskDrive). Windows-only;
/// works in WinPE where WMI is available.
/// </summary>
public sealed class WmiDriveEnumerator : IDriveEnumerator
{
    public IReadOnlyList<PhysicalDriveInfo> ListDrives()
    {
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
                Serial: serial));
        }
        return result.OrderBy(d => d.Index).ToList();
    }
}
