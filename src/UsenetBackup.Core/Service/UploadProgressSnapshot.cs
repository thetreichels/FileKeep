using System.Text.Json.Serialization;

namespace UsenetBackup.Core.Service;

/// <summary>
/// Snapshot of upload progress for the dashboard API.
/// Named record (not anonymous type) so trimmed publishing works.
/// </summary>
public sealed record UploadProgressSnapshot(
    [property: JsonPropertyName("jobName")] string JobName,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("doneChunks")] int DoneChunks,
    [property: JsonPropertyName("totalChunks")] int TotalChunks,
    [property: JsonPropertyName("percent")] double Percent,
    [property: JsonPropertyName("bytesPerSec")] long BytesPerSec,
    [property: JsonPropertyName("eta")] string? Eta,
    [property: JsonPropertyName("measuring")] bool Measuring,
    [property: JsonPropertyName("elapsedSecs")] int ElapsedSecs,
    [property: JsonPropertyName("phase")] string Phase);
