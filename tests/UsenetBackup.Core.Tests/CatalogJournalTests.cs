using Microsoft.Data.Sqlite;
using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// The upload journal is provider-scoped (DL-2): message IDs derive
/// from chunk + repo and are identical for every provider, so a row
/// recorded for one provider must never count as delivery evidence for
/// another.
/// </summary>
public sealed class CatalogJournalTests : IDisposable
{
    [Fact]
    public void ChunkIds_AreCanonicalizedToLowercase()
    {
        // P2-D14: a mixed-case foreign chunk ID and its lowercase form
        // are the same chunk to every layer.
        string dir = Path.Combine(Path.GetTempPath(), "fk-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var catalog = new Catalog(Path.Combine(dir, "catalog.db"));
            string upper = new string('A', 64);
            string lower = upper.ToLowerInvariant();
            catalog.RecordChunk(upper, 42);
            Assert.True(catalog.HasChunk(lower));
            Assert.True(catalog.HasChunk(upper));
            Assert.Equal(42, catalog.SumChunkSizes(new[] { upper }));
            Assert.Contains(lower, catalog.HasChunks(new[] { upper }));
            // D15 companion: plaintext-length semantics are the caller's
            // contract — RecordChunk stores exactly what it is given.
            Assert.Equal(42, catalog.SumChunkSizes(new[] { lower }));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private readonly string _workDir;

    public CatalogJournalTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-catalog-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Catalog_OpensInWalMode_WithBusyTimeout()
    {
        // NS-4: WAL + busy_timeout so a second process holding the repo
        // (service + CLI) waits out contention instead of failing every
        // operation instantly with "database is locked".
        string db = Path.Combine(_workDir, "catalog.db");
        using var catalog = new Catalog(db);
        using var raw = new SqliteConnection($"Data Source={db};Pooling=false");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)cmd.ExecuteScalar());
    }

    [Fact]
    public void Journal_IsScopedPerProvider()
    {
        string db = Path.Combine(_workDir, "catalog.db");
        using var catalog = new Catalog(db);
        catalog.RecordUpload("<mid@x>", "chunk1", "provider-a");
        Assert.True(catalog.IsUploaded("<mid@x>", "provider-a"));
        Assert.False(catalog.IsUploaded("<mid@x>", "provider-b"));
        Assert.False(catalog.IsUploaded("<mid@x>", ""));
    }

    [Fact]
    public void Journal_LegacyUnscopedRows_AreNotEvidenceForAnyProvider()
    {
        // Build a pre-scoping catalog: uploads keyed by message_id
        // alone, no provider column, one row already recorded.
        string db = Path.Combine(_workDir, "legacy.db");
        {
            using var conn = new SqliteConnection($"Data Source={db};Pooling=false");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE uploads(
                    message_id TEXT PRIMARY KEY,
                    chunk_id TEXT NOT NULL,
                    uploaded_utc TEXT NOT NULL);
                INSERT INTO uploads VALUES('<mid@x>', 'chunk1', '2026-01-01T00:00:00.0000000Z');
                """;
            cmd.ExecuteNonQuery();
        }

        using var catalog = new Catalog(db); // migration runs here
        // The legacy row names no provider: it must not satisfy any
        // provider's evidence check (callers fall back to STAT and
        // re-adopt the article under its real provider).
        Assert.False(catalog.IsUploaded("<mid@x>", "provider-a"));
        // History survives for informational lookups...
        Assert.NotNull(catalog.GetUploadTimeUtc("chunk1"));
        Assert.Equal(1, catalog.UploadedCount());
        // ...and the same message ID can now be recorded per provider
        // without colliding with the legacy row.
        catalog.RecordUpload("<mid@x>", "chunk1", "provider-a");
        Assert.True(catalog.IsUploaded("<mid@x>", "provider-a"));
        Assert.False(catalog.IsUploaded("<mid@x>", "provider-b"));
    }
}
