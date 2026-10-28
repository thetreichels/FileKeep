using System.Text.Json.Serialization;

namespace FileKeep.Core;

/// <summary>Contents of repo.json at the repository root (BACKUP FORMAT v1).</summary>
public sealed class RepositoryConfig
{
    [JsonPropertyName("format_version")]
    public string FormatVersion { get; set; } = "v1";

    [JsonPropertyName("created_utc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("kdf_iterations")]
    public int KdfIterations { get; set; }

    [JsonPropertyName("kdf_salt_b64")]
    public string KdfSaltB64 { get; set; } = "";

    [JsonPropertyName("chunk_size")]
    public int ChunkSize { get; set; }

    /// <summary>
    /// How file bytes are divided into chunks: "fixed" (v1 behavior — every
    /// chunk is exactly <see cref="ChunkSize"/> bytes except the last) or
    /// "cdc" (content-defined chunking via a Gear rolling hash, with
    /// <see cref="ChunkSize"/> as the target average, min = avg/4,
    /// max = avg*4). Absent in older repo.json files -&gt; "fixed" (they
    /// predate CDC). New repositories default to "cdc".
    /// Chunk IDs are SHA-256 of plaintext either way, so deduplication,
    /// encryption, restore and verification are scheme-agnostic.
    /// </summary>
    // No default: a repo file without this key is rejected at open (see
    // BackupRepository.Open). Init always writes the key, so a missing
    // key means a pre-CDC repo file.
    [JsonPropertyName("chunking_scheme")]
    public string ChunkingScheme { get; set; } = "";

    /// <summary>Typed chunking scheme (parsed at open).</summary>
    [JsonIgnore]
    public ChunkingKind ParsedScheme => ChunkingKindParser.Parse(ChunkingScheme);

    /// <summary>
    /// Target packed-volume size in bytes for Usenet uploads (default 100 MB, the scene-standard DVD9 RAR size).
    /// Chunks are greedily packed into volumes of at most this size; one
    /// volume is posted as one article. Absent in older repo.json files ->
    /// default applies.
    /// </summary>
    [JsonPropertyName("volume_size_bytes")]
    public long VolumeSizeBytes { get; set; } = 100L * 1000 * 1000;

    /// <summary>
    /// When false, uploads post one article per chunk (v1 behavior) instead
    /// of packing chunks into volumes. Default true.
    /// </summary>
    [JsonPropertyName("use_volumes")]
    public bool UseVolumes { get; set; } = true;

    /// <summary>
    /// Stable pseudonym used as the NNTP From header on posted articles.
    /// Generated once (poster-&lt;8hex&gt;) on first upload if absent; never
    /// advertises the software. Overridable per-run with --poster.
    /// </summary>
    [JsonPropertyName("poster")]
    public string? Poster { get; set; }
}
