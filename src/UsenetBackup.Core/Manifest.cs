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
    /// DL-8: HMAC-SHA-256 over the same canonical form the root hash
    /// covers, keyed by a subkey derived from the repo key. The plain
    /// root hash is a public checksum — anyone who can write to a
    /// repo (a share, a USB drive) could edit the file list and
    /// recompute it, silently shrinking what a restore brings back.
    /// The HMAC makes that edit detectable by anyone holding the
    /// passphrase, on every load path (LoadManifest verifies it
    /// whenever present). Additive and WhenWritingNull: manifests
    /// written before this field existed serialize and verify exactly
    /// as before, and are re-stamped opportunistically on load.
    /// </summary>
    [JsonPropertyName("root_hmac")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RootHmac { get; set; }

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
    /// The canonical JSON both root fields cover: this manifest with
    /// root_sha256 blanked and root_hmac omitted. Property declaration
    /// order is the serialization order, which makes the canonical
    /// form deterministic. Blanking the HMAC as well keeps the root
    /// hash stable across re-stamping — a legacy manifest's root hash
    /// verifies identically before and after it gains an HMAC.
    /// </summary>
    private string CanonicalJson()
    {
        string savedHash = RootSha256;
        string? savedHmac = RootHmac;
        try
        {
            RootSha256 = "";
            RootHmac = null;
            return FileKeepJsonOptions.Serialize(this,
                CanonicalOptions, FileKeepJsonContext.Default.BackupManifest);
        }
        finally
        {
            RootSha256 = savedHash;
            RootHmac = savedHmac;
        }
    }

    /// <summary>
    /// Computes root_sha256 = SHA-256 over the canonical JSON.
    /// </summary>
    public string ComputeRootHash() =>
        Hashing.Sha256Hex(Encoding.UTF8.GetBytes(CanonicalJson()));

    public bool VerifyRootHash() =>
        string.Equals(ComputeRootHash(), RootSha256, StringComparison.OrdinalIgnoreCase);

    private static readonly byte[] RootHmacDomainLabel =
        Encoding.UTF8.GetBytes("FileKeep manifest root_hmac v1");

    /// <summary>
    /// Computes root_hmac = HMAC-SHA-256 over the canonical JSON,
    /// keyed by SHA-256(repo key ‖ domain label) — a labeled subkey,
    /// so the raw repo key is never used directly as an HMAC key.
    /// </summary>
    public string ComputeRootHmac(byte[] repoKey)
    {
        byte[] subkeyInput = new byte[repoKey.Length + RootHmacDomainLabel.Length];
        Buffer.BlockCopy(repoKey, 0, subkeyInput, 0, repoKey.Length);
        Buffer.BlockCopy(RootHmacDomainLabel, 0, subkeyInput, repoKey.Length,
            RootHmacDomainLabel.Length);
        byte[] subkey = System.Security.Cryptography.SHA256.HashData(subkeyInput);
        byte[] mac = System.Security.Cryptography.HMACSHA256.HashData(
            subkey, Encoding.UTF8.GetBytes(CanonicalJson()));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    public bool VerifyRootHmac(byte[] repoKey)
    {
        if (RootHmac is null)
            return false;
        byte[] expected = Convert.FromHexString(ComputeRootHmac(repoKey));
        byte[] actual;
        try { actual = Convert.FromHexString(RootHmac); }
        catch (FormatException) { return false; }
        return System.Security.Cryptography.CryptographicOperations
            .FixedTimeEquals(expected, actual);
    }

    public string ToJson() => FileKeepJsonOptions.Serialize(this,
        CanonicalOptions, FileKeepJsonContext.Default.BackupManifest);

    public static BackupManifest FromJson(string json) =>
        FileKeepJsonOptions.Deserialize(json,
            CanonicalOptions, FileKeepJsonContext.Default.BackupManifest)
        ?? throw new InvalidDataException("Manifest JSON deserialized to null.");
}
