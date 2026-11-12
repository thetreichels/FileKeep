namespace FileKeep.Core;

/// <summary>
/// Exponential backoff shared by the NNTP retry loops (audit §3.1 D4).
/// One formula for every site: the delay after failed attempt N (1-based)
/// is <c>baseDelay × 2^N</c> — the <c>Thread.Sleep(TimeSpan.FromSeconds(
/// Math.Pow(2, attempt)))</c> shape the three sites previously spelled
/// inline. Retry timing is user-observable (log lines, total wait), so
/// each site keeps its own attempt count and base delay; only the formula
/// is shared, never the parameters.
///
/// Per-site parameters (preserved exactly):
/// - <c>NntpBatchUploader.UploadBatch</c>: maxAttempts 3, baseDelay 1s
///   (sleeps 2s, 4s); exhaustion rethrows the original exception.
/// - <c>BackupScheduler</c> manifest post: maxAttempts 3, baseDelay 1s
///   (sleeps 2s, 4s); exhaustion only warns + logs (no rethrow), so it
///   fits <see cref="TryExecute{T}"/> — caller decides what to do when
///   it returns <c>Succeeded == false</c>.
/// - <c>NntpBlobStore.WaitForArticle</c>: maxAttempts 6, baseDelay 250ms
///   (sleeps 500ms, 1s, 2s, 4s, 8s = ~15.5s total); exhaustion throws
///   <c>InvalidDataException</c>. It is a poll loop, not a try/catch
///   retry, so it adopts <see cref="SleepForAttempt"/> only.
/// </summary>
public static class ExponentialBackoff
{
    /// <summary>
    /// Delay before retrying after failed attempt <paramref name="attempt"/>
    /// (1-based): <paramref name="baseDelay"/> × 2^<paramref name="attempt"/>.
    /// Pure computation, so the timing is unit-testable without sleeping.
    /// </summary>
    public static TimeSpan DelayForAttempt(int attempt, TimeSpan baseDelay)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt is 1-based.");
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), baseDelay, "Base delay must not be negative.");
        double ticks = baseDelay.Ticks * Math.Pow(2, attempt);
        return TimeSpan.FromTicks(ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue.Ticks : (long)ticks);
    }

    /// <summary>
    /// Sleeps for <see cref="DelayForAttempt(int, TimeSpan)"/> — the
    /// one-line replacement for an inline <c>Thread.Sleep</c> backoff.
    /// </summary>
    public static void SleepForAttempt(int attempt, TimeSpan baseDelay) =>
        Thread.Sleep(DelayForAttempt(attempt, baseDelay));

    /// <summary>
    /// Runs <paramref name="body"/> up to <paramref name="maxAttempts"/>
    /// times. After each failed attempt except the last,
    /// <paramref name="onFailedAttempt"/> is invoked (typically to log the
    /// failure) and the thread sleeps for
    /// <c>baseDelay × 2^attempt</c>. Returns whether the body succeeded,
    /// its value, and the last exception. Never throws for a body failure:
    /// the caller decides the exhaustion behaviour (warn, wrap, or
    /// rethrow the returned <c>LastError</c>), which is what differs
    /// between the retry sites.
    /// </summary>
    public static (bool Succeeded, T? Value, Exception? LastError) TryExecute<T>(
        int maxAttempts,
        TimeSpan baseDelay,
        Func<T> body,
        Action<int, Exception>? onFailedAttempt = null)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "At least one attempt is required.");
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), baseDelay, "Base delay must not be negative.");
        ArgumentNullException.ThrowIfNull(body);

        Exception? lastError = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return (true, body(), null);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                {
                    onFailedAttempt?.Invoke(attempt, ex);
                    SleepForAttempt(attempt, baseDelay);
                }
            }
        }
        return (false, default, lastError);
    }
}
