namespace FileKeep.Core;

/// <summary>
/// On-stick layout for the portable reader (Stealth Folders slice 3,
/// part 3) and for full exports (a manifest package plus its chunks).
/// One clearly named folder on the stick, so a foreign machine can
/// find it without knowing anything about the main machine:
///
///   FileKeepReader\manifest.filekeep-export.json   (the encrypted
///       package: folder name, location pointer, file manifest —
///       never credentials, never plaintext filenames outside the
///       encrypted blob)
///   FileKeepReader\chunks\&lt;hh&gt;\&lt;rest&gt;   (full exports
///       only: encrypted chunk blobs, chunk ID as filename, exactly
///       as a plain <see cref="LocalBlobStore"/> stores them, so the
///       reader reads them with that store and no new format)
///
/// Manifest-only exports are just the package file: the same file
/// the dashboard downloads, copied onto the stick. A disguised
/// location's chunks still travel this way — the reader folder is
/// explicitly a FileKeep folder, not camouflage, so it is findable;
/// tucking a manifest into a disguised cover folder is NOT done
/// (there is no disguised manifest filename the main app writes,
/// and inventing one the reader guesses at would be fake).
///
/// Discovery (see <see cref="DiscoverPackages"/>): the manifest
/// inside FileKeepReader\, then any *.filekeep-export.json inside
/// FileKeepReader\, then any at the stick root — so a package the
/// user simply dropped on the stick is found too.
/// </summary>
public static class OfflineStickLayout
{
    /// <summary>Folder name on the stick (and in a recovery image).</summary>
    public const string ReaderFolderName = "FileKeepReader";

    /// <summary>Manifest package filename inside the reader folder.</summary>
    public const string ManifestFileName = "manifest.filekeep-export.json";

    /// <summary>Chunk subfolder inside the reader folder (full exports).</summary>
    public const string ChunksFolderName = "chunks";

    /// <summary>Full path of the reader folder under <paramref name="stickRoot"/>.</summary>
    public static string ReaderFolder(string stickRoot) =>
        Path.Combine(stickRoot, ReaderFolderName);

    /// <summary>Full path of the manifest package inside the reader folder.</summary>
    public static string ManifestPath(string stickRoot) =>
        Path.Combine(ReaderFolder(stickRoot), ManifestFileName);

    /// <summary>
    /// Finds candidate export packages on a stick, best first:
    /// the reader folder's manifest, then any package inside the
    /// reader folder, then any at the stick root. Empty when none
    /// are present. Never throws for a missing root.
    /// </summary>
    public static IReadOnlyList<string> DiscoverPackages(string stickRoot)
    {
        var found = new List<string>();
        void AddIfFile(string path)
        {
            if (File.Exists(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
                found.Add(path);
        }
        try
        {
            string readerFolder = ReaderFolder(stickRoot);
            AddIfFile(Path.Combine(readerFolder, ManifestFileName));
            if (Directory.Exists(readerFolder))
                foreach (string f in Directory.EnumerateFiles(readerFolder, "*.filekeep-export.json"))
                    AddIfFile(f);
            if (Directory.Exists(stickRoot))
                foreach (string f in Directory.EnumerateFiles(stickRoot, "*.filekeep-export.json"))
                    AddIfFile(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stick that cannot be listed has no discoverable packages.
        }
        return found;
    }

    /// <summary>
    /// Writes a package file atomically (temp file in the same
    /// folder + move), so a reader PUT/DELETE that re-encrypts the
    /// package never leaves a half-written manifest behind.
    /// </summary>
    public static void WritePackageAtomic(string path, string json)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }
}
