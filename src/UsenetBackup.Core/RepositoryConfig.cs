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
}
