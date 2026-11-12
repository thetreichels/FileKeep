namespace FileKeep.Core.Recovery;

/// <summary>
/// A physical disk available as a restore target.
/// </summary>
public sealed record PhysicalDriveInfo(
    string DevicePath,   // e.g. \\.\PhysicalDrive0
    int Index,           // e.g. 0
    string Model,        // e.g. "Samsung SSD 860 EVO 500GB"
    ulong SizeBytes,     // total size
    string Serial,       // may be empty in WinRE
    bool IsSystemDrive = false)  // hosts the running Windows installation
{
    public string Display =>
        IsSystemDrive
            ? $"⚠ {Index}: {Model} ({SizeFormatting.Format(SizeBytes, SizeUnitStyle.DecimalLabelsGbMb)}) [{DevicePath}] — SYSTEM DRIVE"
            : $"{Index}: {Model} ({SizeFormatting.Format(SizeBytes, SizeUnitStyle.DecimalLabelsGbMb)}) [{DevicePath}]";
}

/// <summary>
/// Enumerates physical drives. Implemented with WMI in the UI project
/// (Windows-only); tests use a fake.
/// </summary>
public interface IDriveEnumerator
{
    IReadOnlyList<PhysicalDriveInfo> ListDrives();
}
