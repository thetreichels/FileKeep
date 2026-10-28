using FileKeep.Core;
using FileKeep.Core.Service;
using FileKeep.Core.Security;

namespace FileKeep.Service;

/// <summary>
/// DashboardApi — Offline Folders slice 3 part 1: Save As.
///
/// Save As is a one-way decrypted export, NOT a checkout: the file's
/// decrypted bytes are written to a destination the user chose, and
/// FileKeep does not track, reload, or clean the copy afterwards.
/// Destinations: a local path (picked with the host save dialog, or
/// typed), a USB stick (resolved by volume serial, like every other
/// USB destination), or an SMB share (a configured location, written
/// with that location's stored credentials). The copy is streamed
/// chunk by chunk through <see cref="DashboardApi.StreamOfflineFile"/>
/// with the same hygiene (plaintext buffers zeroed after each write);
/// a failed transfer deletes the partial destination file rather
/// than leaving half a plaintext file behind.
///
/// Leak discipline, same as the rest of the offline API: no error
/// message echoes a folder name, a file name, a path, or a volume
/// serial. The success payload names only what the caller already
/// chose (the written file name, the destination kind, the byte
/// count). No credentials are accepted, logged, or returned: SMB
/// uses the location's stored credentials, resolved exactly the
/// way the file store resolves them.
/// </summary>
public static partial class DashboardApi
{
    /// <summary>
    /// Writes one offline file's decrypted bytes to a user-chosen
    /// destination. <paramref name="destination"/> is "local", "usb",
    /// or "smb". Local takes a full file <paramref name="path"/> (a
    /// directory path gets the file's leaf name appended); USB and
    /// SMB take a configured <paramref name="locationName"/> and an
    /// optional <paramref name="fileName"/> (default: the offline
    /// file's leaf name). Without <paramref name="overwrite"/>, an
    /// existing destination file is refused rather than replaced.
    /// </summary>
    public static (int Status, object Payload) SaveOfflineFileAs(
        ServiceConfig config, OfflineFoldersSession session,
        string folderName, string filePath, string destination,
        string? locationName, string? path, string? fileName, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(filePath))
            return (400, new ErrorDto("A folder and a file are required."));
        if (destination != "local" && destination != "usb" && destination != "smb")
            return (400, new ErrorDto("A destination is required."));

        string leaf = LeafName(filePath);
        if (fileName is not null && !IsValidLeafName(fileName))
            return (400, new ErrorDto("That file name is not usable as a file name."));
        string writeName = fileName ?? leaf;

        // Resolve the destination BEFORE streaming anything, so a bad
        // destination fails fast instead of after a partial transfer.
        string destPath;
        SmbShare? share = null;
        try
        {
            if (destination == "local")
            {
                if (string.IsNullOrWhiteSpace(path))
                    return (400, new ErrorDto("A destination path is required."));
                destPath = path;
                if (Directory.Exists(destPath) ||
                    destPath.EndsWith(Path.DirectorySeparatorChar) ||
                    destPath.EndsWith(Path.AltDirectorySeparatorChar))
                    destPath = Path.Combine(destPath, writeName);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(locationName))
                    return (400, new ErrorDto("A destination location is required."));
                var location = config.SnapshotLocations().FirstOrDefault(l =>
                    string.Equals(l.Name, locationName, StringComparison.OrdinalIgnoreCase));
                if (location is null)
                    return (400, new ErrorDto("That destination location no longer exists."));
                if (destination == "usb")
                {
                    if (!string.Equals(location.Type, "usb", StringComparison.OrdinalIgnoreCase))
                        return (400, new ErrorDto("That location is not a USB location."));
                    if (string.IsNullOrWhiteSpace(location.VolumeSerial))
                        return (400, new ErrorDto("That location has no volume serial configured."));
                    string? driveRoot = UsbDriveResolver.ResolveDriveLetter(location.VolumeSerial);
                    if (driveRoot is null)
                        return (400, new ErrorDto("That USB drive is not currently connected."));
                    destPath = Path.Combine(driveRoot, writeName);
                }
                else
                {
                    if (!string.Equals(location.Type, "smb", StringComparison.OrdinalIgnoreCase))
                        return (400, new ErrorDto("That location is not an SMB location."));
                    if (string.IsNullOrWhiteSpace(location.Share))
                        return (400, new ErrorDto("That location has no share configured."));
                    string? password;
                    try { password = SecretResolver.ResolvePassword(BackupScheduler.SmbPasswordEnvVar, location.SmbPasswordProtected); }
                    catch { return (400, new ErrorDto("That location's stored credentials could not be read.")); }
                    try { share = SmbShare.Connect(location.Share, location.SmbUser, password, createIfMissing: false); }
                    catch { return (400, new ErrorDto("That SMB share could not be reached.")); }
                    destPath = share.Combine(writeName);
                }
            }
        }
        catch
        {
            share?.Dispose();
            return (400, new ErrorDto("That destination could not be resolved."));
        }

        bool created = false;
        try
        {
            if (!overwrite && File.Exists(destPath))
                return (400, new ErrorDto("A file with that name already exists at the destination."));
            using var output = new FileStream(destPath,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            created = true;
            var (status, error) = StreamOfflineFile(config, session, folderName, filePath, output);
            if (status != 200)
            {
                output.Dispose();
                TryDelete(destPath);
                return (status, new ErrorDto(error ?? "The file could not be read."));
            }
            output.Flush();
            long bytes = output.Length;
            return (200, new OfflineSaveAsDto(writeName, destination, bytes));
        }
        catch (OperationCanceledException)
        {
            if (created) TryDelete(destPath);
            return (400, new ErrorDto("The transfer was interrupted because Offline Folders was locked."));
        }
        catch (IOException)
        {
            if (created) TryDelete(destPath);
            return (400, new ErrorDto("The file could not be written to the destination."));
        }
        catch
        {
            if (created) TryDelete(destPath);
            return (400, new ErrorDto("The file could not be written to the destination."));
        }
        finally
        {
            share?.Dispose();
        }
    }

    private static string LeafName(string filePath)
    {
        int slash = filePath.LastIndexOf('/');
        return slash >= 0 ? filePath[(slash + 1)..] : filePath;
    }

    private static bool IsValidLeafName(string name) =>
        name.Length > 0 && name != "." && name != ".." &&
        name.IndexOfAny(['/', '\\', '\0']) < 0;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort: a partial copy must not linger */ }
    }
}
