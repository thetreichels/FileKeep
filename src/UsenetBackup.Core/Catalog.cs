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
        try
        {
            _conn = OpenAndInit(dbPath);
        }
        catch (SqliteException)
        {
            // P2-D2: a torn catalog (power loss mid-write) used to brick
            // every repository operation with a raw SqliteException.
            // Preserve the damaged file the way the sibling JSON stores
            // do (.corrupt-<timestamp>) and start fresh — the catalog is
            // a rebuildable index (P2-D1: BackupRepository rescans
            // chunks/ and manifests/ to refill it), so no backup data is
            // lost. If the fresh start also fails, that error stands.
            QuarantineCorrupt(dbPath);
            _conn = OpenAndInit(dbPath);
        }
    }

    private static void QuarantineCorrupt(string dbPath)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(dbPath + suffix))
                    File.Move(dbPath + suffix, $"{dbPath}.corrupt-{stamp}{suffix}");
            }
            catch
            {
                // Best effort: a file we cannot move stays where it is;
                // the fresh init below will surface any real problem.
            }
        }
    }

    private static SqliteConnection OpenAndInit(string dbPath)
    {
        // Pooling=false: a pooled connection keeps the catalog.db file handle
        // open after Dispose, which blocks file moves/deletes/scans on Windows
        // (and breaks tests that read the raw file after closing the repo).
        // These are short-lived per-operation connections; the pool buys
        // nothing and the lingering lock is a correctness bug.
        var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        try
        {
            conn.Open();
            InitSchema(conn);
            return conn;
        }
        catch
        {
            // Dispose before the exception propagates: the ctor's
            // corruption path renames the database file, and on Windows
            // this connection's open handle would block the rename
            // (the quarantine then silently no-ops and the fresh init
            // fails on the same corrupt file). Linux's unlink semantics
            // masked this entirely.
            conn.Dispose();
            throw;
        }
    }

    private static void InitSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        // NS-4: two processes can hold a repo (service + CLI). WAL lets
        // readers and a writer coexist, and busy_timeout makes lock
        // contention WAIT (up to 30s) instead of failing instantly with
        // "database is locked". WAL is persistent per database file; the
        // timeout is per connection.
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
        cmd.ExecuteNonQuery();
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
                message_id TEXT NOT NULL,
                chunk_id TEXT NOT NULL,
                uploaded_utc TEXT NOT NULL,
                provider TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(message_id, provider));
            CREATE TABLE IF NOT EXISTS downloads(
                message_id TEXT PRIMARY KEY,
                chunk_id TEXT NOT NULL,
                downloaded_utc TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        // No backwards compatibility: catalogs written before the upload
        // journal was provider-scoped are not migrated — a pre-scope
        // uploads table (no provider column) fails on first use instead
        // of being rebuilt.
    }

    /// <summary>
    /// P2-D14: chunk IDs are lowercase hex by construction (SHA-256),
    /// but imported/foreign IDs can arrive mixed-case — and SQLite's
    /// BINARY collation made the catalog case-SENSITIVE while hash
    /// verification was OrdinalIgnoreCase, so the layers disagreed
    /// about whether a chunk was known. Canonicalize at this boundary.
    /// </summary>
    internal static string CanonicalChunkId(string id) => id.ToLowerInvariant();

    public void RecordChunk(string id, long sizeBytes)
    {
        id = CanonicalChunkId(id);
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
    /// True if the chunk has been seen by this repository (staged now or
    /// in any prior backup). This is a statement about the LOCAL repo's
    /// history only — it is NOT evidence the chunk was ever delivered to
    /// any destination (DL-1: a chunk staged by a failed or crashed run,
    /// or delivered to a different destination, is "known" here all the
    /// same). Delivery evidence lives in the provider-scoped upload
    /// journal and in destination-side presence checks; batched staging
    /// dedup consults those, never this table alone.
    /// </summary>
    public bool HasChunk(string id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM chunks WHERE id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", CanonicalChunkId(id));
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
        var idList = ids.Select(CanonicalChunkId).ToList();
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
        var idList = new List<string>(new HashSet<string>(
            ids.Select(CanonicalChunkId), StringComparer.Ordinal));
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
                cmd.Parameters.AddWithValue("$id", CanonicalChunkId(id));
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
    /// Upload journal for resumable NNTP uploads, scoped per provider
    /// (DL-2): message IDs derive from chunk + repo, identical for every
    /// provider, so an unscoped journal would let one provider's uploads
    /// masquerade as another's. <paramref name="providerKey"/> identifies
    /// the provider the article was posted to (host + newsgroup); rows
    /// recorded before scoping carry '' and match no real provider.
    /// Like the rest of the catalog the journal is rebuildable (by
    /// STAT-probing the server), but keeping it makes resume fast and
    /// avoids re-posting.
    /// </summary>
    public void RecordUpload(string messageId, string chunkId, string providerKey)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO uploads(message_id, chunk_id, uploaded_utc, provider) VALUES($mid, $cid, $ts, $prov)";
            cmd.Parameters.AddWithValue("$mid", messageId);
            cmd.Parameters.AddWithValue("$cid", CanonicalChunkId(chunkId));
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$prov", providerKey);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// True when the journal records <paramref name="messageId"/> as
    /// uploaded to the provider identified by
    /// <paramref name="providerKey"/> — and only that provider.
    /// </summary>
    public bool IsUploaded(string messageId, string providerKey)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM uploads WHERE message_id = $mid AND provider = $prov";
            cmd.Parameters.AddWithValue("$mid", messageId);
            cmd.Parameters.AddWithValue("$prov", providerKey);
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
    /// <summary>
    /// The recorded upload time for a chunk. With
    /// <paramref name="providerKey"/> (P2-D16), ONLY that provider's
    /// journal rows count — the DL-2 rule. Without it, any provider's
    /// row answers; that any-provider form is for guards where
    /// over-counting is the safe direction (mode stickiness), never
    /// for delivery decisions.
    /// </summary>
    public DateTime? GetUploadTimeUtc(string chunkId, string? providerKey = null)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = providerKey is null
                ? "SELECT uploaded_utc FROM uploads WHERE chunk_id = $cid LIMIT 1"
                : "SELECT uploaded_utc FROM uploads WHERE chunk_id = $cid AND provider = $prov LIMIT 1";
            cmd.Parameters.AddWithValue("$cid", chunkId);
            if (providerKey is not null)
                cmd.Parameters.AddWithValue("$prov", providerKey);
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

    /// <summary>Number of backups the catalog knows about.</summary>
    public int CountBackups()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM backups";
            return (int)(long)cmd.ExecuteScalar()!;
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
