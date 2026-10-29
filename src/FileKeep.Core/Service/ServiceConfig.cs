using System.Text.Json.Serialization;

namespace FileKeep.Core.Service;

/// <summary>
/// Service configuration, loaded from JSON (see <c>service.example.json</c>).
/// </summary>
public sealed class ServiceConfig
{
    public ServiceConfig()
    {
        Store = new ServiceConfigStore(this);
    }

    /// <summary>
    /// The guarded store (Stage 3): owns the lock, the snapshot
    /// copies, and the effective-provider view. Not serialized.
    /// </summary>
    [JsonIgnore]
    public ServiceConfigStore Store { get; }

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
    /// The store's lock object (Stage 3): the SAME object the store
    /// holds, so every existing lock site keeps working unchanged.
    /// See <see cref="ServiceConfigStore.Lock"/> for the contract.
    /// Kept as the facade path until every external lock site is
    /// migrated to store entry points, site by site.
    /// </summary>
    [JsonIgnore]
    public object JobsLock => Store.Lock;

    /// <summary>
    /// Returns a snapshot copy of the Jobs list taken under <see cref="JobsLock"/>.
    /// Use this for all read-only iterations outside the lock.
    /// Delegates to <see cref="ServiceConfigStore.SnapshotJobs"/>.
    /// </summary>
    public List<BackupJobConfig> SnapshotJobs() => Store.SnapshotJobs();

    /// <summary>
    /// Returns a snapshot copy of the Locations list taken under
    /// <see cref="JobsLock"/>. Use this for all read-only iterations
    /// outside the lock.
    /// Delegates to <see cref="ServiceConfigStore.SnapshotLocations"/>.
    /// </summary>
    public List<BackupLocation> SnapshotLocations() => Store.SnapshotLocations();

    /// <summary>
    /// Returns a snapshot copy of the NntpProviders list taken under
    /// <see cref="JobsLock"/>.
    /// Delegates to <see cref="ServiceConfigStore.SnapshotProviders"/>.
    /// </summary>
    public List<NntpConfig> SnapshotProviders() => Store.SnapshotProviders();

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
    /// Delegates to <see cref="ServiceConfigStore.EffectiveProviders"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<NntpConfig> EffectiveProviders => Store.EffectiveProviders;

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
