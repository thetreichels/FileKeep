using System.Text.Json.Serialization;
using FileKeep.Core.Security;

namespace FileKeep.Core.Service;

/// <summary>
/// Usenet (NNTP) provider configuration. The password is stored as a DPAPI-encrypted
/// blob (Windows only) or supplied via the FILEKEEP_NNTP_PASSWORD environment variable.
/// The env var takes precedence when set.
/// </summary>
public sealed class NntpConfig
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    // TLS by default (security audit D-2): port and SSL defaults are the
    // TLS pair (563 + on). An explicit "port"/"ssl" in a stored config is
    // honored as written — these initializers only supply omitted fields.
    [JsonPropertyName("port")]
    public int Port { get; set; } = 563;

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("ssl")]
    public bool Ssl { get; set; } = true;

    [JsonPropertyName("connections")]
    public int Connections { get; set; } = 10;

    [JsonPropertyName("newsgroup")]
    public string Newsgroup { get; set; } = "alt.binaries.test";

    /// <summary>
    /// Usenet retention in days for this provider. Backups uploaded longer ago
    /// than this are considered expired. Default 1095 (3 years). Set to match
    /// your provider's actual retention (varies by provider).
    /// </summary>
    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = 1095;

    /// <summary>
    /// Redundancy mode for this provider: "none", "xor", or "par2".
    /// If empty, falls back to the job's RedundancyMode.
    /// Use stronger redundancy for less reliable providers.
    /// </summary>
    [JsonPropertyName("redundancyMode")]
    public string RedundancyMode { get; set; } = "";

    /// <summary>Typed redundancy mode (parsed at load; empty means fallback).</summary>
    [JsonIgnore]
    public RedundancyKind ParsedRedundancy => RedundancyKindParser.Parse(RedundancyMode);

    /// <summary>
    /// DPAPI-encrypted password (base64). Set via the dashboard; never holds plaintext.
    /// </summary>
    [JsonPropertyName("passwordProtected")]
    public string? PasswordProtected { get; set; }

    /// <summary>
    /// Plaintext password supplied by the dashboard UI for saving. Serialized as
    /// <c>"password"</c> on input only; the API encrypts it into
    /// <see cref="PasswordProtected"/> and nulls this before persisting.
    /// Not written when null.
    /// </summary>
    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordPlaintext { get; set; }

    /// <summary>True if a password is available (stored blob or env var).</summary>
    [JsonIgnore]
    public bool HasPassword =>
        SecretResolver.HasSecret(BackupScheduler.NntpPasswordEnvVar, PasswordProtected);
}
