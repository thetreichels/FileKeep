namespace UsenetBackup.Core;

/// <summary>
/// Append-only audit log of critical repository operations
/// (init, backup, restore, verify). One UTC-timestamped line per
/// operation with key=value details. Informational only: it is not
/// covered by manifest hashes and is never required for restore.
/// </summary>
public static class OperationLog
{
    public const string FileName = "operations.log";

    // P2-C12: Append is called from scheduler threads, dashboard tasks,
    // and retention concurrently, and File.AppendAllText takes no
    // share lock that tolerates another writer — overlapping appends
    // threw sharing-violation IOExceptions at call sites that treat a
    // log write as infallible plumbing (including FinalizeManifest).
    // One static lock serializes appends process-wide; the log is
    // low-volume, so contention is negligible.
    private static readonly object AppendLock = new();

    public static void Append(string repoRoot, string operation, string details)
    {
        string line = $"{DateTime.UtcNow:O} [{operation}] {details}{Environment.NewLine}";
        string path = Path.Combine(repoRoot, FileName);
        lock (AppendLock)
        {
            try
            {
                File.AppendAllText(path, line);
            }
            catch (IOException)
            {
                // A reader (the dashboard log view) or an external
                // process can still hold the file briefly despite the
                // lock. One short retry, then give up silently — a log
                // line must never take down the operation it describes.
                System.Threading.Thread.Sleep(50);
                try { File.AppendAllText(path, line); }
                catch { /* best effort */ }
            }
        }
    }
}
