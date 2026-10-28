namespace FileKeep.Core;

/// <summary>
/// Redundancy applied to uploaded articles: none, one XOR parity article
/// per group, or three par2-style parity articles per group.
///
/// JSON transport stays a string; the typed value is parsed at load
/// (see <see cref="ServiceConfig.Validate"/>) and exposed through the
/// <c>ParsedRedundancy</c> accessors. An empty value parses as
/// <see cref="None"/>: for locations and providers empty means "fall back
/// to the job's mode", and for jobs empty is rejected by Validate before
/// any accessor runs, so the default is never observed for a job.
/// </summary>
public enum RedundancyKind
{
    /// <summary>No parity articles.</summary>
    None = 0,

    /// <summary>One XOR parity article per group.</summary>
    Xor = 1,

    /// <summary>Three parity articles per group.</summary>
    Par2 = 2,
}

/// <summary>Parses redundancy mode from configuration strings.</summary>
public static class RedundancyKindParser
{
    public static RedundancyKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "xor" => RedundancyKind.Xor,
            "par2" => RedundancyKind.Par2,
            "none" or null or "" => RedundancyKind.None,
            _ => throw new ArgumentException(
                $"Unknown redundancy mode '{value}'. Valid: none, xor, par2."),
        };
}

/// <summary>
/// How the enabled protection methods combine for a security-config
/// change or restore authorization: any one suffices, or all are required.
///
/// JSON transport stays a string; the typed value is parsed at load and
/// exposed through <c>ServiceConfig.ParsedLink</c>. An empty value parses
/// as <see cref="Or"/>, matching the previous load behavior (empty was
/// accepted and behaved as "or").
/// </summary>
public enum LinkKind
{
    /// <summary>Any one enabled method authorizes.</summary>
    Or = 0,

    /// <summary>Every enabled method must authorize.</summary>
    And = 1,
}

/// <summary>Parses protection link mode from configuration strings.</summary>
public static class LinkKindParser
{
    public static LinkKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "and" => LinkKind.And,
            "or" or null or "" => LinkKind.Or,
            _ => throw new ArgumentException(
                $"Unknown protection link mode '{value}'. Valid: or, and."),
        };
}

/// <summary>
/// Kind of a backup location. There is no valid empty value: a location
/// without a type is rejected at load, so the parser throws on null/empty
/// rather than inventing a default.
///
/// JSON transport stays a string; the typed value is parsed at load and
/// exposed through <c>BackupLocation.ParsedKind</c>.
/// </summary>
public enum LocationKind
{
    /// <summary>Usenet provider location.</summary>
    Nntp = 0,

    /// <summary>SMB network-share location.</summary>
    Smb = 1,

    /// <summary>USB removable-drive location.</summary>
    Usb = 2,
}

/// <summary>Parses backup location type from configuration strings.</summary>
public static class LocationKindParser
{
    public static LocationKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "nntp" => LocationKind.Nntp,
            "smb" => LocationKind.Smb,
            "usb" => LocationKind.Usb,
            _ => throw new ArgumentException(
                $"Unknown location type '{value}'. Valid: nntp, smb, usb."),
        };
}

/// <summary>
/// Whether a job backs up only changed files or re-backs-up everything.
/// There is no valid empty value: a job without a mode is rejected at
/// load, so the parser throws on null/empty.
///
/// JSON transport stays a string; the typed value is parsed at load and
/// exposed through <c>BackupJobConfig.ParsedMode</c>.
/// </summary>
public enum BackupMode
{
    /// <summary>Back up only files changed since the last backup.</summary>
    Incremental = 0,

    /// <summary>Re-back-up every source file.</summary>
    Full = 1,
}

/// <summary>Parses job mode from configuration strings.</summary>
public static class BackupModeParser
{
    public static BackupMode Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "incremental" => BackupMode.Incremental,
            "full" => BackupMode.Full,
            _ => throw new ArgumentException(
                $"Unknown backup mode '{value}'. Valid: incremental, full."),
        };
}

/// <summary>
/// Whether a job backs up files or a whole disk image. There is no valid
/// empty value: a job without a type is rejected at load, so the parser
/// throws on null/empty.
///
/// JSON transport stays a string; the typed value is parsed at load and
/// exposed through <c>BackupJobConfig.ParsedJobKind</c>. Note
/// <c>BackupJobConfig.IsDiskImage</c> intentionally stays a string
/// comparison: the dashboard calls it on not-yet-validated job input
/// (before Validate runs), where a throwing accessor would turn a 400
/// into a 500.
/// </summary>
public enum JobKind
{
    /// <summary>Ordinary file backup.</summary>
    Files = 0,

    /// <summary>Whole-disk image backup.</summary>
    Disk = 1,
}

/// <summary>Parses job type from configuration strings.</summary>
public static class JobKindParser
{
    public static JobKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "files" => JobKind.Files,
            "disk" => JobKind.Disk,
            _ => throw new ArgumentException(
                $"Unknown job type '{value}'. Valid: files, disk."),
        };
}

/// <summary>
/// How a repository splits files into chunks. There is no valid empty
/// value: a repo.json without a chunking scheme is rejected at open
/// (an absent key means a pre-CDC repo file), so the parser throws on
/// null/empty.
///
/// JSON transport stays a string; the typed value is parsed at open and
/// exposed through <c>RepositoryConfig.ParsedScheme</c>.
/// </summary>
public enum ChunkingKind
{
    /// <summary>Fixed-size chunks.</summary>
    Fixed = 0,

    /// <summary>Content-defined chunks.</summary>
    Cdc = 1,
}

/// <summary>Parses repository chunking scheme from configuration strings.</summary>
public static class ChunkingKindParser
{
    public static ChunkingKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "fixed" => ChunkingKind.Fixed,
            "cdc" => ChunkingKind.Cdc,
            _ => throw new ArgumentException(
                $"Unknown chunking scheme '{value}'. Valid: fixed, cdc."),
        };
}
