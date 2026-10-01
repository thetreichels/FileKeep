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

    public static void Append(string repoRoot, string operation, string details)
    {
        string line = $"{DateTime.UtcNow:O} [{operation}] {details}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(repoRoot, FileName), line);
    }
}
