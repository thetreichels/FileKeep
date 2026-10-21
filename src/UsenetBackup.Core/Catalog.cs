using Microsoft.Data.Sqlite;

namespace UsenetBackup.Core;

/// <summary>
/// catalog.db — a rebuildable index over the repository. Deleting it loses
/// no backup data; it can be reconstructed by scanning chunks/ and manifests/.
/// </summary>
public sealed class Catalog : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new(); // SQLite connection is not thread-safe
    private bool _disposed;

    public Catalog(string dbPath)
    {
        // Pooling=false: a pooled connection keeps the catalog.db file handle
        // open after Dispose, which blocks file moves/deletes/scans on Windows
        // (and breaks tests that read the raw file after closing the repo).
        // These are short-lived per-operation connections; the pool buys
        // nothing and the lingering lock is a correctness bug.
        _conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
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
            CREATE TABLE IF NOT EXISTS uploads(
                message_id TEXT PRIMARY KEY,
                chunk_id TEXT NOT NULL,
                uploaded_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS downloads(
                message_id TEXT PRIMARY KEY,
                chunk_id TEXT NOT NULL,
                downloaded_utc TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public void RecordChunk(string id, long sizeBytes)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO chunks(id, size_bytes, created_utc) VALUES($id, $size, $ts)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// True if the chunk has been seen by this repository (staged now or in
    /// any prior backup). Used by batched staging for cross-batch dedup
    /// after earlier batches' chunks were pruned: a known chunk was either
    /// staged in the current batch or confirmed-uploaded earlier.
    /// </summary>
    public bool HasChunk(string id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM chunks WHERE id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>
    /// Batched version of <see cref="HasChunk"/>: returns the subset of
    /// <paramref name="ids"/> already in the catalog, using a single query.
    /// Reduces SQLite round-trips when checking many chunks (e.g. during
    /// parallel file processing). Returns an empty set for empty input.
    /// </summary>
    public HashSet<string> HasChunks(IEnumerable<string> ids)
    {
        var idList = ids as IReadOnlyList<string> ?? ids.ToList();
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (idList.Count == 0)
            return known;

        lock (_lock)
        {
            // SQLite has a 999-variable limit; chunk the IN clause.
            const int batchSize = 500;
            for (int i = 0; i < idList.Count; i += batchSize)
            {
                var batch = idList.Skip(i).Take(batchSize).ToList();
                using var cmd = _conn.CreateCommand();
                var paramNames = new string[batch.Count];
                for (int j = 0; j < batch.Count; j++)
                {
                    paramNames[j] = "$p" + j;
                    cmd.Parameters.AddWithValue(paramNames[j], batch[j]);
                }
                cmd.CommandText =
                    "SELECT id FROM chunks WHERE id IN (" + string.Join(",", paramNames) + ")";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    known.Add(reader.GetString(0));
            }
        }
        return known;
    }

    /// <summary>
    /// Total catalogued size in bytes of the given chunks. IDs that are not
    /// in the catalog (e.g. a manifest referencing a chunk this repo never
    /// staged) contribute 0. Batched like <see cref="HasChunks"/> to stay
    /// under SQLite's variable limit.
    /// </summary>
    public long SumChunkSizes(IEnumerable<string> ids)
    {
        var idList = new List<string>(new HashSet<string>(ids, StringComparer.Ordinal));
        if (idList.Count == 0)
            return 0;

        long total = 0;
        lock (_lock)
        {
            const int batchSize = 500;
            for (int i = 0; i < idList.Count; i += batchSize)
            {
                var batch = idList.Skip(i).Take(batchSize).ToList();
                using var cmd = _conn.CreateCommand();
                var paramNames = new string[batch.Count];
                for (int j = 0; j < batch.Count; j++)
                {
                    paramNames[j] = "$p" + j;
                    cmd.Parameters.AddWithValue(paramNames[j], batch[j]);
                }
                cmd.CommandText =
                    "SELECT COALESCE(SUM(size_bytes), 0) FROM chunks WHERE id IN (" + string.Join(",", paramNames) + ")";
                total += (long)cmd.ExecuteScalar()!;
            }
        }
        return total;
    }

    /// <summary>
    /// Batched version of <see cref="RecordChunk"/>: inserts multiple chunks
    /// in a single transaction. Used with <see cref="HasChunks"/> to keep
    /// per-chunk SQLite round-trips down during backup.
    /// </summary>
    public void RecordChunks(IEnumerable<(string id, long sizeBytes)> chunks)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            string ts = DateTime.UtcNow.ToString("O");
            foreach (var (id, sizeBytes) in chunks)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO chunks(id, size_bytes, created_utc) VALUES($id, $size, $ts)";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$size", sizeBytes);
                cmd.Parameters.AddWithValue("$ts", ts);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void RecordBackup(string id, string type, string source, DateTime createdUtc)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO backups(id, type, source, created_utc) VALUES($id, $type, $src, $ts)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$src", source);
            cmd.Parameters.AddWithValue("$ts", createdUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Upload journal for resumable NNTP uploads. Like the rest of the
    /// catalog it is rebuildable (by STAT-probing the server), but keeping
    /// it makes resume fast and avoids re-posting.
    /// </summary>
    public void RecordUpload(string messageId, string chunkId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO uploads(message_id, chunk_id, uploaded_utc) VALUES($mid, $cid, $ts)";
            cmd.Parameters.AddWithValue("$mid", messageId);
            cmd.Parameters.AddWithValue("$cid", chunkId);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public bool IsUploaded(string messageId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM uploads WHERE message_id = $mid";
            cmd.Parameters.AddWithValue("$mid", messageId);
            using var r = cmd.ExecuteReader();
            return r.Read();
        }
    }

    public long UploadedCount()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM uploads";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// When a chunk was uploaded (UTC), or null when this repository has
    /// no journal record of uploading it.
    /// </summary>
    public DateTime? GetUploadTimeUtc(string chunkId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT uploaded_utc FROM uploads WHERE chunk_id = $cid LIMIT 1";
            cmd.Parameters.AddWithValue("$cid", chunkId);
            object? value = cmd.ExecuteScalar();
            if (value is null or DBNull)
                return null;
            return DateTime.Parse((string)value, null,
                System.Globalization.DateTimeStyles.RoundtripKind);
        }
    }

    /// <summary>
    /// Download journal for resumable NNTP downloads. Records every chunk
    /// fetched from the server and verified, so an interrupted download
    /// resumes without re-fetching articles it already has.
    /// </summary>
    public void RecordDownload(string messageId, string chunkId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO downloads(message_id, chunk_id, downloaded_utc) VALUES($mid, $cid, $ts)";
            cmd.Parameters.AddWithValue("$mid", messageId);
            cmd.Parameters.AddWithValue("$cid", chunkId);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public bool IsDownloaded(string messageId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM downloads WHERE message_id = $mid";
            cmd.Parameters.AddWithValue("$mid", messageId);
            using var r = cmd.ExecuteReader();
            return r.Read();
        }
    }

    public long DownloadedCount()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM downloads";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public IReadOnlyList<BackupSummary> ListBackups()
    {
        lock (_lock)
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

/// <summary>Outcome of <see cref="BackupRepository.DownloadChunks"/>.</summary>
public sealed record DownloadResult(int Downloaded, int AlreadyPresent, int Total, int Reconstructed = 0);
