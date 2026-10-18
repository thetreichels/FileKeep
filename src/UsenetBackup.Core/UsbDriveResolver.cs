using System;
using System.IO;
using System.Runtime.InteropServices;

namespace UsenetBackup.Core;

/// <summary>
/// Resolves removable USB drives by volume serial number (stable across
/// drive-letter changes) instead of by letter.
/// </summary>
public static class UsbDriveResolver
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        System.Text.StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        System.Text.StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    /// <summary>
    /// Returns the volume serial number for a drive root (e.g. "E:\") as
    /// an uppercase hex string, or null if it can't be determined.
    /// </summary>
    public static string? GetVolumeSerial(string driveRoot)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            if (GetVolumeInformationW(driveRoot, null, 0,
                out uint serial, out _, out _, null, 0))
            {
                return serial.ToString("X8");
            }
        }
        catch { /* fall through */ }
        return null;
    }

    /// <summary>
    /// Finds the current drive letter for a USB location by matching its
    /// stored volume serial. Returns null if the drive isn't plugged in.
    /// </summary>
    public static string? ResolveDriveLetter(string volumeSerial)
    {
        if (string.IsNullOrWhiteSpace(volumeSerial))
            return null;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady)
                    continue;
                string? serial = GetVolumeSerial(drive.Name);
                if (serial is not null &&
                    serial.Equals(volumeSerial, StringComparison.OrdinalIgnoreCase))
                {
                    return drive.Name;
                }
            }
        }
        catch { /* fall through */ }
        return null;
    }

    /// <summary>
    /// Resolves the full backup folder path for a USB location, e.g.
    /// "E:\FileKeep". Returns null if the drive isn't present.
    /// </summary>
    public static string? ResolveBackupPath(string volumeSerial, string folder)
    {
        string? letter = ResolveDriveLetter(volumeSerial);
        if (letter is null)
            return null;
        string sub = string.IsNullOrWhiteSpace(folder) ? "FileKeep" : folder;
        return Path.Combine(letter, sub);
    }
}
