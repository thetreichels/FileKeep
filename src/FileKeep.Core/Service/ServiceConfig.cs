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

/// <summary>
/// One previous vault, recorded when vault migration re-points jobs
/// at fresh repos. The old repo path and old targets are kept so the
/// previous vault stays findable: its local dir is untouched, and
/// its destination pools are untouched (fresh pools were created
/// for the new vaults), so it remains restorable with the old
/// secret through a future repo-picker — it is never silently lost.
/// </summary>
public sealed class MigratedVaultRecord
{
    /// <summary>Jobs that were re-pointed away from <see cref="OldRepo"/>.</summary>
    [JsonPropertyName("jobs")]
    public List<string> Jobs { get; set; } = new();

    /// <summary>Local repo path the jobs used before migration.</summary>
    [JsonPropertyName("oldRepo")]
    public string OldRepo { get; set; } = "";

    /// <summary>Local repo path the jobs use after migration.</summary>
    [JsonPropertyName("newRepo")]
    public string NewRepo { get; set; } = "";

    /// <summary>When the migration ran (UTC, round-trip format).</summary>
    [JsonPropertyName("migratedUtc")]
    public string MigratedUtc { get; set; } = "";

    /// <summary>Target names the jobs used before migration.</summary>
    [JsonPropertyName("oldTargets")]
    public List<string> OldTargets { get; set; } = new();
}

/// <summary>
/// One scheduled backup job from the service configuration file.
/// </summary>
public sealed class BackupJobConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = "";

    /// <summary>
    /// Sources to back up. For file jobs, the folders (one manifest per
    /// folder per run). For disk-image jobs, exactly one entry: the
    /// device path (e.g. <c>\\.\PhysicalDrive1</c>). The dashboard
    /// guarantees file-job folders are not nested inside each other (a
    /// folder already includes everything below it).
    /// </summary>
    [JsonPropertyName("sources")]
    public List<string> Sources { get; set; } = new();

    /// <summary>
    /// Effective sources: <see cref="Sources"/> trimmed of blank entries.
    /// Never null; may be empty (validated on save).
    /// </summary>
    [JsonIgnore]
    public List<string> EffectiveSources
    {
        get
        {
            return Sources.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
    }

    /// <summary>
    /// First effective source: the disk device for disk-image jobs, the
    /// snapshot root for file jobs. Empty when the job has no sources.
    /// </summary>
    [JsonIgnore]
    public string FirstSource => EffectiveSources.Count > 0 ? EffectiveSources[0] : "";

    /// <summary>
    /// <c>"daily HH:mm"</c> (local time, e.g. <c>"daily 02:00"</c>) or
    /// <c>"interval N"</c> (every N minutes).
    /// </summary>
    [JsonPropertyName("schedule")]
    public string Schedule { get; set; } = "";

    /// <summary><c>"incremental"</c> (default) or <c>"full"</c>.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "incremental";

    /// <summary>Typed job mode (parsed at load).</summary>
    [JsonIgnore]
    public BackupMode ParsedMode => BackupModeParser.Parse(Mode);

    /// <summary>
    /// <c>"files"</c> (default) or <c>"disk"</c> (whole-disk image).
    /// For disk jobs, <see cref="Source"/> holds the device path
    /// (e.g. <c>\\.\PhysicalDrive2</c>) instead of a folder, the image
    /// is always a full capture, and VSS/backup-privilege do not apply.
    /// </summary>
    [JsonPropertyName("jobType")]
    public string JobType { get; set; } = "files";

    /// <summary>Typed job kind (parsed at load).</summary>
    [JsonIgnore]
    public JobKind ParsedJobKind => JobKindParser.Parse(JobType);

    /// <summary>True when this job captures a whole-disk image.</summary>
    [JsonIgnore]
    // Deliberately a string comparison, not ParsedJobKind: the dashboard
    // calls this on not-yet-validated job input (UpsertJob checks sources
    // before Validate runs), where a throwing accessor would turn a 400
    // into a 500. Validated configs can use ParsedJobKind instead.
    public bool IsDiskImage =>
        JobType.Equals("disk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For disk-image jobs: back up only used space (via the NTFS volume
    /// bitmap, falling back to zero-detection). Skipped regions restore as
    /// zeros. Defaults to true — a sparse image is smaller and faster, and
    /// restores byte-identically.
    /// </summary>
    [JsonPropertyName("sparse")]
    public bool Sparse { get; set; } = true;

    /// <summary>Read files with Windows backup privilege to bypass exclusive locks (Windows only, admin required).</summary>
    [JsonPropertyName("backupPrivilege")]
    public bool BackupPrivilege { get; set; }

    /// <summary>
    /// Take a Volume Shadow Copy snapshot of the source volume and back up
    /// from the shadow copy (Windows only, admin required). Provides a
    /// point-in-time frozen view so open files back up consistently.
    /// Mutually exclusive with <see cref="BackupPrivilege"/>.
    /// </summary>
    [JsonPropertyName("vss")]
    public bool Vss { get; set; }

    /// <summary>
    /// Seconds to hold the VSS snapshot. Backups exceeding this abort when
    /// the helper times out. Default 7200 (2 hours); raise for very large
    /// sources. 0 means the helper default.
    /// </summary>
    [JsonPropertyName("vssTimeoutSecs")]
    public int VssTimeoutSecs { get; set; }

    /// <summary>
    /// User-controlled pause. A paused job never runs — scheduled or
    /// manual — until resumed. This is the only STORED pause: the other
    /// pauses (no backup location, USB drive away) are derived from the
    /// job's targets and the attached drives, computed by
    /// <see cref="JobStateEvaluator"/>, never persisted, so they lift on
    /// their own when the cause clears. Resuming never triggers a
    /// catch-up run; the job simply re-arms for its next scheduled time.
    /// </summary>
    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    /// <summary>
    /// Upload targets after a backup completes: the NAMES of
    /// <see cref="ServiceConfig.Locations"/> entries. A name that matches
    /// no location fails config validation — unknown targets are
    /// rejected at load (see <see cref="ServiceConfig.Validate"/>).
    /// </summary>
    [JsonPropertyName("targets")]
    public List<string>? Targets { get; set; }

    /// <summary>
    /// A shallow copy of this job with a different target list. The
    /// scheduler runs a due job against a copy filtered to the targets
    /// available right now, leaving the stored job untouched.
    /// </summary>
    public BackupJobConfig CloneWithTargets(IReadOnlyList<string> targets)
    {
        var clone = (BackupJobConfig)MemberwiseClone();
        clone.Targets = targets.ToList();
        return clone;
    }

    /// <summary>
    /// Effective upload targets: the explicit <see cref="Targets"/>,
    /// normalized (trimmed, non-empty, de-duplicated). There is no
    /// fallback derivation (Phase 6 dropped the legacy auto-upload
    /// default): a job with no targets has no
    /// destinations, and a target name that matches no location in
    /// <see cref="ServiceConfig.Locations"/> resolves to nothing — the
    /// job evaluates to the derived NeedsLocation state, exactly like a
    /// job with an empty target list (<see cref="JobStateEvaluator"/>).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveTargets
    {
        get
        {
            if (Targets is { Count: > 0 })
                return Targets
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            return new List<string>();
        }
    }

    /// <summary>
    /// Automatically verify the backup after it completes (checks all chunk
    /// hashes). Catches bitrot and corruption early.
    /// </summary>
    [JsonPropertyName("autoVerify")]
    public bool AutoVerify { get; set; }

    /// <summary>
    /// How incremental backup determines unchanged files: "fast" (default,
    /// size+mtime metadata only), "verify" (metadata + SHA-256 check of
    /// apparent matches), or "paranoid" (SHA-256 every file).
    /// </summary>
    [JsonPropertyName("verificationMode")]
    public string VerificationMode { get; set; } = "fast";

    /// <summary>
    /// Redundancy mode for Usenet uploads: "none" (default), "xor" (single
    /// parity block per group, recovers 1 missing per group), or "par2"
    /// (Reed-Solomon, recovers multiple missing per group).
    /// </summary>
    [JsonPropertyName("redundancyMode")]
    public string RedundancyMode { get; set; } = "none";

    /// <summary>Typed redundancy mode (parsed at load).</summary>
    [JsonIgnore]
    public RedundancyKind ParsedRedundancy => RedundancyKindParser.Parse(RedundancyMode);

    /// <summary>
    /// Number of data chunks per XOR parity group. Default 10 (1 parity per
    /// 10 chunks, ~10% overhead). Higher values mean fewer parity files but
    /// less protection: only 1 missing chunk recoverable per group.
    /// </summary>
    [JsonPropertyName("parityGroupSize")]
    public int ParityGroupSize { get; set; } = 10;
}

/// <summary>
/// Service configuration, loaded from JSON (see <c>service.example.json</c>).
/// </summary>
public sealed class ServiceConfig
{
    [JsonPropertyName("dashboardPort")]
    public int DashboardPort { get; set; } = 15789;

    /// <summary>
    /// Dashboard bind address. Defaults to loopback only; the dashboard has
    /// no authentication, so do not bind it to a public interface
    /// without a reverse proxy in front (the loopback guard in
    /// Dashboard.cs refuses non-loopback binds at startup).
    /// </summary>
    [JsonPropertyName("dashboardBind")]
    public string DashboardBind { get; set; } = "127.0.0.1";

    [JsonPropertyName("jobs")]
    public List<BackupJobConfig> Jobs { get; set; } = new();

    /// <summary>
    /// Guards concurrent access to ALL mutable config state — Jobs,
    /// Locations, NntpProviders, the top-level Nntp, and the protection
    /// fields (P2-C1/C2: Jobs were guarded; everything else was mutated
    /// unlocked while handlers and the scheduler read it lock-free,
    /// which tore saves and handed running jobs half-edited locations).
    /// Dashboard HTTP handlers and the scheduler run on different
    /// threads; every mutation — and every enumeration that isn't a
    /// Snapshot* copy — must hold this lock. (Named JobsLock for
    /// historical reasons; the contract covers the whole config.)
    /// </summary>
    [JsonIgnore]
    public readonly object JobsLock = new();

    /// <summary>
    /// Returns a snapshot copy of the Jobs list taken under <see cref="JobsLock"/>.
    /// Use this for all read-only iterations outside the lock.
    /// </summary>
    public List<BackupJobConfig> SnapshotJobs()
    {
        lock (JobsLock)
            return Jobs.ToList();
    }

    /// <summary>
    /// Returns a snapshot copy of the Locations list taken under
    /// <see cref="JobsLock"/>. Use this for all read-only iterations
    /// outside the lock.
    /// </summary>
    public List<BackupLocation> SnapshotLocations()
    {
        lock (JobsLock)
            return Locations.ToList();
    }

    /// <summary>
    /// Returns a snapshot copy of the NntpProviders list taken under
    /// <see cref="JobsLock"/>.
    /// </summary>
    public List<NntpConfig> SnapshotProviders()
    {
        lock (JobsLock)
            return NntpProviders.ToList();
    }

    /// <summary>
    /// Named backup destinations (Usenet providers and SMB shares). Jobs
    /// reference these by name in their <c>targets</c>; connection details
    /// and credentials for job uploads live here and nowhere else (the
    /// per-job connection fields were retired in Phase 6).
    /// </summary>
    [JsonPropertyName("locations")]
    public List<BackupLocation> Locations { get; set; } = new();

    /// <summary>
    /// Previous vaults recorded by vault migration (see
    /// <see cref="MigratedVaultRecord"/>). Appended to, never pruned
    /// by migration itself — the user deletes the record when the
    /// old vault is gone.
    /// </summary>
    [JsonPropertyName("previousVaults")]
    public List<MigratedVaultRecord> PreviousVaults { get; set; } = new();

    /// <summary>
    /// Global Usenet provider surface for the repo-scoped operations that
    /// are not tied to a job's targets: remote-backup discovery, NZB
    /// download, retention checks, and the dashboard's manual upload.
    /// Read via <see cref="EffectiveProviders"/> (fallback when
    /// <see cref="NntpProviders"/> is empty); no dashboard route writes
    /// it anymore. Jobs do NOT upload through this —
    /// they upload to their target <see cref="Locations"/>. Null/empty
    /// host means no global provider is configured. The password comes
    /// from FILEKEEP_NNTP_PASSWORD if set, else the DPAPI blob.
    /// </summary>
    [JsonPropertyName("nntp")]
    public NntpConfig? Nntp { get; set; }

    /// <summary>
    /// Multiple global Usenet providers (same repo-scoped surface as
    /// <see cref="Nntp"/>). If set, uploads go to all providers;
    /// downloads try each in order until the chunk is found. If empty,
    /// <see cref="EffectiveProviders"/> falls back to the singleton
    /// <see cref="Nntp"/>.
    /// </summary>
    [JsonPropertyName("nntpProviders")]
    public List<NntpConfig> NntpProviders { get; set; } = new();

    /// <summary>
    /// DPAPI-encrypted repository passphrase for offline storage. When set,
    /// the scheduler uses this if the FILEKEEP_PASSPHRASE environment
    /// variable is not present. Set via the dashboard Security card; the
    /// plaintext never touches disk. Optional: protected by YubiKey
    /// challenge-response instead of DPAPI (see YubiKeySlot).
    /// </summary>
    [JsonPropertyName("passphraseProtected")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PassphraseProtected { get; set; }

    /// <summary>
    /// If true, restore operations require a YubiKey touch to unlock the
    /// passphrase for the session. The YubiKey is used exactly twice: once
    /// during setup (to wrap the passphrase with a challenge-response
    /// derived key) and again at the start of each restore session.
    /// Scheduled backups use the DPAPI-stored passphrase unattended and
    /// never need the key. This gates decryption (restores) on physical
    /// presence while keeping backups automatic.
    /// </summary>
    [JsonPropertyName("passphraseYubiKey")]
    public bool PassphraseYubiKey { get; set; }

    /// <summary>
    /// True when the repository secret was generated by FileKeep
    /// (passphrase-free YubiKey-only mode) rather than typed by the user.
    /// The generated secret occupies the passphrase slot: its DPAPI copy
    /// lives in <see cref="PassphraseProtected"/> and it is wrapped by
    /// the YubiKey exactly like a typed passphrase, so backups, manifest
    /// stamping and recovery all work unchanged. The flag exists so the
    /// dashboard reports the profile honestly (Passphrase off,
    /// YubiKey-only on), so a typed passphrase is rejected while this
    /// mode is active, and so a restore session can also be opened by
    /// typing the recovery-sheet secret. Set once at generated-secret
    /// setup; cleared when the secret is cleared.
    /// </summary>
    [JsonPropertyName("generatedSecretMode")]
    public bool GeneratedSecretMode { get; set; }

    /// <summary>
    /// Base64 random challenge for the YubiKey HMAC-SHA1 slot. Generated
    /// once at YubiKey setup; the challenge never changes afterwards so the
    /// same touch always reproduces the same wrapping key.
    /// </summary>
    [JsonPropertyName("yubiKeyChallenge")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? YubiKeyChallenge { get; set; }

    /// <summary>
    /// YubiKey OTP slot holding the HMAC-SHA1 credential (1 or 2).
    /// Default 2 (long press), leaving slot 1 free for the user's own use.
    /// </summary>
    [JsonPropertyName("yubiKeySlot")]
    public int YubiKeySlot { get; set; } = 2;

    /// <summary>
    /// The repository passphrase wrapped (AES-256-GCM) under the
    /// YubiKey-derived KEK; see <see cref="Security.YubiKeyCrypto"/>.
    /// Unwrapping requires a YubiKey touch. The DPAPI copy in
    /// <see cref="PassphraseProtected"/> is kept alongside so scheduled
    /// backups run unattended.
    /// </summary>
    [JsonPropertyName("passphraseYubiKeyWrapped")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PassphraseYubiKeyWrapped { get; set; }

    /// <summary>
    /// In YubiKey-only (generated-secret) mode, a user-added passphrase
    /// wraps the generated secret (AES-256-GCM, the same blob format as
    /// <see cref="PassphraseYubiKeyWrapped"/>) under a PBKDF2-HMAC-SHA512
    /// KEK derived from the added passphrase — see
    /// DashboardApi.AddPassphraseWrap. The added passphrase unlocks
    /// restores and proves security-config changes; it never replaces
    /// the root secret, so the YubiKey touch and the recovery-sheet
    /// secret keep working. Null when no passphrase has been added.
    /// The salt lives in <see cref="PassphraseWrapSaltB64"/>; the
    /// iteration count in <see cref="PassphraseWrapIterations"/>.
    /// </summary>
    [JsonPropertyName("passphraseWrapProtected")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PassphraseWrapProtected { get; set; }

    /// <summary>
    /// The 32-byte salt (base64) for the added-passphrase KEK.
    /// </summary>
    [JsonPropertyName("passphraseWrapSaltB64")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PassphraseWrapSaltB64 { get; set; }

    /// <summary>
    /// PBKDF2 iteration count for the added-passphrase KEK. 0 means
    /// unset; readers fall back to the repository default.
    /// </summary>
    [JsonPropertyName("passphraseWrapIterations")]
    public int PassphraseWrapIterations { get; set; }

    /// <summary>
    /// The YubiKey HMAC-SHA1 slot secret, DPAPI-encrypted. Stored so spare
    /// keys can be programmed later with the same secret. Without this,
    /// a lost key means re-running setup (which invalidates old keys).
    /// </summary>
    [JsonPropertyName("yubiKeySlotSecretProtected")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? YubiKeySlotSecretProtected { get; set; }

    /// <summary>
    /// Whether the phone authenticator is enabled as a fallback unlock
    /// method. It is always standalone: it can never be the sole method
    /// (at least one of Passphrase/YubiKey must also be on, because USB
    /// recovery needs one of them), and it is running-system-only — it
    /// is never used in USB recovery. As a proof in the Windows app, a
    /// TOTP code stands alone like any other proof.
    /// </summary>
    [JsonPropertyName("phoneAuthEnabled")]
    public bool PhoneAuthEnabled { get; set; }

    /// <summary>
    /// DPAPI-protected base32 TOTP secret for the phone authenticator
    /// (RFC 6238). Generated at phone enrollment; the raw secret never
    /// sits in plaintext in the config file.
    /// </summary>
    [JsonPropertyName("phoneTotpSecretProtected")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PhoneTotpSecretProtected { get; set; }

    /// <summary>
    /// DPAPI-protected TOTP secret from an in-progress phone enrollment
    /// (SF-17). A re-enrollment stages its new secret here and only a
    /// verified code promotes it over
    /// <see cref="PhoneTotpSecretProtected"/> — abandoning or failing
    /// enrollment must never invalidate the authenticator that already
    /// works. Not itself an unlock secret; nothing verifies against it
    /// except the enrollment check that promotes it.
    /// </summary>
    [JsonPropertyName("pendingPhoneTotpSecretProtected")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingPhoneTotpSecretProtected { get; set; }

    /// <summary>
    /// How the Passphrase and YubiKey methods interact: <c>"or"</c>
    /// (default; either one unlocks) or <c>"and"</c> (both required).
    /// The Phone method is never linked.
    /// </summary>
    [JsonPropertyName("protectionLinkMode")]
    public string ProtectionLinkMode { get; set; } = "or";

    /// <summary>Typed protection link mode (parsed at load; empty means "or").</summary>
    [JsonIgnore]
    public LinkKind ParsedLink => LinkKindParser.Parse(ProtectionLinkMode);

    /// <summary>
    /// Display names of registered YubiKeys (e.g. "YubiKey 1"). Labels
    /// only — all keys are programmed with the same slot secret (see
    /// <see cref="YubiKeySlotSecretProtected"/>) and work interchangeably.
    /// </summary>
    [JsonPropertyName("yubiKeys")]
    public List<string> YubiKeys { get; set; } = new();

    /// <summary>
    /// Gets the effective list of global Usenet providers: NntpProviders
    /// if non-empty, otherwise the single Nntp config as a singleton list.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<NntpConfig> EffectiveProviders
    {
        get
        {
            // Snapshot copy under the lock (P2-C2): returning the live
            // list let the scheduler's provider list alias a list the
            // dashboard could replace or mutate mid-run.
            lock (JobsLock)
                return NntpProviders.Count > 0 ? NntpProviders.ToList() :
                    Nntp is not null ? new[] { Nntp } :
                    Array.Empty<NntpConfig>();
        }
    }

    /// <summary>
    /// Enable automatic retention checks. When true, the scheduler periodically
    /// runs RetentionManager.CheckAndRepost to keep Usenet articles alive.
    /// Default true.
    /// </summary>
    [JsonPropertyName("retentionEnabled")]
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>
    /// Hours between automatic retention checks. Default 24 (daily).
    /// </summary>
    [JsonPropertyName("retentionCheckIntervalHours")]
    public int RetentionCheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// Backups expiring within this many days are checked for retention.
    /// Default 90.
    /// </summary>
    [JsonPropertyName("retentionWarnDays")]
    public int RetentionWarnDays { get; set; } = 90;

    /// <summary>
    /// Backups with at most this many days of retention remaining are
    /// proactively refreshed. Default 30. Set to 0 to refresh only when
    /// articles are actually missing.
    /// </summary>
    [JsonPropertyName("retentionRepostThresholdDays")]
    public int RetentionRepostThresholdDays { get; set; } = 30;

    /// <summary>
    /// Loads a config from <paramref name="path"/>. Delegates to
    /// <see cref="ServiceConfigPersistence.Load"/> (Stage 2 of the
    /// config-lifecycle split); fail-closed semantics live there.
    /// </summary>
    public static ServiceConfig Load(string path) => ServiceConfigPersistence.Load(path);

    /// <summary>
    /// Validates and writes the configuration to <paramref name="path"/>
    /// (used by the dashboard Settings UI). Writes atomically via a temp file.
    /// Delegates to <see cref="ServiceConfigPersistence.Save"/> (Stage 2).
    /// </summary>
    public void Save(string path) => ServiceConfigPersistence.Save(this, path);

    /// <summary>
    /// Validates this config. Delegates to
    /// <see cref="ServiceConfigValidator.Validate"/> (Stage 1 of the
    /// config-lifecycle split); rules and messages live there.
    /// </summary>
    public void Validate() => ServiceConfigValidator.Validate(this);

    /// <summary>
    /// Delegates to
    /// <see cref="ServiceConfigValidator.ValidateNoNestedSources"/>.
    /// </summary>
    public static void ValidateNoNestedSources(BackupJobConfig job) =>
        ServiceConfigValidator.ValidateNoNestedSources(job);
}
