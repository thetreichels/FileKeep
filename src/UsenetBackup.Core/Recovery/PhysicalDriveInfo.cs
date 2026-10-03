namespace UsenetBackup.Core.Recovery;

/// <summary>
/// A physical disk available as a restore target.
/// </summary>
public sealed record PhysicalDriveInfo(
    string DevicePath,   // e.g. \\.\PhysicalDrive0
    int Index,           // e.g. 0
    string Model,        // e.g. "Samsung SSD 860 EVO 500GB"
    ulong SizeBytes,     // total size
    string Serial)       // may be empty in WinPE
{
    public string Display =>
        $"{Index}: {Model} ({FormatSize(SizeBytes)}) [{DevicePath}]";

    private static string FormatSize(ulong bytes)
    {
        const ulong GB = 1024UL * 1024 * 1024;
        if (bytes >= GB)
            return $"{bytes / (double)GB:F1} GB";
        return $"{bytes / (1024.0 * 1024):F0} MB";
    }
}

/// <summary>
/// Enumerates physical drives. Implemented with WMI in the UI project
/// (Windows-only); tests use a fake.
/// </summary>
public interface IDriveEnumerator
{
    IReadOnlyList<PhysicalDriveInfo> ListDrives();
}
