using System.Text.Json.Serialization;

namespace UsenetBackup.Core;

/// <summary>Contents of repo.json at the repository root (BACKUP FORMAT v1).</summary>
public sealed class RepositoryConfig
{
    [JsonPropertyName("format_version")]
    public string FormatVersion { get; set; } = "v1";

    [JsonPropertyName("created_utc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("kdf")]
    public string Kdf { get; set; } = "pbkdf2-hmac-sha512";

    [JsonPropertyName("kdf_iterations")]
    public int KdfIterations { get; set; }

    [JsonPropertyName("kdf_salt_b64")]
    public string KdfSaltB64 { get; set; } = "";

    [JsonPropertyName("chunk_size")]
    public int ChunkSize { get; set; }

    /// <summary>
    /// Target packed-volume size in bytes for Usenet uploads (default 32 MiB).
    /// Chunks are greedily packed into volumes of at most this size; one
    /// volume is posted as one article. Absent in older repo.json files ->
    /// default applies.
    /// </summary>
    [JsonPropertyName("volume_size_bytes")]
    public long VolumeSizeBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>
    /// When false, uploads post one article per chunk (v1 behavior) instead
    /// of packing chunks into volumes. Default true.
    /// </summary>
    [JsonPropertyName("use_volumes")]
    public bool UseVolumes { get; set; } = true;
}
