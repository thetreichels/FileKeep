using System.Text.Json;
using System.Text.Json.Serialization;
using FileKeep.Core;
using FileKeep.Core.Service;

namespace FileKeep.Service;

/// <summary>
/// Offline Folders export package (slice 3, part 2): one folder's
/// manifest plus a location pointer, encrypted under a passphrase
/// the user chooses at export time. The reader (part 3) consumes
/// this format, so it is versioned and fail-closed: a mismatched
/// format refuses rather than garbling.
///
/// This class is now a thin adapter over
/// <see cref="OfflineExportEnvelope"/> in Core (the reader must NOT
/// reference Service, so the envelope implementation lives there).
/// The format, the payload shapes, and the public API here are
/// unchanged — the Service payload is mapped to the Core payload,
/// encrypted by Core, and mapped back — so existing packages and
/// tests behave exactly as before, and a package written by the
/// Service decrypts under the reader and vice versa.
///
/// Envelope (JSON, snake_case like repo.json — this is a file
/// format, not a dashboard response): format tag, PBKDF2 salt +
/// iterations, nonce, and data = ciphertext ‖ tag (AES-256-GCM,
/// the same nonce‖ciphertext‖tag pattern as the catalog blob, with
/// the nonce split out for JSON readability).
///
/// What is NOT in the package, deliberately: no credentials
/// (no NNTP/SMB passwords, no SMB username — the reader re-enters
/// credentials on the spot) and no filenames in plaintext (names
/// live only inside the encrypted payload — an unencrypted export
/// would be a plaintext list of everything stored, undoing the
/// encrypted catalog). The export passphrase is a fresh
/// user-chosen secret, never a stored credential, and must never
/// be logged.
/// </summary>
public static class OfflineExport
{
    public const string Format = OfflineExportEnvelope.Format;

    /// <summary>Package scope: a whole-folder manifest.</summary>
    public const string ScopeFolder = OfflineExportEnvelope.ScopeFolder;

    /// <summary>Package scope: exactly one file's entry, for per-file export.</summary>
    public const string ScopeFile = OfflineExportEnvelope.ScopeFile;

    /// <summary>Decrypted export payload: the folder, its location pointer, and its files.</summary>
    public sealed class Payload
    {
        [JsonPropertyName("folder")]
        public string Folder { get; set; } = "";

        /// <summary>
        /// Package scope: "folder" or "file" (see
        /// <see cref="OfflineExportEnvelope"/>). Absent in older
        /// packages, which read as folders.
        /// </summary>
        [JsonPropertyName("scope")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Scope { get; set; }

        [JsonPropertyName("location")]
        public LocationPointer Location { get; set; } = new();

        [JsonPropertyName("files")]
        public List<FileRef> Files { get; set; } = new();
    }

    /// <summary>
    /// Location pointer: enough for the reader to find the chunks,
    /// never enough to authenticate. SMB carries the share path;
    /// USB carries the volume serial + folder. Passwords and SMB
    /// usernames are never included.
    /// </summary>
    public sealed class LocationPointer
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("share")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Share { get; set; }

        [JsonPropertyName("volume_serial")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? VolumeSerial { get; set; }

        [JsonPropertyName("volume_label")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? VolumeLabel { get; set; }

        [JsonPropertyName("usb_folder")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UsbFolder { get; set; }

        [JsonPropertyName("disguise_enabled")]
        public bool DisguiseEnabled { get; set; }

        [JsonPropertyName("cover_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CoverName { get; set; }

        [JsonPropertyName("disguise_decoys")]
        public bool DisguiseDecoys { get; set; }
    }

    /// <summary>One file's manifest entry: relative path, size, mtime, chunk references.</summary>
    public sealed class FileRef
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("modified_utc")]
        public DateTimeOffset ModifiedUtc { get; set; }

        [JsonPropertyName("chunk_ids")]
        public List<string> ChunkIds { get; set; } = new();
    }

    /// <summary>
    /// Builds an encrypted package for <paramref name="folder"/> on
    /// <paramref name="location"/>, containing all of the folder's
    /// files. Throws <see cref="InvalidOperationException"/> for
    /// non-SMB/USB locations (offline folders store on USB/SMB only).
    /// </summary>
    public static OfflineExportPackageDto CreatePackage(
        OfflineFolderEntry folder, BackupLocation location, string exportPassphrase) =>
        BuildPackage(folder.Name, BuildPointer(folder, location),
            folder.Files.Select(f => new OfflineExportEnvelope.FileRef
            {
                Path = f.Path,
                Size = f.Size,
                ModifiedUtc = f.ModifiedUtc,
                ChunkIds = f.ChunkIds.ToList(),
            }).ToList(), ScopeFolder, exportPassphrase);

    /// <summary>
    /// Builds an encrypted package holding exactly one file's entry
    /// from <paramref name="folder"/>: the same v1 envelope and the
    /// same location pointer, so the reader (part 3) and the import
    /// path treat it as a package — but no other file's name or
    /// chunk reference leaves the machine. The scope is "file"; the
    /// import side merges it into the named folder.
    /// </summary>
    public static OfflineExportPackageDto CreateFilePackage(
        OfflineFolderEntry folder, OfflineFileEntry file, BackupLocation location, string exportPassphrase) =>
        BuildPackage(folder.Name, BuildPointer(folder, location),
            new List<OfflineExportEnvelope.FileRef>
            {
                new()
                {
                    Path = file.Path,
                    Size = file.Size,
                    ModifiedUtc = file.ModifiedUtc,
                    ChunkIds = file.ChunkIds.ToList(),
                },
            }, ScopeFile, exportPassphrase);

    private static OfflineExportPackageDto BuildPackage(
        string folderName, LocationPointer pointer,
        List<OfflineExportEnvelope.FileRef> files, string scope, string exportPassphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(exportPassphrase);
        var payload = new OfflineExportEnvelope.Payload
        {
            Folder = folderName,
            Scope = scope,
            Location = new OfflineExportEnvelope.LocationPointer
            {
                Type = pointer.Type,
                Name = pointer.Name,
                Share = pointer.Share,
                VolumeSerial = pointer.VolumeSerial,
                VolumeLabel = pointer.VolumeLabel,
                UsbFolder = pointer.UsbFolder,
                DisguiseEnabled = pointer.DisguiseEnabled,
                CoverName = pointer.CoverName,
                DisguiseDecoys = pointer.DisguiseDecoys,
            },
            Files = files,
        };
        var package = OfflineExportEnvelope.CreatePackage(payload, exportPassphrase);
        return new OfflineExportPackageDto(package.Format, package.KdfSaltB64,
            package.KdfIterations, package.NonceB64, package.DataB64);
    }

    private static LocationPointer BuildPointer(OfflineFolderEntry folder, BackupLocation location)
    {
        var pointer = new LocationPointer
        {
            Type = location.Type.ToLowerInvariant(),
            Name = location.Name,
            DisguiseEnabled = location.DisguiseEnabled,
            DisguiseDecoys = location.DisguiseDecoys,
            CoverName = string.IsNullOrWhiteSpace(location.CoverName) ? null : location.CoverName,
        };
        if (pointer.Type == "smb")
        {
            pointer.Share = location.Share;
        }
        else if (pointer.Type == "usb")
        {
            pointer.VolumeSerial = location.VolumeSerial;
            pointer.VolumeLabel = string.IsNullOrWhiteSpace(location.VolumeLabel) ? null : location.VolumeLabel;
            pointer.UsbFolder = location.UsbFolder;
        }
        else
        {
            throw new InvalidOperationException(
                $"Offline folder '{folder.Name}' is on a '{location.Type}' location — exports cover USB/SMB only.");
        }
        return pointer;
    }

    /// <summary>
    /// Decrypts and validates a package. Throws
    /// <see cref="InvalidDataException"/> for a wrong format, bad
    /// encoding, or invalid payload, and
    /// <see cref="CryptographicException"/> for a wrong passphrase
    /// or tampered data.
    /// </summary>
    public static Payload DecryptPackage(OfflineExportPackageDto package, string exportPassphrase)
    {
        var core = OfflineExportEnvelope.DecryptPackage(
            new OfflineExportEnvelope.Package(package.Format, package.KdfSaltB64,
                package.KdfIterations, package.NonceB64, package.DataB64),
            exportPassphrase);
        return new Payload
        {
            Folder = core.Folder,
            Scope = core.Scope,
            Location = new LocationPointer
            {
                Type = core.Location.Type,
                Name = core.Location.Name,
                Share = core.Location.Share,
                VolumeSerial = core.Location.VolumeSerial,
                VolumeLabel = core.Location.VolumeLabel,
                UsbFolder = core.Location.UsbFolder,
                DisguiseEnabled = core.Location.DisguiseEnabled,
                CoverName = core.Location.CoverName,
                DisguiseDecoys = core.Location.DisguiseDecoys,
            },
            Files = core.Files.Select(f => new FileRef
            {
                Path = f.Path,
                Size = f.Size,
                ModifiedUtc = f.ModifiedUtc,
                ChunkIds = f.ChunkIds.ToList(),
            }).ToList(),
        };
    }
}
