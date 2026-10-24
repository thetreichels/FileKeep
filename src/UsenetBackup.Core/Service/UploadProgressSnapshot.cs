using System.Text.Json.Serialization;

namespace UsenetBackup.Core.Service;

/// <summary>
/// Snapshot of upload progress for the run-progress tracker. Only the
/// fields the tracker lambda reads are carried; the tracker's own log
/// lines (not this snapshot) report job, host, phase, percent and ETA.
/// Named record (not anonymous type) so trimmed publishing works.
/// </summary>
public sealed record UploadProgressSnapshot(
    [property: JsonPropertyName("doneChunks")] int DoneChunks,
    [property: JsonPropertyName("totalChunks")] int TotalChunks,
    [property: JsonPropertyName("bytesPerSec")] long BytesPerSec);
