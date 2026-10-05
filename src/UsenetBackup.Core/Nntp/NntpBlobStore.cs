namespace UsenetBackup.Core.Nntp;

/// <summary>
/// <see cref="IBlobStore"/> backed by an NNTP (Usenet) server: one chunk
/// per article, deterministic message-IDs, yEnc bodies.
///
/// Uploads are resumable: a journal in the repository's catalog records
/// every posted message-ID, and a journal miss falls back to STAT before
/// posting, so an interrupted (or journal-less) upload never re-posts
/// articles the server already has.
/// </summary>
public sealed class NntpBlobStore : IBlobStore, IDisposable
{
    private readonly NntpClient _client;
    private readonly string _newsgroup;
    private readonly string _from;
    private readonly string _repoId;
    private readonly Catalog _journal;
    private bool _disposed;

    public NntpBlobStore(
        NntpClient client,
        string newsgroup,
        string repoId,
        string catalogDbPath,
        string from = "usenet-backup")
    {
        ArgumentException.ThrowIfNullOrEmpty(newsgroup);
        ArgumentException.ThrowIfNullOrEmpty(repoId);
        _client = client;
        _newsgroup = newsgroup;
        _from = string.IsNullOrEmpty(from) ? "usenet-backup" : from;
        _repoId = repoId;
        _journal = new Catalog(catalogDbPath);
    }

    public bool Exists(string chunkIdHex)
    {
        ValidateChunkId(chunkIdHex);
        string messageId = ArticleCodec.MakeMessageId(chunkIdHex, _repoId);
        if (_journal.IsUploaded(messageId))
            return true;
        if (_client.Stat(messageId))
        {
            _journal.RecordUpload(messageId, chunkIdHex); // adopt into journal
            return true;
        }
        return false;
    }

    public void Put(string chunkIdHex, byte[] blob)
    {
        ValidateChunkId(chunkIdHex);
        ArgumentNullException.ThrowIfNull(blob);
        string messageId = ArticleCodec.MakeMessageId(chunkIdHex, _repoId);
        if (_journal.IsUploaded(messageId))
            return; // resume fast path: already posted
        if (_client.Stat(messageId))
        {
            _journal.RecordUpload(messageId, chunkIdHex); // server already has it
            return;
        }
        string article = ArticleCodec.BuildArticle(chunkIdHex, _repoId, blob, _newsgroup, _from);
        _client.Post(article);
        // POST returned 240, but the article may not be retrievable yet
        // (propagation delay). Confirm via STAT with retry before journaling,
        // otherwise a subsequent download would fail with 430.
        WaitForArticle(messageId);
        _journal.RecordUpload(messageId, chunkIdHex);
    }

    /// <summary>
    /// Waits for a newly-posted article to become retrievable via STAT,
    /// with exponential backoff. Throws if the article never appears.
    /// </summary>
    private void WaitForArticle(string messageId)
    {
        const int maxAttempts = 6;
        int delayMs = 500;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (_client.Stat(messageId))
                return;
            if (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
                delayMs *= 2; // 0.5s, 1s, 2s, 4s, 8s = ~15.5s total
            }
        }
        throw new InvalidDataException(
            $"Article {messageId} was posted but never became retrievable (STAT failed after {maxAttempts} attempts).");
    }

    public byte[] Get(string chunkIdHex)
    {
        ValidateChunkId(chunkIdHex);
        string messageId = ArticleCodec.MakeMessageId(chunkIdHex, _repoId);
        string? article = _client.GetArticle(messageId);
        if (article is null)
            throw new InvalidDataException(
                $"Chunk {chunkIdHex} not found on the NNTP server (message-ID {messageId}).");
        var (id, blob) = ArticleCodec.ParseArticle(article);
        if (!id.Equals(chunkIdHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"NNTP server returned the wrong article for chunk {chunkIdHex} (got {id}).");
        return blob;
    }

    /// <summary>
    /// Number of uploads recorded in the journal. This is what this
    /// repository has posted, not a live server inventory.
    /// </summary>
    public long StoredCount => _journal.UploadedCount();

    /// <summary>
    /// Posts an encrypted manifest article. Idempotent via STAT check.
    /// </summary>
    public void PostManifest(string backupId, byte[] encryptedBlob)
    {
        ArgumentNullException.ThrowIfNull(encryptedBlob);
        string messageId = ArticleCodec.MakeManifestMessageId(backupId, _repoId);
        if (_client.Stat(messageId))
            return; // already posted
        string article = ArticleCodec.BuildManifestArticle(
            backupId, _repoId, encryptedBlob, _newsgroup, _from);
        _client.Post(article);
    }

    /// <summary>
    /// Fetches an encrypted manifest article, or null when absent.
    /// </summary>
    public byte[]? GetManifest(string backupId)
    {
        string messageId = ArticleCodec.MakeManifestMessageId(backupId, _repoId);
        string? article = _client.GetArticle(messageId);
        if (article is null)
            return null;
        var (id, blob) = ArticleCodec.ParseManifestArticle(article);
        if (!id.Equals(backupId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Server returned wrong manifest (asked {backupId}, got {id}).");
        return blob;
    }

    /// <summary>
    /// Scans the newsgroup for manifest articles belonging to this repo.
    /// Returns their backup IDs.
    /// </summary>
    public IReadOnlyList<string> ListManifestIds()
    {
        var result = new List<string>();
        foreach (string msgId in _client.ListGroup(_newsgroup))
        {
            string? backupId = ArticleCodec.TryParseManifestMessageId(msgId, _repoId);
            if (backupId is not null)
                result.Add(backupId);
        }
        return result;
    }

    private static void ValidateChunkId(string chunkIdHex)
    {
        if (chunkIdHex.Length != 64 || !chunkIdHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _journal.Dispose();
            _disposed = true;
        }
    }
}
