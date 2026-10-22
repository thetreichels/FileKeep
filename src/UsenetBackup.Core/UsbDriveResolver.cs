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
    /// <summary>
    /// Optional diagnostics sink (MU-17): resolution failures used to
    /// be swallowed whole, making a present-but-erroring drive
    /// indistinguishable from an unplugged one in every surface. The
    /// service wires this to its log; when null (tests, CLI) failures
    /// stay quiet but the return contract is unchanged.
    /// </summary>
    public static Action<string>? DiagnosticLog { get; set; }

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
            DiagnosticLog?.Invoke(
                $"USB: could not read the volume serial of {driveRoot} " +
                $"(Win32 error {Marshal.GetLastWin32Error()}).");
        }
        catch (Exception ex)
        {
            DiagnosticLog?.Invoke(
                $"USB: reading the volume serial of {driveRoot} failed: {ex.Message}");
        }
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
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            // MU-17: an enumeration failure is not "drive not plugged
            // in" — log it as the error it is.
            DiagnosticLog?.Invoke($"USB: enumerating drives failed: {ex.Message}");
            return null;
        }
        foreach (var drive in drives)
        {
            // Per-drive isolation: one present-but-erroring drive
            // (no media, not ready) must not hide every other drive.
            try
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
            catch (Exception ex)
            {
                DiagnosticLog?.Invoke(
                    $"USB: inspecting drive {drive.Name} failed: {ex.Message}");
            }
        }
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
