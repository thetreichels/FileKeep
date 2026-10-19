using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UsenetBackup.Core.Json;

namespace UsenetBackup.Core;

public sealed class FileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("mtime_utc")]
    public DateTime MtimeUtc { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("chunks")]
    public List<string> Chunks { get; set; } = new();

    [JsonPropertyName("symlink_target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SymlinkTarget { get; set; }
}

/// <summary>
/// One packed volume in a backup manifest: the set of chunks posted as a
/// single Usenet article. Additive: manifests uploaded per-chunk (v1) have
/// no volumes list.
/// </summary>
public sealed class VolumeEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("chunk_ids")]
    public List<string> ChunkIds { get; set; } = new();

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// NNTP post-set name this volume was uploaded in. Null for legacy
    /// (pre-batched-staging) backups, where the whole backup was one set.
    /// Batched backups post one set per batch; the NZB generator and parity
    /// reconstruction use this to keep per-batch subjects and parity groups
    /// aligned.
    /// </summary>
    [JsonPropertyName("set_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SetName { get; set; }
}

public sealed class BackupManifest
{
    [JsonPropertyName("format_version")]
    public string FormatVersion { get; set; } = "v1";

    [JsonPropertyName("backup_id")]
    public string BackupId { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "full";

    [JsonPropertyName("parent_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; set; }

    /// <summary>
    /// What was backed up: "directory" (default when absent, pre-v0.6 manifests)
    /// or "disk-image" (raw block device). Null is treated as "directory" and is
    /// not serialized, so old manifests verify unchanged.
    /// </summary>
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }

    /// <summary>
    /// Snapshot mechanism used during backup ("backup-privilege", ...).
    /// Null/absent means the live tree was read directly. Additive; old
    /// manifests verify unchanged.
    /// </summary>
    [JsonPropertyName("snapshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Snapshot { get; set; }

    /// <summary>True for raw block-device image backups (<see cref="Kind"/> == "disk-image").</summary>
    [JsonIgnore]
    public bool IsDiskImage => string.Equals(Kind, "disk-image", StringComparison.Ordinal);

    [JsonPropertyName("created_utc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("chunk_size")]
    public int ChunkSize { get; set; }

    /// <summary>
    /// Chunking scheme used for this backup: "fixed" or "cdc"
    /// (content-defined chunking). Null/absent means "fixed" (all v1
    /// manifests). Additive; old manifests verify unchanged, and restore
    /// never consults it — it only follows the chunk ID list.
    /// </summary>
    [JsonPropertyName("chunking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Chunking { get; set; }

    [JsonPropertyName("compression")]
    public string Compression { get; set; } = "none";

    [JsonPropertyName("files")]
    public List<FileEntry> Files { get; set; } = new();

    [JsonPropertyName("directories")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Directories { get; set; }

    /// <summary>
    /// Volume packing used for the Usenet upload of this backup (null when
    /// the backup was uploaded one article per chunk). Additive; old
    /// manifests verify unchanged.
    /// </summary>
    [JsonPropertyName("volumes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VolumeEntry>? Volumes { get; set; }

    /// <summary>
    /// Target volume size in bytes used when packing <see cref="Volumes"/>
    /// (informational). Null when volumes were not used.
    /// </summary>
    [JsonPropertyName("volume_size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? VolumeSize { get; set; }

    [JsonPropertyName("root_sha256")]
    public string RootSha256 { get; set; } = "";

    /// <summary>
    /// YubiKey HMAC-SHA1 challenge (base64) used to derive the decryption key.
    /// Not secret — posted with the backup so recovery can use a YubiKey
    /// without access to the original service.json. Null when YubiKey mode
    /// was not used for this backup.
    /// </summary>
    [JsonPropertyName("yubikey_challenge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? YubiKeyChallenge { get; set; }

    /// <summary>
    /// AES-256-GCM wrapped passphrase (base64), encrypted under the KEK
    /// derived from the YubiKey HMAC. Stored so bare-metal recovery can
    /// unwrap the passphrase with a YubiKey touch alone (no typing).
    /// Safe to store publicly — decryptable only with the physical key.
    /// </summary>
    [JsonPropertyName("yubikey_wrapped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? YubiKeyWrappedPassphrase { get; set; }

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = FileKeepJsonContext.Default,
    };

    /// <summary>
    /// Computes root_sha256 = SHA-256 over the canonical JSON of this manifest
    /// with the root_sha256 field itself excluded (it is serialized as "").
    /// Property declaration order is the serialization order, which makes the
    /// canonical form deterministic.
    /// </summary>
    public string ComputeRootHash()
    {
        string saved = RootSha256;
        try
        {
            RootSha256 = "";
            string canonical = FileKeepJsonOptions.Serialize(this,
                CanonicalOptions, FileKeepJsonContext.Default.BackupManifest);
            return Hashing.Sha256Hex(Encoding.UTF8.GetBytes(canonical));
        }
        finally
        {
            RootSha256 = saved;
        }
    }

    public bool VerifyRootHash() =>
        string.Equals(ComputeRootHash(), RootSha256, StringComparison.OrdinalIgnoreCase);

    public string ToJson() => FileKeepJsonOptions.Serialize(this,
        CanonicalOptions, FileKeepJsonContext.Default.BackupManifest);

    public static BackupManifest FromJson(string json) =>
        FileKeepJsonOptions.Deserialize(json,
            CanonicalOptions, FileKeepJsonContext.Default.BackupManifest)
        ?? throw new InvalidDataException("Manifest JSON deserialized to null.");
}
