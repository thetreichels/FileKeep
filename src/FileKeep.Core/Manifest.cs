using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileKeep.Core.Json;

namespace FileKeep.Core;

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
    /// NNTP post-set name this volume was uploaded in. Null for volumes
    /// posted by a single-set (non-batched) upload — the CLI/scheduler
    /// single-set paths and the streaming path record no set name, and
    /// the whole backup is one set. Batched backups post one set per
    /// batch and stamp each volume with its set name; the NZB generator
    /// and parity reconstruction use this to keep per-batch subjects
    /// and parity groups aligned.
    /// </summary>
    [JsonPropertyName("set_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SetName { get; set; }
}

/// <summary>
/// One provider's parity record for a backup (P2-V2a): the redundancy
/// mode and XOR group size used at upload time, plus the parity group
/// member lists exactly as the parity block IDs were derived from
/// them (chunk IDs in chunk mode, volume IDs in volume mode; each
/// inner list is one MakeParityId input). Retention enumerates parity
/// article IDs from these groups and regenerates missing blocks from
/// the members' bytes — no derivation from config or batch layout is
/// needed, so the record cannot drift from what was actually posted.
/// </summary>
public sealed class ManifestParityRecord
{
    /// <summary>"xor", "par2", or "none" (explicit: no parity by design).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "none";

    /// <summary>XOR group size used at upload (PAR2's is fixed at 10).</summary>
    [JsonPropertyName("group_size")]
    public int GroupSize { get; set; }

    [JsonPropertyName("groups")]
    public List<List<string>> Groups { get; set; } = new();
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

    /// <summary>
    /// P2-V2a: the Usenet parity this backup was uploaded with, per
    /// provider (key = ChunkMessageIndex.MakeProviderKey: "host/newsgroup").
    /// Parity composition — redundancy mode, XOR group size, and the
    /// group member lists the parity block IDs were derived from — used
    /// to be upload-time knowledge only (job/provider config), so
    /// retention could never enumerate, health-check, or regenerate
    /// parity articles: data articles refreshed while parity silently
    /// aged out and the reconstruction safety net disappeared. Recorded
    /// by the upload paths and stamped via SaveManifest before the
    /// manifest article is posted. Additive and WhenWritingNull: legacy
    /// manifests have no record and retention treats them exactly as
    /// before (parity unrefreshable — the information was never kept).
    /// </summary>
    [JsonPropertyName("parity_by_provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ManifestParityRecord>? ParityByProvider { get; set; }

    /// <summary>
    /// P2-V2b: the posting date ("yyyy-MM-dd") each batch's NFO article
    /// was built with, by batch index (null entries = no NFO posted for
    /// that batch). Batched uploads build each batch's NFO with that
    /// day's date; without this record retention could only rebuild the
    /// single-set NFO (whose date is the backup's creation date).
    /// Additive; when absent, retention falls back to the creation
    /// date for every batch.
    /// </summary>
    [JsonPropertyName("nfo_dates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string?>? NfoDates { get; set; }

    [JsonPropertyName("root_sha256")]
    public string RootSha256 { get; set; } = "";

    /// <summary>
    /// DL-8: HMAC-SHA-256 over the same canonical form the root hash
    /// covers, keyed by a subkey derived from the repo key. The plain
    /// root hash is a public checksum — anyone who can write to a
    /// repo (a share, a USB drive) could edit the file list and
    /// recompute it, silently shrinking what a restore brings back.
    /// The HMAC makes that edit detectable by anyone holding the
    /// passphrase, on every load path: LoadManifest and ImportManifest
    /// require it and verify it — a manifest without one uses the
    /// pre-release format and is refused (legacy repo support was
    /// dropped before the first release). Every write path stamps it
    /// (WriteManifest is the single funnel), so a persisted manifest
    /// always carries one.
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
    /// form deterministic. Blanking both fields keeps the root hash a
    /// pure function of the manifest content, independent of the stamp
    /// it carries.
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
