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
    private readonly NntpClient? _client; // single-client mode (legacy)
    private readonly NntpConnectionPool? _pool; // pooled mode
    private readonly string _newsgroup;
    private readonly string _from;
    private readonly string _repoId;
    private readonly Catalog _journal;
    private readonly ChunkMessageIndex? _messageIndex;
    private bool _disposed;

    public NntpBlobStore(
        NntpClient client,
        string newsgroup,
        string repoId,
        string catalogDbPath,
        string from = "usenet-backup",
        ChunkMessageIndex? messageIndex = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(newsgroup);
        ArgumentException.ThrowIfNullOrEmpty(repoId);
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _newsgroup = newsgroup;
        _from = string.IsNullOrEmpty(from) ? "usenet-backup" : from;
        _repoId = repoId;
        _journal = new Catalog(catalogDbPath);
        _messageIndex = messageIndex;
    }

    /// <summary>
    /// Creates a store backed by a connection pool for parallel operations.
    /// Each operation acquires a connection, uses it exclusively, and releases it.
    /// </summary>
    public NntpBlobStore(
        NntpConnectionPool pool,
        string newsgroup,
        string repoId,
        string catalogDbPath,
        string from = "usenet-backup",
        ChunkMessageIndex? messageIndex = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(newsgroup);
        ArgumentException.ThrowIfNullOrEmpty(repoId);
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _newsgroup = newsgroup;
        _from = string.IsNullOrEmpty(from) ? "usenet-backup" : from;
        _repoId = repoId;
        _journal = new Catalog(catalogDbPath);
        _messageIndex = messageIndex;
    }

    /// <summary>True if this store uses a connection pool (parallel-capable).</summary>
    public bool IsPooled => _pool is not null;

    /// <summary>Number of connections in the pool, or 1 for single-client mode.</summary>
    public int ConnectionCount => _pool?.Size ?? 1;

    private T UseClient<T>(Func<NntpClient, T> action)
    {
        if (_pool is not null)
            return _pool.Use(action);
        return action(_client!);
    }

    private void UseClient(Action<NntpClient> action)
    {
        if (_pool is not null)
            _pool.Use(action);
        else
            action(_client!);
    }

    private string ResolveMessageId(string chunkIdHex) =>
        _messageIndex?.GetMessageId(chunkIdHex, _repoId)
            ?? ArticleCodec.MakeMessageId(chunkIdHex, _repoId);

    public bool Exists(string chunkIdHex)
    {
        ValidateChunkId(chunkIdHex);
        string messageId = ResolveMessageId(chunkIdHex);
        if (_journal.IsUploaded(messageId))
            return true;
        if (UseClient(c => c.Stat(messageId)))
        {
            _journal.RecordUpload(messageId, chunkIdHex); // adopt into journal
            return true;
        }
        return false;
    }

    /// <summary>
    /// Live server availability check. Unlike <see cref="Exists"/>, this
    /// NEVER trusts the local journal — it always issues STAT against the
    /// server. Use for retention health checks, where the journal cannot
    /// prove the article is still within provider retention.
    /// </summary>
    public bool ExistsOnServer(string chunkIdHex)
    {
        ValidateChunkId(chunkIdHex);
        string messageId = ResolveMessageId(chunkIdHex);
        return UseClient(c => c.Stat(messageId));
    }

    /// <summary>
    /// Republication for retention refresh. Posts the chunk under a NEW
    /// message ID (servers reject duplicate IDs, so reposting the same ID
    /// does not extend retention), waits until the new article is
    /// retrievable via STAT, records the new identity in the message index,
    /// and returns the new message ID.
    ///
    /// Unlike <see cref="Put"/>, this NEVER early-returns on journal hit or
    /// STAT hit — the point is to create a fresh article with a fresh
    /// retention clock.
    /// </summary>
    /// <returns>The new message ID under which the chunk was published.</returns>
    public string RepublishWithNewIdentity(string chunkIdHex, byte[] blob)
    {
        ValidateChunkId(chunkIdHex);
        ArgumentNullException.ThrowIfNull(blob);
        string newMessageId = ArticleCodec.MakeRefreshMessageId(chunkIdHex, _repoId);
        string article = ArticleCodec.BuildArticleWithMessageId(
            chunkIdHex, newMessageId, blob, _newsgroup, _from);
        UseClient(c => c.Post(article));
        // Confirm the new article is retrievable before recording it —
        // otherwise we'd point the index at a phantom article.
        WaitForArticle(newMessageId);
        _journal.RecordUpload(newMessageId, chunkIdHex);
        _messageIndex?.RecordNewIdentity(chunkIdHex, newMessageId);
        return newMessageId;
    }

    public void Put(string chunkIdHex, byte[] blob)
    {
        ValidateChunkId(chunkIdHex);
        ArgumentNullException.ThrowIfNull(blob);
        string messageId = ArticleCodec.MakeMessageId(chunkIdHex, _repoId);
        if (_journal.IsUploaded(messageId))
            return; // resume fast path: already posted
        if (UseClient(c => c.Stat(messageId)))
        {
            _journal.RecordUpload(messageId, chunkIdHex); // server already has it
            return;
        }
        string article = ArticleCodec.BuildArticle(chunkIdHex, _repoId, blob, _newsgroup, _from);
        UseClient(c => c.Post(article));
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
            if (UseClient(c => c.Stat(messageId)))
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
        string messageId = ResolveMessageId(chunkIdHex);
        string? article = UseClient(c => c.GetArticle(messageId));
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
        if (UseClient(c => c.Stat(messageId)))
            return; // already posted
        string article = ArticleCodec.BuildManifestArticle(
            backupId, _repoId, encryptedBlob, _newsgroup, _from);
        UseClient(c => c.Post(article));
    }

    /// <summary>
    /// Fetches an encrypted manifest article, or null when absent.
    /// </summary>
    public byte[]? GetManifest(string backupId)
    {
        string messageId = ArticleCodec.MakeManifestMessageId(backupId, _repoId);
        string? article = UseClient(c => c.GetArticle(messageId));
        if (article is null)
            return null;
        var (id, blob) = ArticleCodec.ParseManifestArticle(article);
        if (!id.Equals(backupId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Server returned wrong manifest (asked {backupId}, got {id}).");
        return blob;
    }

    /// <summary>
    /// Posts a versioned monthly manifest index. Each version supersedes the
    /// previous (Usenet articles are immutable). The index lists all backup
    /// IDs for the given year-month. Returns the version number posted.
    /// </summary>
    public int PostManifestIndex(string yearMonth, IReadOnlyList<string> backupIds)
    {
        // Find the latest version by probing: STAT v1, v2, ... until 430.
        int version = 1;
        int latestVersion = 0;
        while (UseClient(c => c.Stat(ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, version))))
        {
            latestVersion = version;
            version++;
        }
        // If the latest version already has identical content, don't re-post.
        // This makes re-upload idempotent.
        if (latestVersion > 0)
        {
            var existing = GetLatestManifestIndex(yearMonth);
            if (existing is not null && existing.SequenceEqual(backupIds))
                return latestVersion;
        }
        string messageId = ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, version);
        string body = string.Join("\n", backupIds);
        string article = BuildIndexArticle(yearMonth, version, body);
        UseClient(c => c.Post(article));
        WaitForArticle(messageId);
        return version;
    }

    /// <summary>
    /// Fetches the latest manifest index for a year-month by probing versions
    /// until STAT returns 430. Returns null if no index exists for that month.
    /// </summary>
    public IReadOnlyList<string>? GetLatestManifestIndex(string yearMonth)
    {
        int latestVersion = 0;
        int version = 1;
        // Probe versions until we hit a 430 (not found).
        // Cap at 1000 to avoid infinite loop on misbehaving servers.
        while (version <= 1000)
        {
            string messageId = ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, version);
            if (!UseClient(c => c.Stat(messageId)))
                break;
            latestVersion = version;
            version++;
        }
        if (latestVersion == 0)
            return null;
        string latestId = ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, latestVersion);
        string? article = UseClient(c => c.GetArticle(latestId));
        if (article is null)
            return null;
        // Parse the body: one backup ID per line.
        var (_, body) = ParseIndexArticle(article);
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private string BuildIndexArticle(string yearMonth, int version, string body)
    {
        string messageId = ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, version);
        var sb = new System.Text.StringBuilder();
        sb.Append("From: ").Append(_from).Append("\r\n");
        sb.Append("Newsgroups: ").Append(_newsgroup).Append("\r\n");
        sb.Append("Subject: [usenet-backup] manifest-index ").Append(yearMonth).Append(" v").Append(version).Append("\r\n");
        sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
        sb.Append("\r\n");
        sb.Append(body);
        return sb.ToString();
    }

    private static (string MessageId, string Body) ParseIndexArticle(string article)
    {
        // Split headers from body on the first blank line.
        int sep = article.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int skip = 4;
        if (sep < 0)
        {
            sep = article.IndexOf("\n\n", StringComparison.Ordinal);
            skip = 2;
        }
        if (sep < 0)
            throw new InvalidDataException("Article has no header/body separator.");
        string headers = article[..sep];
        string body = article[(sep + skip)..];
        // Extract Message-ID
        string messageId = "";
        foreach (string line in headers.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("Message-ID:", StringComparison.OrdinalIgnoreCase))
            {
                messageId = t["Message-ID:".Length..].Trim();
                break;
            }
        }
        return (messageId, body);
    }

    /// <summary>
    /// Scans the newsgroup for manifest articles belonging to this repo.
    /// Returns their backup IDs.
    /// </summary>
    public IReadOnlyList<string> ListManifestIds()
    {
        var result = new List<string>();
        foreach (string msgId in UseClient(c => c.ListGroup(_newsgroup)))
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
