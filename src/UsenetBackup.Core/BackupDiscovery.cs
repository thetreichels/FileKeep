using System.Text.Json;

namespace UsenetBackup.Core;

/// <summary>
/// Scans a filesystem path (local directory, SMB share, or USB drive) for
/// FileKeep backup repositories. A FileKeep repo is identified by a
/// <c>manifests/</c> directory containing <c>&lt;backupId&gt;.json</c> files
/// (the same layout <see cref="SmbSync"/> writes to shares).
///
/// Manifests are plain JSON at rest (they're only encrypted for Usenet
/// transport), so backup metadata can be read without a passphrase.
/// </summary>
public static class BackupDiscovery
{
    public sealed record DiscoveredBackup(
        string BackupId,
        string? Type,
        DateTime? CreatedUtc,
        string? Source,
        string LocationPath);

    /// <summary>
    /// Scans <paramref name="path"/> for FileKeep backups. Returns the
    /// backups found in <c>manifests/*.json</c> under that path.
    /// Returns an empty list if the path isn't a FileKeep repo.
    /// </summary>
    public static IReadOnlyList<DiscoveredBackup> ScanPath(string path, Action<string>? log = null)
    {
        var found = new List<DiscoveredBackup>();
        if (string.IsNullOrWhiteSpace(path))
            return found;

        string manifestsDir = Path.Combine(path, "manifests");
        if (!Directory.Exists(manifestsDir))
        {
            log?.Invoke($"BackupDiscovery: no manifests/ directory at {path} — not a FileKeep repo.");
            return found;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(manifestsDir, "*.json");
        }
        catch (Exception ex)
        {
            log?.Invoke($"BackupDiscovery: cannot list manifests at {manifestsDir}: {ex.Message}");
            return found;
        }

        foreach (string file in files)
        {
            string backupId = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(backupId))
                continue;

            // Skip temp files from atomic writes.
            if (backupId.Contains(".tmp-", StringComparison.Ordinal))
                continue;

            string? type = null;
            DateTime? createdUtc = null;
            string? source = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                    type = t.GetString();
                if (root.TryGetProperty("created_utc", out var c) && c.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(c.GetString(), out var dt))
                    createdUtc = dt;
                if (root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
                    source = s.GetString();
            }
            catch (Exception ex)
            {
                log?.Invoke($"BackupDiscovery: cannot parse manifest {file}: {ex.Message}");
            }

            found.Add(new DiscoveredBackup(backupId, type, createdUtc, source, path));
        }

        log?.Invoke($"BackupDiscovery: found {found.Count} backup(s) at {path}.");
        return found;
    }

    /// <summary>
    /// Scans an SMB share for FileKeep backups. Connects with the given
    /// credentials (read-only: does not create missing directories).
    /// </summary>
    public static IReadOnlyList<DiscoveredBackup> ScanSmbShare(
        string uncPath, string? username, string? password, Action<string>? log = null)
    {
        try
        {
            using var share = SmbShare.Connect(uncPath, username, password, createIfMissing: false);
            return ScanPath(share.UncPath, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"BackupDiscovery: cannot access SMB share {uncPath}: {ex.Message}");
            return Array.Empty<DiscoveredBackup>();
        }
    }
}
