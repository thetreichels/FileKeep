using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
            string canonical = JsonSerializer.Serialize(this, CanonicalOptions);
            return Hashing.Sha256Hex(Encoding.UTF8.GetBytes(canonical));
        }
        finally
        {
            RootSha256 = saved;
        }
    }

    public bool VerifyRootHash() =>
        string.Equals(ComputeRootHash(), RootSha256, StringComparison.OrdinalIgnoreCase);

    public string ToJson() => JsonSerializer.Serialize(this, CanonicalOptions);

    public static BackupManifest FromJson(string json) =>
        JsonSerializer.Deserialize<BackupManifest>(json, CanonicalOptions)
        ?? throw new InvalidDataException("Manifest JSON deserialized to null.");
}
