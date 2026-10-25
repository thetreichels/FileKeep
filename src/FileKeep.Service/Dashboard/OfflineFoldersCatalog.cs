using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FileKeep.Service;

/// <summary>
/// One entry in the Offline Folders catalog: a display name the user
/// chose, and the BackupLocation it is stored on. Locations are keyed
/// by name (the unique key BackupLocation itself uses); <see cref="LocationId"/>
/// holds that name.
/// </summary>
public sealed record OfflineFolderEntry(string Name, string LocationId, DateTimeOffset AddedUtc)
{
    /// <summary>
    /// Files stored in this folder (slice 2). The list is replaced, never
    /// mutated in place, when a file is added, updated, or removed, so a
    /// session snapshot cannot observe a half-updated index.
    /// </summary>
    public List<OfflineFileEntry> Files { get; set; } = new();
}

/// <summary>
/// One file stored in an offline folder (slice 2). <see cref="Path"/> is
/// a '/'-separated relative path inside the folder (nested paths are
/// "sub/name" with each segment validated). <see cref="ChunkIds"/> are
/// the content-addressed chunk IDs (lowercase hex SHA-256 of each
/// plaintext chunk) that reassemble the file from the folder's
/// location; the file's bytes live ONLY there and in the encrypted
/// catalog's index — never as plaintext on this machine.
/// </summary>
public sealed record OfflineFileEntry(
    string Path, long Size, DateTimeOffset ModifiedUtc, IReadOnlyList<string> ChunkIds);

/// <summary>
/// The Offline Folders catalog store (slice 1: locked card + encrypted
/// catalog).
///
/// The file on disk is AES-256-GCM ciphertext ONLY: a single base64
/// string of nonce ‖ ciphertext ‖ tag, with no JSON envelope, no
/// plaintext names, and no metadata of any kind around it. The
/// plaintext inside is a small versioned JSON document
/// (<c>{"version":2,"entries":[...]}</c>; version 1 catalogs, which
/// carry folders without file indexes, still load). The encryption key is a
/// domain-separated subkey of the vault root secret — the same pattern
/// as the manifest <c>root_hmac</c> and the Usenet index auth:
/// SHA-256(secret ‖ "FileKeep offline folders catalog v1"). The raw
/// root secret is never stored and never used directly as a key.
///
/// Consequences worth stating plainly: the catalog is bound to the
/// vault secret it was created under. If that secret is later changed
/// (passphrase replaced), the catalog no longer decrypts and unlock
/// reports exactly that — there is no silent fallback and no recovery
/// inside slice 1 beyond deleting the catalog file and starting over.
/// </summary>
public static class OfflineFoldersCatalog
{
    /// <summary>Domain label for the catalog key subkey (DL-8 pattern).</summary>
    public const string KeyDomainLabel = "FileKeep offline folders catalog v1";

    /// <summary>File name for the catalog, next to service.json.</summary>
    public const string FileName = "offline-folders.json";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>
    /// Catalog format version written by Save. Version 1 catalogs
    /// (folders without file indexes) are still readable: Load maps
    /// them to entries with empty <see cref="OfflineFolderEntry.Files"/>.
    /// </summary>
    private const int CatalogVersion = 2;

    /// <summary>Catalog path for a service config path (same directory).</summary>
    public static string CatalogPathFor(string configPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        if (string.IsNullOrEmpty(dir)) dir = ".";
        return Path.Combine(dir, FileName);
    }

    /// <summary>Derives the catalog key from the vault root secret.</summary>
    public static byte[] DeriveKey(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] labelBytes = Encoding.UTF8.GetBytes(KeyDomainLabel);
        byte[] input = new byte[secretBytes.Length + labelBytes.Length];
        try
        {
            Buffer.BlockCopy(secretBytes, 0, input, 0, secretBytes.Length);
            Buffer.BlockCopy(labelBytes, 0, input, secretBytes.Length, labelBytes.Length);
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    /// <summary>
    /// Validates a catalog display name. Returns the error message, or
    /// null when valid. Names are display-only, but '/' and '\' are
    /// rejected: the delete route addresses entries by name in the URL
    /// path, and a separator in a name would make that entry
    /// unreachable for deletion.
    /// </summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a folder name.";
        string trimmed = name.Trim();
        if (trimmed.Length > 128)
            return "Folder names are at most 128 characters.";
        if (trimmed.Any(char.IsControl))
            return "Folder names cannot contain control characters.";
        if (trimmed.Contains('/') || trimmed.Contains('\\'))
            return "Folder names cannot contain '/' or '\\'.";
        return null;
    }

    /// <summary>
    /// Validates a '/'-separated relative file path inside an offline
    /// folder. Returns the error message, or null when valid. Each
    /// segment is validated on its own (nested paths are "sub/name");
    /// segments may not be empty, ".", "..", or contain control
    /// characters, backslashes, or Windows-invalid filename characters.
    /// File names are never used as filesystem paths by this feature —
    /// they are catalog keys only — but the same strictness keeps them
    /// safe for the WebDAV href mapping and for any future use.
    /// </summary>
    public static string? ValidateFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Enter a file name.";
        string trimmed = path.Trim();
        if (trimmed.Length > 512)
            return "File paths are at most 512 characters.";
        if (trimmed.StartsWith('/') || trimmed.EndsWith('/'))
            return "File paths must not start or end with '/'.";
        const string windowsInvalid = "<>:\"|?*";
        foreach (string segment in trimmed.Split('/'))
        {
            if (segment.Length == 0)
                return "File paths cannot contain empty segments.";
            if (segment is "." or "..")
                return "File paths cannot contain '.' or '..' segments.";
            if (segment.Length > 128)
                return "File path segments are at most 128 characters.";
            if (segment.Any(char.IsControl) || segment.Contains('\\') ||
                segment.Any(c => windowsInvalid.Contains(c)))
                return "File names cannot contain control characters or <>:\"|?*\\.";
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                return "File path segments must not end with a space or a period.";
        }
        return null;
    }

    /// <summary>
    /// Persists entries under <paramref name="key"/>, atomically
    /// (temp file + move) so a torn write never leaves a half catalog.
    /// The plaintext is zeroed after encryption.
    /// </summary>
    public static void Save(string catalogPath, byte[] key, IReadOnlyList<OfflineFolderEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("Catalog key must be 32 bytes.", nameof(key));
        var doc = new JsonObject { ["version"] = CatalogVersion };
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var filesArr = new JsonArray();
            foreach (var f in e.Files)
            {
                var chunksArr = new JsonArray();
                foreach (string chunkId in f.ChunkIds)
                    chunksArr.Add(chunkId);
                filesArr.Add(new JsonObject
                {
                    ["path"] = f.Path,
                    ["size"] = f.Size,
                    ["modifiedUtc"] = f.ModifiedUtc.ToString("O"),
                    ["chunks"] = chunksArr,
                });
            }
            arr.Add(new JsonObject
            {
                ["name"] = e.Name,
                ["locationId"] = e.LocationId,
                ["addedUtc"] = e.AddedUtc.ToString("O"),
                ["files"] = filesArr,
            });
        }
        doc["entries"] = arr;
        byte[] plaintext = Encoding.UTF8.GetBytes(doc.ToJsonString());
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Encrypt(nonce, plaintext, ciphertext, tag);
            byte[] blob = new byte[NonceSize + ciphertext.Length + TagSize];
            Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
            Buffer.BlockCopy(ciphertext, 0, blob, NonceSize, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, blob, NonceSize + ciphertext.Length, TagSize);
            string tempPath = catalogPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, Convert.ToBase64String(blob));
                File.Move(tempPath, catalogPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    /// <summary>
    /// Loads and decrypts the catalog. Throws
    /// <see cref="CryptographicException"/> when the key does not
    /// decrypt the file (wrong vault secret or damaged file),
    /// <see cref="FormatException"/> on malformed content, and
    /// <see cref="InvalidDataException"/> on a version mismatch.
    /// Plaintext is zeroed before returning.
    /// </summary>
    public static List<OfflineFolderEntry> Load(string catalogPath, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("Catalog key must be 32 bytes.", nameof(key));
        byte[] blob;
        try { blob = Convert.FromBase64String(File.ReadAllText(catalogPath).Trim()); }
        catch (FormatException ex) { throw new CryptographicException("Catalog file is not valid ciphertext.", ex); }
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Catalog file is too short to be valid ciphertext.");
        byte[] nonce = blob[..NonceSize];
        byte[] ciphertext = blob[NonceSize..^TagSize];
        byte[] tag = blob[^TagSize..];
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            JsonNode? node = JsonNode.Parse(Encoding.UTF8.GetString(plaintext));
            var obj = node as JsonObject
                ?? throw new FormatException("Catalog plaintext is not a JSON object.");
            int? version = (int?)obj["version"];
            if (version is not (1 or 2))
                throw new InvalidDataException("Unsupported offline folders catalog version.");
            var entries = new List<OfflineFolderEntry>();
            if (obj["entries"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject e) continue;
                    string name = (string?)e["name"] ?? "";
                    string locationId = (string?)e["locationId"] ?? "";
                    string added = (string?)e["addedUtc"] ?? "";
                    if (!DateTimeOffset.TryParse(added, out var addedUtc))
                        addedUtc = DateTimeOffset.UtcNow;
                    var entry = new OfflineFolderEntry(name, locationId, addedUtc);
                    // Version 1 catalogs carry no file index; they load
                    // as folders with no files, and Save rewrites them
                    // as version 2 on the next persist.
                    if (version == 2 && e["files"] is JsonArray filesArr)
                    {
                        foreach (var fitem in filesArr)
                        {
                            if (fitem is not JsonObject f) continue;
                            string path = (string?)f["path"] ?? "";
                            long size = (long?)f["size"] ?? 0;
                            string modified = (string?)f["modifiedUtc"] ?? "";
                            if (!DateTimeOffset.TryParse(modified, out var modifiedUtc))
                                modifiedUtc = DateTimeOffset.UtcNow;
                            var chunks = new List<string>();
                            if (f["chunks"] is JsonArray chunksArr)
                            {
                                foreach (var c in chunksArr)
                                {
                                    if (c is not null && c.GetValueKind() == System.Text.Json.JsonValueKind.String)
                                        chunks.Add(c.GetValue<string>());
                                }
                            }
                            entry.Files.Add(new OfflineFileEntry(path, size, modifiedUtc, chunks));
                        }
                    }
                    entries.Add(entry);
                }
            }
            return entries;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(blob);
        }
    }
}
