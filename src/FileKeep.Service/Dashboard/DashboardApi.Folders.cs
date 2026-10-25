namespace FileKeep.Service;

/// <summary>
/// DashboardApi — server-side folder browsing for the dashboard's folder
/// picker (used in a plain browser, where the GUI's native folder dialog
/// is unavailable).
/// </summary>
public static partial class DashboardApi
{
    /// <summary>
    /// Lists a folder's subdirectories for GET /api/browse-folders.
    /// An empty path returns the drives view: every ready drive as a
    /// starting point, with no current folder. A non-empty path must name
    /// an existing directory; its immediate subdirectories (directories
    /// only, never files) come back sorted by name, along with the parent
    /// path — null at a drive root, where "up" means the drives view.
    /// User errors (path missing, path is a file, access denied) return
    /// 400 with a displayable message rather than a 500.
    /// Returns (200, BrowseFoldersDto) or (400/500, ErrorDto).
    /// </summary>
    public static (int Status, object Payload) BrowseFolders(string? path)
    {
        string home = SafeUserProfilePath();

        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = new List<FolderDto>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady) continue;
                    string driveRoot = d.RootDirectory.FullName;
                    string label = "";
                    try { label = d.VolumeLabel ?? ""; } catch { /* label is cosmetic */ }
                    drives.Add(new FolderDto(
                        string.IsNullOrWhiteSpace(label)
                            ? driveRoot
                            : $"{label} ({driveRoot.TrimEnd('\\', '/')})",
                        driveRoot));
                }
            }
            catch (Exception ex)
            {
                return (500, new ErrorDto(ex.Message));
            }
            drives.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return (200, new BrowseFoldersDto("", null, home, new List<FolderDto>(), drives));
        }

        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (400, new ErrorDto("Invalid path: " + path));
        }

        if (File.Exists(full))
            return (400, new ErrorDto("That path is a file, not a folder: " + full));
        if (!Directory.Exists(full))
            return (400, new ErrorDto("Folder not found: " + full));

        // Normalize: strip trailing separators except on the root itself,
        // so the displayed path and the parent computation are stable.
        string root = Path.GetPathRoot(full) ?? "";
        string current = full.Length > root.Length
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;

        List<FolderDto> folders;
        try
        {
            folders = new DirectoryInfo(current).EnumerateDirectories()
                .Select(d => new FolderDto(d.Name, d.FullName))
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return (400, new ErrorDto("Access denied: " + current));
        }
        catch (IOException ex)
        {
            return (400, new ErrorDto(ex.Message));
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto(ex.Message));
        }
        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        string? parent = Directory.GetParent(current)?.FullName;
        if (parent is not null && string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            parent = null;

        return (200, new BrowseFoldersDto(current, parent, home, folders, new List<FolderDto>()));
    }

    /// <summary>The signed-in user's profile folder, or "" when it cannot be determined.</summary>
    private static string SafeUserProfilePath()
    {
        try
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                return p;
        }
        catch { /* home is a convenience default; the drives view still works */ }
        return "";
    }
}
