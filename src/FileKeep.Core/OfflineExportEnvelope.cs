using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKeep.Core;

/// <summary>
/// Offline Folders export envelope (slice 3, part 2), in Core so the
/// portable reader (FileKeep.Reader, which must NOT reference
/// FileKeep.Service) can consume the exact same format.
/// <see cref="FileKeep.Service.OfflineExport"/> is a thin adapter over
/// this type: it maps its dashboard-facing payload/DTO types to these
/// Core types and back, so there is exactly ONE envelope
/// implementation and ONE set of crypto — no duplicated format that
/// could drift. The format is unchanged from the Service original:
/// "FileKeep offline export v1", PBKDF2-HMAC-SHA512 + AES-256-GCM,
/// nonce split out for JSON readability, data = ciphertext ‖ tag.
/// The reader (part 3) and the Service both use this type, so a
/// package written by either decrypts under the other — proven by
/// the format-compatibility tests.
///
/// What is NOT in the package, deliberately: no credentials (no
/// NNTP/SMB passwords, no SMB username — the reader re-enters
/// credentials on the spot) and no filenames in plaintext (names
/// live only inside the encrypted payload). The export passphrase
/// is a fresh user-chosen secret, never a stored credential, and
/// must never be logged.
///
/// Folder-name validation here mirrors
/// OfflineFoldersCatalog.ValidateName (whitespace, length, control
/// characters, separators) without depending on Service types; the
/// error text may differ from the Service adapter's, but the
/// exception type (InvalidDataException) and the fail-closed
/// behavior are identical.
/// </summary>
public static class OfflineExportEnvelope
{
    public const string Format = "FileKeep offline export v1";

    /// <summary>Package scope: a whole-folder manifest.</summary>
    public const string ScopeFolder = "folder";

    /// <summary>Package scope: exactly one file's entry, for per-file export.</summary>
    public const string ScopeFile = "file";

    /// <summary>Decrypted export payload: the folder, its location pointer, and its files.</summary>
    public sealed class Payload
    {
        [JsonPropertyName("folder")]
        public string Folder { get; set; } = "";

        /// <summary>
        /// Package scope: "folder" (the whole manifest) or "file"
        /// (exactly one entry). Absent in older packages, which read
        /// as folders; anything else refuses fail-closed on decrypt.
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

    /// <summary>Encrypted envelope, as serialized to the .filekeep-export.json file (snake_case, like the Service DTO).</summary>
    public sealed record Package(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("kdf_salt_b64")] string KdfSaltB64,
        [property: JsonPropertyName("kdf_iterations")] int KdfIterations,
        [property: JsonPropertyName("nonce_b64")] string NonceB64,
        [property: JsonPropertyName("data_b64")] string DataB64);

    /// <summary>
    /// Encrypts <paramref name="payload"/> under <paramref name="exportPassphrase"/>.
    /// Property declaration order above matches the original Service
    /// payload exactly, so the serialized plaintext (and therefore
    /// the format) is unchanged.
    /// </summary>
    public static Package CreatePackage(Payload payload, string exportPassphrase)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrEmpty(exportPassphrase);
        // Source-generated metadata (FileKeepJsonContext): trimmed
        // single-file builds disable reflection serialization, so a
        // reflection Serialize here breaks the trimmed service and
        // the trimmed reader alike (found by the reader's trimmed
        // publish smoke test, 2026-10-28).
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload,
            Json.FileKeepJsonContext.Default.Payload);
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
            return new Package(Format,
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
    public static Payload DecryptPackage(Package package, string exportPassphrase)
    {
        ArgumentNullException.ThrowIfNull(package);
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
            var payload = JsonSerializer.Deserialize(plaintext,
                    Json.FileKeepJsonContext.Default.Payload)
                ?? throw new InvalidDataException("Export payload is empty.");
            string? nameError = ValidateFolderName(payload.Folder);
            if (nameError is not null)
                throw new InvalidDataException($"Export folder name is invalid: {nameError}");
            if (payload.Location.Type is not ("smb" or "usb"))
                throw new InvalidDataException($"Export location type '{payload.Location.Type}' is not smb/usb.");
            if (payload.Scope is not null and not (ScopeFolder or ScopeFile))
                throw new InvalidDataException($"Export package scope '{payload.Scope}' is not folder/file.");
            // Older packages carry no scope; they are whole-folder packages.
            payload.Scope ??= ScopeFolder;
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

    /// <summary>Parses an envelope from its JSON text without decrypting. Returns null when not an export envelope.</summary>
    public static Package? TryParseEnvelope(string json)
    {
        try
        {
            var package = JsonSerializer.Deserialize(json,
                Json.FileKeepJsonContext.Default.Package);
            if (package is null || string.IsNullOrWhiteSpace(package.Format))
                return null;
            return package;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ValidateFolderName(string? name)
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

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(passphrase, salt, iterations, HashAlgorithmName.SHA512);
        return pbkdf2.GetBytes(32);
    }
}
