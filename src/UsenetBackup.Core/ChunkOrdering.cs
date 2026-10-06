namespace UsenetBackup.Core;

/// <summary>
/// Canonical ordering for chunk IDs used in parity grouping.
/// </summary>
/// <remarks>
/// Parity groups are position-based (chunks 0-9 = group 1, 10-19 = group 2,
/// etc.). ALL code paths that group chunks for parity MUST use this ordering,
/// otherwise upload and download will compute different groups and
/// reconstruction will silently fail to find the correct parity blocks.
///
/// Current call sites:
/// - NntpGenerator.Generate (NZB creation)
/// - BackupScheduler (scheduled uploads)
/// - Program.NntpUpload (CLI uploads)
/// - BackupRepository.TryReconstructChunk (download reconstruction, via NZB order)
///
/// History: commit b1921c8 fixed a critical bug where upload used manifest
/// file order but NZB/download used sorted order.
/// </remarks>
public static class ChunkOrdering
{
    /// <summary>
    /// Returns chunk IDs in canonical sorted order (Ordinal) for parity grouping.
    /// </summary>
    public static string[] GetOrderedChunkIds(IEnumerable<string> chunkIds)
    {
        return chunkIds.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Returns distinct chunk IDs from a manifest in canonical order.
    /// </summary>
    public static string[] GetOrderedChunkIds(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return GetOrderedChunkIds(manifest.Files.SelectMany(f => f.Chunks));
    }
}
