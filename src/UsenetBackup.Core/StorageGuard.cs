using System.Runtime.InteropServices;

namespace UsenetBackup.Core;

/// <summary>
/// Enforces the invariant that backup chunks never live on the drive being
/// backed up. Every operation that reads from a source and writes to a
/// destination must verify the destination cannot overlap the source —
/// otherwise the backup contains itself (the Inception problem), or a
/// restore/sync corrupts the repo it's reading from.
/// </summary>
public static class StorageGuard
{
    /// <summary>
    /// Throws if the repo directory is inside the backup source directory.
    /// Called at the start of file/directory backups.
    /// </summary>
    public static void EnsureRepoNotInSource(string repoRoot, string sourceDir)
    {
        string repo = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        string source = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar);

        if (repo.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(repo, source, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Backup source '{sourceDir}' contains the repository '{repoRoot}'. " +
                "The backup would include its own chunks. Move the repository outside the source tree.");
        }
    }

    /// <summary>
    /// Throws if the repo is on the physical drive being imaged.
    /// Called at the start of disk-image backups.
    /// <paramref name="devicePath"/> is like \\.\PhysicalDrive0.
    /// </summary>
    public static void EnsureRepoNotOnDrive(string repoRoot, string devicePath)
    {
        if (!OperatingSystem.IsWindows())
            return; // Physical-drive mapping is Windows-specific; non-Windows callers use path checks.

        int? targetDrive = ParsePhysicalDriveNumber(devicePath);
        if (targetDrive is null)
            return; // Not a physical-drive path (e.g. a volume); path checks apply instead.

        int? repoDrive = GetPhysicalDriveForPath(repoRoot);
        if (repoDrive is null)
            return; // Could not determine; fail open (the backup itself will fail naturally on space).

        if (repoDrive == targetDrive)
        {
            throw new InvalidOperationException(
                $"Repository '{repoRoot}' is on PhysicalDrive{targetDrive}, the drive being imaged. " +
                "Backup chunks would land on the drive they are backing up. " +
                "Use a repository on a different physical drive, or stream to SMB/NNTP.");
        }
    }

    /// <summary>
    /// Throws if a streaming backup's SMB destination is on the drive being imaged.
    /// UNC shares (\\server\share) are always safe — they're on another machine.
    /// Local paths are checked against the target drive.
    /// </summary>
    public static void EnsureSmbShareNotOnDrive(string sharePath, string devicePath)
    {
        if (string.IsNullOrWhiteSpace(sharePath))
            return;
        string trimmed = sharePath.Trim();
        if (trimmed.StartsWith(@"\\") || trimmed.StartsWith("//"))
            return; // UNC share: remote machine, cannot be the target drive.

        if (!OperatingSystem.IsWindows())
            return;

        int? targetDrive = ParsePhysicalDriveNumber(devicePath);
        if (targetDrive is null)
            return;

        int? shareDrive = GetPhysicalDriveForPath(trimmed);
        if (shareDrive is null)
            return;

        if (shareDrive == targetDrive)
        {
            throw new InvalidOperationException(
                $"SMB destination '{sharePath}' is on PhysicalDrive{targetDrive}, the drive being imaged. " +
                "Chunks would land on the drive they are backing up. Use a share on a different drive or machine.");
        }
    }

    /// <summary>
    /// Throws if the restore destination is the repo directory or inside it.
    /// Restoring there would overwrite chunks and manifests.
    /// </summary>
    public static void EnsureRestoreDestNotInRepo(string repoRoot, string destDir)
    {
        string repo = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        string dest = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar);

        if (dest.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dest, repo, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Restore destination '{destDir}' is inside the repository '{repoRoot}'. " +
                "Restoring there would overwrite backup chunks. Choose a destination outside the repository.");
        }
    }

    /// <summary>
    /// Throws if the SMB sync destination is the repo itself or inside it.
    /// </summary>
    public static void EnsureSmbShareNotInRepo(string repoRoot, string sharePath)
    {
        // UNC shares are on another machine — cannot overlap the local repo.
        string trimmed = sharePath.Trim();
        if (trimmed.StartsWith(@"\\") || trimmed.StartsWith("//"))
            return;

        string repo = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        string share = Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar);

        if (share.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(share, repo, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SMB destination '{sharePath}' is inside the repository '{repoRoot}'. " +
                "Syncing the repo onto itself is not allowed.");
        }
    }

    // ---------- helpers ----------

    /// <summary>Parses "PhysicalDriveN" from paths like \\.\PhysicalDrive0. Null if not a physical-drive path.</summary>
    internal static int? ParsePhysicalDriveNumber(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            devicePath, @"PhysicalDrive(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
            return n;
        return null;
    }

    /// <summary>
    /// Returns the physical drive number hosting the given path, via
    /// IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS on the volume. Null if it cannot
    /// be determined (non-Windows, non-volume path, API failure).
    /// </summary>
    internal static int? GetPhysicalDriveForPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return null;
            // Root is like "C:\". Open the volume.
            string volume = @"\\.\" + root.TrimEnd('\\');
            nint hVol = CreateFile(volume, 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0, nint.Zero);
            if (hVol == -1)
                return null;
            try
            {
                const int outSize = 1024;
                nint outBuf = Marshal.AllocHGlobal(outSize);
                try
                {
                    if (!DeviceIoControl(hVol, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                        nint.Zero, 0, outBuf, outSize, out uint bytesReturned, nint.Zero))
                        return null;
                    if (bytesReturned < 16)
                        return null;
                    // VOLUME_DISK_EXTENTS: NumberOfDiskExtents (DWORD), then extents.
                    // Each DISK_EXTENT: DiskNumber (DWORD), StartingOffset (LARGE), ExtentLength (LARGE).
                    uint extentCount = (uint)Marshal.ReadInt32(outBuf, 0);
                    if (extentCount == 0)
                        return null;
                    // Return the first extent's disk number. (Spanned volumes are
                    // rare for backup repos; the first extent is the practical answer.)
                    return Marshal.ReadInt32(outBuf, 8);
                }
                finally
                {
                    Marshal.FreeHGlobal(outBuf);
                }
            }
            finally
            {
                CloseHandle(hVol);
            }
        }
        catch
        {
            return null;
        }
    }

    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        nint hDevice, uint dwIoControlCode,
        nint lpInBuffer, uint nInBufferSize,
        nint lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
