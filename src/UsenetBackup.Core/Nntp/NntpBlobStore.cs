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
    private readonly string? _providerKey;
    private bool _disposed;

    public NntpBlobStore(
        NntpClient client,
        string newsgroup,
        string repoId,
        string catalogDbPath,
        string from = "usenet-backup",
        ChunkMessageIndex? messageIndex = null,
        string? providerKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(newsgroup);
        ArgumentException.ThrowIfNullOrEmpty(repoId);
        if (messageIndex is not null && string.IsNullOrEmpty(providerKey))
            throw new ArgumentException(
                "providerKey is required when messageIndex is provided: the index " +
                "is per-provider, so lookups need to know which provider's " +
                "refresh history to use.", nameof(providerKey));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _newsgroup = newsgroup;
        _from = string.IsNullOrEmpty(from) ? "usenet-backup" : from;
        _repoId = repoId;
        _journal = new Catalog(catalogDbPath);
        _messageIndex = messageIndex;
        _providerKey = providerKey;
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
        ChunkMessageIndex? messageIndex = null,
        string? providerKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(newsgroup);
        ArgumentException.ThrowIfNullOrEmpty(repoId);
        if (messageIndex is not null && string.IsNullOrEmpty(providerKey))
            throw new ArgumentException(
                "providerKey is required when messageIndex is provided: the index " +
                "is per-provider, so lookups need to know which provider's " +
                "refresh history to use.", nameof(providerKey));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _newsgroup = newsgroup;
        _from = string.IsNullOrEmpty(from) ? "usenet-backup" : from;
        _repoId = repoId;
        _journal = new Catalog(catalogDbPath);
        _messageIndex = messageIndex;
        _providerKey = providerKey;
    }

    /// <summary>True if this store uses a connection pool (parallel-capable).</summary>
    public bool IsPooled => _pool is not null;

    /// <summary>
    /// Default read-time cap for a single article download (64 MiB). Covers
    /// chunk sizes up to ~30 MiB at default settings; call sites that know the
    /// repo's chunk size should set <see cref="MaxArticleBytes"/> tighter from
    /// the repo's chunk-size-derived bound.
    /// </summary>
    public const long DefaultMaxArticleBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Read-time cap (characters) for a single ARTICLE response. The download
    /// aborts mid-read when exceeded, so a malicious oversized article cannot
    /// exhaust memory. Set from the repo's chunk-size-derived bound when known.
    /// </summary>
    public long MaxArticleBytes { get; set; } = DefaultMaxArticleBytes;

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
        _messageIndex?.GetMessageId(_providerKey!, chunkIdHex, _repoId)
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
        _messageIndex?.RecordNewIdentity(_providerKey!, chunkIdHex, newMessageId);
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

    private string ResolveVolumeMessageId(string volumeIdHex)
    {
        if (_messageIndex is not null &&
            _messageIndex.TryGetMessageId(_providerKey!, volumeIdHex, out string? recorded) &&
            recorded is not null)
            return recorded;
        return ArticleCodec.MakeVolumeMessageId(volumeIdHex, _repoId);
    }

    /// <summary>
    /// Posts a packed volume as a single article. Idempotent via the journal
    /// fast path and STAT fallback, like <see cref="Put"/>. Refuses up front
    /// when the framed article would exceed <see cref="MaxArticleBytes"/>
    /// instead of failing mid-POST with a bare server 441.
    /// </summary>
    public void PutVolume(string volumeIdHex, byte[] volumeBytes)
    {
        ValidateChunkId(volumeIdHex);
        ArgumentNullException.ThrowIfNull(volumeBytes);
        string messageId = ArticleCodec.MakeVolumeMessageId(volumeIdHex, _repoId);
        if (_journal.IsUploaded(messageId))
            return; // resume fast path: already posted
        if (UseClient(c => c.Stat(messageId)))
        {
            _journal.RecordUpload(messageId, volumeIdHex); // server already has it
            return;
        }
        string article = ArticleCodec.BuildVolumeArticle(
            volumeIdHex, _repoId, volumeBytes, _newsgroup, _from);
        if (article.Length > MaxArticleBytes)
            throw new InvalidDataException(
                $"Volume article ({article.Length} bytes) exceeds the {MaxArticleBytes}-byte article limit. " +
                "Lower volume_size_bytes in repo.json and re-run the upload.");
        UseClient(c => c.Post(article));
        WaitForArticle(messageId);
        _journal.RecordUpload(messageId, volumeIdHex);
    }

    /// <summary>
    /// Fetches a volume article, resolving refreshed identities through the
    /// message index. Validates the X-UsenetBackup-Volume header matches.
    /// Unpacking and per-chunk verification are the caller's job.
    /// </summary>
    public byte[] GetVolume(string volumeIdHex)
    {
        ValidateChunkId(volumeIdHex);
        string messageId = ResolveVolumeMessageId(volumeIdHex);
        string? article = UseClient(c => c.GetArticle(messageId, MaxArticleBytes));
        if (article is null)
            throw new InvalidDataException(
                $"Volume {volumeIdHex} not found on the NNTP server (message-ID {messageId}).");
        var (id, blob) = ArticleCodec.ParseVolumeArticle(article);
        if (!id.Equals(volumeIdHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"NNTP server returned the wrong article for volume {volumeIdHex} (got {id}).");
        return blob;
    }

    /// <summary>
    /// Republication for retention refresh. Posts the volume under a NEW
    /// message ID (servers reject duplicate IDs), waits until retrievable,
    /// records the new identity in the message index, and returns the new
    /// message ID. Never early-returns — the point is a fresh article with a
    /// fresh retention clock.
    /// </summary>
    public string RepublishVolumeWithNewIdentity(string volumeIdHex, byte[] volumeBytes)
    {
        ValidateChunkId(volumeIdHex);
        ArgumentNullException.ThrowIfNull(volumeBytes);
        string newMessageId = ArticleCodec.MakeRefreshVolumeMessageId(volumeIdHex, _repoId);
        string article = ArticleCodec.BuildVolumeArticleWithMessageId(
            volumeIdHex, newMessageId, volumeBytes, _newsgroup, _from);
        if (article.Length > MaxArticleBytes)
            throw new InvalidDataException(
                $"Volume article ({article.Length} bytes) exceeds the {MaxArticleBytes}-byte article limit. " +
                "Lower volume_size_bytes in repo.json and re-run.");
        UseClient(c => c.Post(article));
        WaitForArticle(newMessageId);
        _journal.RecordUpload(newMessageId, volumeIdHex);
        _messageIndex?.RecordNewIdentity(_providerKey!, volumeIdHex, newMessageId);
        return newMessageId;
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
        string? article = UseClient(c => c.GetArticle(messageId, MaxArticleBytes));
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
        string? article = UseClient(c => c.GetArticle(messageId, MaxArticleBytes));
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
        // Find the latest version by probing (tolerates expired versions).
        int latestVersion = ProbeLatestVersion(
            v => ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, v));
        int version = latestVersion + 1;
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
        int latestVersion = ProbeLatestVersion(
            v => ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, v));
        if (latestVersion == 0)
            return null;
        string latestId = ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, latestVersion);
        string? article = UseClient(c => c.GetArticle(latestId, MaxArticleBytes));
        if (article is null)
            return null;
        // Parse the body: one backup ID per line.
        var (_, body) = ParseIndexArticle(article);
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Posts a versioned chunk-message-identity index. Each version
    /// supersedes the previous (Usenet articles are immutable). The index
    /// maps provider keys to chunk-ID -&gt; message-ID mappings, so a
    /// recovering machine can find refreshed articles. Returns the version
    /// number posted.
    /// </summary>
    public int PostMessageIndex(string indexJson)
    {
        // Find the latest version by probing (tolerates expired versions).
        int latestVersion = ProbeLatestVersion(
            v => ArticleCodec.MakeMessageIndexMessageId(_repoId, v));
        int version = latestVersion + 1;
        // If the latest version already has identical content, don't re-post.
        if (latestVersion > 0)
        {
            var existing = GetLatestMessageIndex();
            if (existing is not null && existing.Trim() == indexJson.Trim())
                return latestVersion;
        }
        string messageId = ArticleCodec.MakeMessageIndexMessageId(_repoId, version);
        var sb = new System.Text.StringBuilder();
        sb.Append("From: ").Append(_from).Append("\r\n");
        sb.Append("Newsgroups: ").Append(_newsgroup).Append("\r\n");
        sb.Append("Subject: [usenet-backup] message-index v").Append(version).Append("\r\n");
        sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append("\r\n");
        sb.Append(indexJson);
        UseClient(c => c.Post(sb.ToString()));
        WaitForArticle(messageId);
        return version;
    }

    /// <summary>
    /// Fetches the latest versioned chunk-message-identity index.
    /// Version discovery tolerates gaps: an expired older version does not
    /// hide a newer surviving one.
    ///
    /// Returns null when no index version exists on the server — either no
    /// index has ever been published, or every published version has
    /// expired. Throws <see cref="MessageIndexFetchException"/> when a
    /// version IS listed on the server but cannot be retrieved or parsed:
    /// the index is then expected-but-broken, and the caller must surface
    /// an explicit recovery error rather than silently falling back to
    /// stale message IDs.
    /// </summary>
    public string? GetLatestMessageIndex()
    {
        int latestVersion = ProbeLatestVersion(
            v => ArticleCodec.MakeMessageIndexMessageId(_repoId, v));
        if (latestVersion == 0)
            return null;
        string latestId = ArticleCodec.MakeMessageIndexMessageId(_repoId, latestVersion);
        string? article;
        try
        {
            article = UseClient(c => c.GetArticle(latestId, MaxArticleBytes));
        }
        catch (Exception ex)
        {
            throw new MessageIndexFetchException(
                $"Message-identity index v{latestVersion} ({latestId}) is listed on the " +
                $"server but could not be retrieved: {ex.Message}", ex);
        }
        if (article is null)
            throw new MessageIndexFetchException(
                $"Message-identity index v{latestVersion} ({latestId}) is listed on the " +
                "server but the article body could not be fetched.");
        string body;
        try
        {
            (_, body) = ParseIndexArticle(article);
        }
        catch (Exception ex)
        {
            throw new MessageIndexFetchException(
                $"Message-identity index v{latestVersion} ({latestId}) could not be parsed: " +
                ex.Message, ex);
        }
        return body.Trim();
    }

    /// <summary>
    /// Fetches the latest published message-identity index and loads it
    /// into <paramref name="index"/>. A retention refresh republishes
    /// articles under new message IDs; without this sync a recovering
    /// machine would request stale (possibly expired) IDs.
    ///
    /// No-op when no index was ever published: the local index or
    /// deterministic IDs are then authoritative. Throws
    /// <see cref="MessageIndexFetchException"/> with an explicit recovery
    /// error when an index exists on the server but cannot be retrieved
    /// or parsed — continuing with stale IDs in that case would turn an
    /// index problem into later chunk-download failures.
    /// </summary>
    public void SyncMessageIndex(ChunkMessageIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        string? remoteJson;
        try
        {
            remoteJson = GetLatestMessageIndex();
        }
        catch (MessageIndexFetchException ex)
        {
            throw new MessageIndexFetchException(
                "A message-identity index is published for this repository but it could not be " +
                "retrieved from the Usenet server. A retention refresh may have republished " +
                "articles under new message IDs; continuing with the local index or " +
                "deterministic IDs could request stale (possibly expired) articles and turn " +
                "this into chunk-download failures. Resolve the server issue and retry. " +
                $"Detail: {ex.Message}", ex);
        }
        if (remoteJson is null)
            return; // never published: local index / deterministic IDs are authoritative
        try
        {
            index.LoadFromJson(remoteJson);
        }
        catch (Exception ex)
        {
            throw new MessageIndexFetchException(
                "The published message-identity index could not be parsed. Continuing with " +
                "the local index or deterministic IDs could request stale (possibly expired) " +
                $"articles. Detail: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Finds the highest published index version, tolerating gaps: a
    /// version whose article has expired (or never propagated) does not
    /// hide newer surviving versions. Stops after a run of consecutive
    /// misses so a long-dead series still terminates quickly.
    /// </summary>
    private int ProbeLatestVersion(Func<int, string> makeMessageId)
    {
        const int maxConsecutiveMisses = 5;
        const int hardCap = 1000;
        int latest = 0;
        int misses = 0;
        for (int version = 1; version <= hardCap; version++)
        {
            if (UseClient(c => c.Stat(makeMessageId(version))))
            {
                latest = version;
                misses = 0;
            }
            else if (++misses >= maxConsecutiveMisses)
            {
                break;
            }
        }
        return latest;
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
