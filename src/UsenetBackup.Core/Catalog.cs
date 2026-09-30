using Microsoft.Data.Sqlite;

namespace UsenetBackup.Core;

/// <summary>
/// catalog.db — a rebuildable index over the repository. Deleting it loses
/// no backup data; it can be reconstructed by scanning chunks/ and manifests/.
/// </summary>
public sealed class Catalog : IDisposable
{
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public Catalog(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS chunks(
                id TEXT PRIMARY KEY,
                size_bytes INTEGER NOT NULL,
                created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS backups(
                id TEXT PRIMARY KEY,
                type TEXT NOT NULL,
                source TEXT NOT NULL,
                created_utc TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public void RecordChunk(string id, long sizeBytes)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO chunks(id, size_bytes, created_utc) VALUES($id, $size, $ts)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$size", sizeBytes);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void RecordBackup(string id, string type, string source, DateTime createdUtc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO backups(id, type, source, created_utc) VALUES($id, $type, $src, $ts)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$ts", createdUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<BackupSummary> ListBackups()
    {
        var list = new List<BackupSummary>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, type, source, created_utc FROM backups ORDER BY created_utc";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new BackupSummary(
                r.GetString(0), r.GetString(1), r.GetString(2),
                DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return list;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _conn.Dispose();
            _disposed = true;
        }
    }
}

public sealed record BackupSummary(string BackupId, string Type, string Source, DateTime CreatedUtc);
