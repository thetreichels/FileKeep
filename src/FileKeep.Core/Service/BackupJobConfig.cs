using System.Text.Json.Serialization;

namespace FileKeep.Core.Service;

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
