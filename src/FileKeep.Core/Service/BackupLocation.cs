using System.Text.Json.Serialization;
using FileKeep.Core.Security;

namespace FileKeep.Core.Service;

/// <summary>
/// A named backup destination: either a Usenet (NNTP) provider or an SMB
/// network share. Jobs reference locations by name in their <c>targets</c>;
/// connection details and credentials live here, in one place.
/// </summary>
public sealed class BackupLocation
{
    /// <summary>User-visible name, e.g. "Frugal Usenet" or "NAS backups". Unique.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary><c>"nntp"</c>, <c>"smb"</c> or <c>"usb"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Typed location kind (parsed at load).</summary>
    [JsonIgnore]
    public LocationKind ParsedKind => LocationKindParser.Parse(Type);

    // ---- NNTP fields (type == "nntp") ----

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    // TLS by default (security audit D-2), same as NntpConfig: the
    // defaults are the TLS pair (563 + on); explicit stored values win.
    [JsonPropertyName("port")]
    public int Port { get; set; } = 563;

    [JsonPropertyName("ssl")]
    public bool Ssl { get; set; } = true;

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("connections")]
    public int Connections { get; set; } = 10;

    [JsonPropertyName("newsgroup")]
    public string Newsgroup { get; set; } = "alt.binaries.test";

    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = 1095;

    [JsonPropertyName("passwordProtected")]
    public string? PasswordProtected { get; set; }

    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordPlaintext { get; set; }

    // ---- SMB fields (type == "smb") ----

    /// <summary>UNC path, e.g. \\NAS\backups\filekeep.</summary>
    [JsonPropertyName("share")]
    public string Share { get; set; } = "";

    [JsonPropertyName("smbUser")]
    public string SmbUser { get; set; } = "";

    [JsonPropertyName("smbPasswordProtected")]
    public string? SmbPasswordProtected { get; set; }

    [JsonPropertyName("smbPassword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SmbPasswordPlaintext { get; set; }

    // ---- USB fields (type == "usb") ----

    /// <summary>Volume serial number (hex), used to identify the drive reliably across letter changes.</summary>
    [JsonPropertyName("volumeSerial")]
    public string VolumeSerial { get; set; } = "";

    /// <summary>Volume label at the time the location was added, for display.</summary>
    [JsonPropertyName("volumeLabel")]
    public string VolumeLabel { get; set; } = "";

    /// <summary>Subfolder on the drive where backups are stored, e.g. "FileKeep".</summary>
    [JsonPropertyName("usbFolder")]
    public string UsbFolder { get; set; } = "FileKeep";

    // ---- Disguise fields (usb/smb only; see DisguisedLayout) ----

    /// <summary>
    /// When true, this destination wears a disguised layout: files live
    /// flat in <see cref="CoverName"/> under derived media-style names
    /// instead of chunks/manifests/repo.json. Default false; existing
    /// configs load unchanged.
    /// </summary>
    [JsonPropertyName("disguiseEnabled")]
    public bool DisguiseEnabled { get; set; } = false;

    /// <summary>Innocuous on-destination folder name for disguised locations, e.g. "Photos".</summary>
    [JsonPropertyName("coverName")]
    public string CoverName { get; set; } = "";

    /// <summary>When true (and disguised), decoy media files are written alongside real files.</summary>
    [JsonPropertyName("disguiseDecoys")]
    public bool DisguiseDecoys { get; set; } = false;

    /// <summary>The disguise layout for this location, or null when plain. See <see cref="DisguisedLayout"/>.</summary>
    [JsonIgnore]
    public DisguisedLayout? Disguise => DisguisedLayout.ForLocation(this);

    /// <summary>
    /// Parity/redundancy mode for this location ("none", "xor", "par2").
    /// If empty, falls back to the job's RedundancyMode.
    /// </summary>
    public string RedundancyMode { get; set; } = "";

    /// <summary>Typed redundancy mode (parsed at load; empty means fallback).</summary>
    [JsonIgnore]
    public RedundancyKind ParsedRedundancy => RedundancyKindParser.Parse(RedundancyMode);

    /// <summary>True if a password is available (stored blob or env var).</summary>
    [JsonIgnore]
    public bool HasPassword =>
        ParsedKind == LocationKind.Nntp
            ? SecretResolver.HasSecret(BackupScheduler.NntpPasswordEnvVar, PasswordProtected)
            : ParsedKind == LocationKind.Smb
                ? SecretResolver.HasSecret(BackupScheduler.SmbPasswordEnvVar, SmbPasswordProtected)
                : true; // usb: no password needed

    /// <summary>
    /// Shallow copy. Safe: every member is a string, number, or bool
    /// (the rest are computed). Vault migration clones a location and
    /// then changes only the pool-identifying fields.
    /// </summary>
    public BackupLocation Clone() => (BackupLocation)MemberwiseClone();
}
