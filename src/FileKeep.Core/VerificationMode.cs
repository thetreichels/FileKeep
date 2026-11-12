namespace FileKeep.Core;

/// <summary>
/// Controls how incremental backup determines whether a file is unchanged.
///
/// The size+mtime fast path is a performance optimization, not a correctness
/// mechanism. A file whose contents change while size and mtime are preserved
/// (or restored) will be misclassified by metadata alone.
///
/// <list type="bullet">
/// <item><description>
/// <c>Fast</c>: Metadata only (size + mtime). Fastest, but vulnerable to
/// timestamp spoofing.
/// </description></item>
/// <item><description>
/// <c>Verify</c>: Metadata fast path, plus SHA-256 verification for files
/// that pass the metadata check. Catches spoofed timestamps at the cost
/// of hashing apparently-unchanged files.
/// </description></item>
/// <item><description>
/// <c>Paranoid</c>: SHA-256 every file, ignore metadata entirely. Slowest,
/// strongest guarantee.
/// </description></item>
/// </list>
/// </summary>
public enum VerificationMode
{
    /// <summary>Metadata (size + mtime) only.</summary>
    Fast = 0,

    /// <summary>Metadata fast path + hash verification of apparent matches.</summary>
    Verify = 1,

    /// <summary>Hash every file; ignore metadata.</summary>
    Paranoid = 2,
}

/// <summary>Parses verification mode from configuration strings.</summary>
public static class VerificationModeParser
{
    private static readonly Dictionary<string, VerificationMode> Names = new(StringComparer.Ordinal)
    {
        ["fast"] = VerificationMode.Fast,
        ["verify"] = VerificationMode.Verify,
        ["paranoid"] = VerificationMode.Paranoid,
    };

    public static VerificationMode Parse(string? value) =>
        StringEnumParser.Parse(value, Names, VerificationMode.Fast,
            v => new ArgumentException(
                $"Unknown verification mode '{v}'. Valid: fast, verify, paranoid."));
}
