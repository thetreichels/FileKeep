using System.Security.Cryptography;
using System.Text;
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
    public const string Format = "FileKeep offline export v1";

    /// <summary>Decrypted export payload: the folder, its location pointer, and its files.</summary>
    public sealed class Payload
    {
        [JsonPropertyName("folder")]
        public string Folder { get; set; } = "";

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
    /// <paramref name="location"/>. Throws
    /// <see cref="InvalidOperationException"/> for non-SMB/USB
    /// locations (offline folders store on USB/SMB only).
    /// </summary>
    public static OfflineExportPackageDto CreatePackage(
        OfflineFolderEntry folder, BackupLocation location, string exportPassphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(exportPassphrase);
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
        var payload = new Payload
        {
            Folder = folder.Name,
            Location = pointer,
            Files = folder.Files.Select(f => new FileRef
            {
                Path = f.Path,
                Size = f.Size,
                ModifiedUtc = f.ModifiedUtc,
                ChunkIds = f.ChunkIds.ToList(),
            }).ToList(),
        };
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] key = DeriveKey(exportPassphrase, salt, BackupRepository.DefaultKdfIterations);
        byte[] nonce = RandomNumberGenerator.GetBytes(ChunkCrypto.NonceSizeBytes);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[ChunkCrypto.TagSizeBytes];
        try
        {
            using (var gcm = new AesGcm(key, ChunkCrypto.TagSizeBytes))
                gcm.Encrypt(nonce, plaintext, ciphertext, tag);
            byte[] data = new byte[ciphertext.Length + tag.Length];
            ciphertext.CopyTo(data, 0);
            tag.CopyTo(data, ciphertext.Length);
            return new OfflineExportPackageDto(Format,
                Convert.ToBase64String(salt), BackupRepository.DefaultKdfIterations,
                Convert.ToBase64String(nonce), Convert.ToBase64String(data));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
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
        ArgumentException.ThrowIfNullOrEmpty(exportPassphrase);
        if (!string.Equals(package.Format, Format, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Unsupported export format '{package.Format}' (expected '{Format}') — refusing rather than garbling.");
        byte[] salt, nonce, data;
        try
        {
            salt = Convert.FromBase64String(package.KdfSaltB64);
            nonce = Convert.FromBase64String(package.NonceB64);
            data = Convert.FromBase64String(package.DataB64);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Export package encoding is malformed.", ex);
        }
        if (data.Length < ChunkCrypto.TagSizeBytes)
            throw new InvalidDataException("Export package data is too short.");
        byte[] key = DeriveKey(exportPassphrase, salt, package.KdfIterations);
        byte[] plaintext = new byte[data.Length - ChunkCrypto.TagSizeBytes];
        try
        {
            using (var gcm = new AesGcm(key, ChunkCrypto.TagSizeBytes))
                gcm.Decrypt(nonce,
                    data.AsSpan(0, plaintext.Length),
                    data.AsSpan(plaintext.Length, ChunkCrypto.TagSizeBytes),
                    plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(plaintext)
                ?? throw new InvalidDataException("Export payload is empty.");
            string? nameError = OfflineFoldersCatalog.ValidateName(payload.Folder);
            if (nameError is not null)
                throw new InvalidDataException($"Export folder name is invalid: {nameError}");
            if (payload.Location.Type is not ("smb" or "usb"))
                throw new InvalidDataException($"Export location type '{payload.Location.Type}' is not smb/usb.");
            foreach (var file in payload.Files)
                foreach (string chunkId in file.ChunkIds)
                {
                    try { _ = Hashing.HexToBytes(chunkId); }
                    catch (FormatException ex)
                    {
                        throw new InvalidDataException($"Export chunk id '{chunkId}' is not hex.", ex);
                    }
                }
            return payload;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Export payload is not valid JSON.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(passphrase, salt, iterations, HashAlgorithmName.SHA512);
        return pbkdf2.GetBytes(32);
    }
}
