namespace UsenetBackup.Core.Service;

/// <summary>
/// Tracks upload progress and provides rolling time estimates.
/// Logs "measuring..." during warmup (first 5 min), then real ETAs.
/// </summary>
public sealed class UploadProgressTracker : IDisposable
{
    private readonly Action<string> _log;
    private readonly string _jobName;
    private readonly string _host;
    private readonly int _totalChunks;
    private readonly Timer _timer;
    private readonly DateTime _startTime;
    private readonly object _lock = new();

    private int _uploaded;
    private int _skipped;
    private long _bytesUploaded;

    // Rolling window: track completions in last 5 minutes
    private readonly Queue<(DateTime Time, long Bytes)> _recent = new();
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(5);

    private bool _disposed;

    public UploadProgressTracker(
        Action<string> log,
        string jobName,
        string host,
        int totalChunks)
    {
        _log = log;
        _jobName = jobName;
        _host = host;
        _totalChunks = totalChunks;
        _startTime = DateTime.UtcNow;
        _timer = new Timer(LogProgress, null, LogInterval, LogInterval);
    }

    public void RecordUploaded(int chunks, long bytes)
    {
        lock (_lock)
        {
            _uploaded += chunks;
            _bytesUploaded += bytes;
            _recent.Enqueue((DateTime.UtcNow, bytes));
            PruneOld();
        }
    }

    public void RecordSkipped(int chunks)
    {
        lock (_lock)
        {
            _skipped += chunks;
        }
    }

    private void PruneOld()
    {
        var cutoff = DateTime.UtcNow - Window;
        while (_recent.Count > 0 && _recent.Peek().Time < cutoff)
            _recent.Dequeue();
    }

    private void LogProgress(object? state)
    {
        lock (_lock)
        {
            PruneOld();
            int done = _uploaded + _skipped;
            double pct = _totalChunks > 0 ? 100.0 * done / _totalChunks : 0;
            var elapsed = DateTime.UtcNow - _startTime;

            // Adaptive: need minimum data before estimating.
            // At least 10 uploaded chunks AND 30 seconds elapsed.
            // More data = better estimate; we say so.
            bool hasEnoughData = _uploaded >= 10 && elapsed.TotalSeconds >= 30;

            if (!hasEnoughData)
            {
                // Early phase: show percentage, explain we're gathering data
                string reason = _uploaded < 10
                    ? $"({_uploaded}/10 chunks sampled)"
                    : $"({(int)elapsed.TotalSeconds}s/30s elapsed)";
                _log($"job '{_jobName}': uploading to {_host}... {done}/{_totalChunks} chunks ({pct:F1}%) — measuring throughput {reason}...");
            }
            else
            {
                // Have enough data for an estimate; quality improves over time
                long windowBytes = _recent.Sum(r => r.Bytes);
                double windowSecs = Math.Min(Window.TotalSeconds, elapsed.TotalSeconds);
                double bytesPerSec = windowSecs > 0 ? windowBytes / windowSecs : 0;

                // Estimate remaining: use avg bytes/chunk from what we've seen
                double avgBytesPerChunk = _uploaded > 0 ? (double)_bytesUploaded / _uploaded : 0;
                int remainingChunks = _totalChunks - done;
                long estRemainingBytes = (long)(remainingChunks * avgBytesPerChunk);

                string eta;
                string confidence;
                if (bytesPerSec > 0 && estRemainingBytes > 0)
                {
                    var remaining = TimeSpan.FromSeconds(estRemainingBytes / bytesPerSec);
                    eta = $"~{FormatDuration(remaining)} remaining";

                    // Confidence based on how much data we've seen
                    double dataRatio = (double)_uploaded / Math.Max(1, _totalChunks);
                    confidence = dataRatio < 0.2 ? " (early estimate)" :
                                 dataRatio < 0.5 ? " (refining...)" : "";
                }
                else
                {
                    eta = "almost done";
                    confidence = "";
                }

                string rate = bytesPerSec >= 1024*1024
                    ? $"{bytesPerSec/(1024*1024):F1} MB/s"
                    : $"{bytesPerSec/1024:F0} KB/s";

                _log($"job '{_jobName}': uploading to {_host}... {done}/{_totalChunks} chunks ({pct:F1}%) — {rate}, {eta}{confidence}");
            }
        }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
