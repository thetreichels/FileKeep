namespace FileKeep.Core;

/// <summary>
/// Shared validation for 64-hex-char IDs (SHA-256 hex): chunk IDs and volume
/// IDs share the same shape. Consolidates the copies previously spread across
/// VolumePacker, ArticleCodec, and the blob stores (audit §3.1 D2).
/// The canonical messages are preserved exactly: "Chunk ID must be 64 hex
/// chars." / "Volume ID must be 64 hex chars.", thrown as
/// <see cref="ArgumentException"/>.
/// </summary>
public static class ChunkIdValidator
{
    /// <summary>True when <paramref name="value"/> is exactly 64 hex chars.</summary>
    public static bool IsValid(string? value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> ("Chunk ID must be 64 hex chars.")
    /// unless <paramref name="value"/> is a valid chunk ID.
    /// </summary>
    public static void RequireChunkId(string? value, string paramName)
    {
        if (!IsValid(value))
            throw new ArgumentException("Chunk ID must be 64 hex chars.", paramName);
    }

    /// <summary>
    /// Same 64-hex rule as <see cref="RequireChunkId"/>, for volume IDs
    /// (same SHA-256-hex shape, different message).
    /// Throws <see cref="ArgumentException"/> ("Volume ID must be 64 hex chars.").
    /// </summary>
    public static void RequireVolumeId(string? value, string paramName)
    {
        if (!IsValid(value))
            throw new ArgumentException("Volume ID must be 64 hex chars.", paramName);
    }
}
