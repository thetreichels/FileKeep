namespace FileKeep.Core;

/// <summary>
/// What to do when a restore encounters a file that already exists in the
/// destination. The user picks this at restore time (recovery wizard,
/// dashboard, or --merge-policy).
/// </summary>
public enum MergePolicy
{
    /// <summary>
    /// Classic snapshot restore: every file from the backup overwrites the
    /// destination, even when it differs.
    /// </summary>
    Overwrite = 0,

    /// <summary>
    /// Never overwrite: files already in the destination are left alone.
    /// Identical files are skipped silently; differing files are skipped
    /// and reported in <see cref="RestoreResult.SkippedDiffering"/>.
    /// </summary>
    SkipDiffering = 1,

    /// <summary>
    /// Per-file newer-wins: the modification dates are compared and the
    /// newer copy is kept. Symlinks have no meaningful "newer", so they
    /// follow <see cref="SkipDiffering"/> (identical target skipped,
    /// differing target reported).
    /// </summary>
    KeepNewer = 2,
}

/// <summary>
/// End-user display names and explanations for <see cref="MergePolicy"/>
/// choices. Shared by the recovery wizard, the dashboard, and CLI help so
/// the wording stays consistent everywhere.
/// </summary>
public static class MergePolicyInfo
{
    public static string DisplayName(MergePolicy policy) => policy switch
    {
        MergePolicy.SkipDiffering => "Skip files that differ (recommended)",
        MergePolicy.Overwrite => "Overwrite with the backup's copies",
        MergePolicy.KeepNewer => "Keep whichever file is newer",
        _ => policy.ToString(),
    };

    public static string Description(MergePolicy policy) => policy switch
    {
        MergePolicy.SkipDiffering =>
            "Existing files are never overwritten. Files that differ from the backup " +
            "are left alone and listed when the restore finishes.",
        MergePolicy.Overwrite =>
            "Classic snapshot restore. Every file from the backup replaces the file " +
            "in the destination, even when it differs.",
        MergePolicy.KeepNewer =>
            "Compares the modification date of each file and keeps the newer copy. " +
            "Links are never overwritten: identical links are skipped, differing links are reported.",
        _ => "",
    };

    /// <summary>Parses the --merge-policy / API value: "skip", "overwrite", "newer".</summary>
    public static bool TryParse(string? value, out MergePolicy policy)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "skip": policy = MergePolicy.SkipDiffering; return true;
            case "overwrite": policy = MergePolicy.Overwrite; return true;
            case "newer": policy = MergePolicy.KeepNewer; return true;
            default: policy = MergePolicy.Overwrite; return false;
        }
    }
}

/// <summary>
/// Options for <see cref="BackupRepository.Restore(string,string,RestoreOptions)"/>.
/// </summary>
public sealed class RestoreOptions
{
    /// <summary>
    /// Glob patterns (manifest-relative paths with '/' separators) selecting
    /// which files to restore. Empty means all files. Supports '*', '**'
    /// and '?'. Repeatable on the CLI (--include).
    /// </summary>
    public List<string> Include { get; } = new();

    /// <summary>
    /// Glob patterns excluding files from the restore. Repeatable on the
    /// CLI (--exclude). Applied after <see cref="Include"/>.
    /// </summary>
    public List<string> Exclude { get; } = new();

    /// <summary>
    /// What to do when a file already exists in the destination.
    /// Default is <see cref="MergePolicy.Overwrite"/> (classic snapshot
    /// restore). Set to <see cref="MergePolicy.SkipDiffering"/> for merge
    /// mode: existing files are never overwritten — identical files are
    /// skipped silently and differing files are skipped and reported in
    /// <see cref="RestoreResult.SkippedDiffering"/>.
    /// </summary>
    public MergePolicy MergePolicy { get; set; } = MergePolicy.Overwrite;
}

/// <summary>Outcome of <see cref="BackupRepository.Restore(string,string,RestoreOptions)"/>.</summary>
public sealed class RestoreResult
{
    /// <summary>File entries actually written (regular files and symlinks).</summary>
    public int RestoredFiles { get; set; }

    /// <summary>Files skipped in merge mode because the destination already matches the manifest.</summary>
    public int SkippedIdentical { get; set; }

    /// <summary>
    /// Manifest-relative paths skipped in merge mode because the destination
    /// already has a differing file or link. Left untouched, never overwritten.
    /// </summary>
    public List<string> SkippedDiffering { get; } = new();

    /// <summary>
    /// Non-fatal problems on files that DID restore (NS-11) — e.g. the
    /// content landed but restoring the original modification time
    /// failed. Never counted as restore errors.
    /// </summary>
    public List<string> Warnings { get; } = new();
}
