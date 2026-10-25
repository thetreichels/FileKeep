using System.Text.Json;
using FileKeep.Core.Json;

namespace FileKeep.Core.Nntp;

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
    private readonly IndexAuth? _indexAuth;
    private bool _disposed;

    public NntpBlobStore(
        NntpClient client,
        string newsgroup,
        string repoId,
        string catalogDbPath,
        string from = "poster",
        ChunkMessageIndex? messageIndex = null,
        string? providerKey = null,
        IndexAuth? indexAuth = null)
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
        _from = string.IsNullOrEmpty(from) ? "poster" : from;
        _repoId = repoId;
        _journal = new Catalog(catalogDbPath);
        _messageIndex = messageIndex;
        _providerKey = providerKey;
        _indexAuth = indexAuth;
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
        string from = "poster",
        ChunkMessageIndex? messageIndex = null,
        string? providerKey = null,
        IndexAuth? indexAuth = null)
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
        _from = string.IsNullOrEmpty(from) ? "poster" : from;
        _repoId = repoId;
        _journal = new Catalog(catalogDbPath);
        _messageIndex = messageIndex;
        _providerKey = providerKey;
        _indexAuth = indexAuth;
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

    /// <summary>
    /// Journal scope for this store's provider (DL-2): journal rows are
    /// only ever read and written under this key, so one provider's
    /// uploads can't be mistaken for another's. Stores constructed
    /// without a provider key share the legacy '' scope.
    /// </summary>
    private string JournalKey => _providerKey ?? "";

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
        if (_journal.IsUploaded(messageId, JournalKey))
            return true;
        if (UseClient(c => c.Stat(messageId)))
        {
            _journal.RecordUpload(messageId, chunkIdHex, JournalKey); // adopt into journal
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
        _journal.RecordUpload(newMessageId, chunkIdHex, JournalKey);
        _messageIndex?.RecordNewIdentity(_providerKey!, chunkIdHex, newMessageId);
        return newMessageId;
    }

    public void Put(string chunkIdHex, byte[] blob)
    {
        ValidateChunkId(chunkIdHex);
        ArgumentNullException.ThrowIfNull(blob);
        string messageId = ArticleCodec.MakeMessageId(chunkIdHex, _repoId);
        if (_journal.IsUploaded(messageId, JournalKey))
            return; // resume fast path: already posted
        if (UseClient(c => c.Stat(messageId)))
        {
            _journal.RecordUpload(messageId, chunkIdHex, JournalKey); // server already has it
            return;
        }
        string article = ArticleCodec.BuildArticle(chunkIdHex, _repoId, blob, _newsgroup, _from);
        UseClient(c => c.Post(article));
        // POST returned 240, but the article may not be retrievable yet
        // (propagation delay). Confirm via STAT with retry before journaling,
        // otherwise a subsequent download would fail with 430.
        WaitForArticle(messageId);
        _journal.RecordUpload(messageId, chunkIdHex, JournalKey);
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
    /// Posts a packed volume as a single article with neutral unnumbered naming
    /// ("{volumeId8}.bin" - yEnc). For maintenance articles outside a backup's
    /// numbered post set (e.g. parity blocks). Idempotent like <see cref="Put"/>.
    /// </summary>
    public void PutVolume(string volumeIdHex, byte[] volumeBytes)
    {
        ValidateChunkId(volumeIdHex);
        ArgumentNullException.ThrowIfNull(volumeBytes);
        string messageId = ArticleCodec.MakeVolumeMessageId(volumeIdHex, _repoId);
        if (_journal.IsUploaded(messageId, JournalKey))
            return;
        if (UseClient(c => c.Stat(messageId)))
        {
            _journal.RecordUpload(messageId, volumeIdHex, JournalKey);
            return;
        }
        string article = ArticleCodec.BuildVolumeArticleWithMessageId(
            volumeIdHex, messageId, volumeBytes, _newsgroup, _from);
        if (article.Length > MaxArticleBytes)
            throw new InvalidDataException(
                $"Volume article ({article.Length} bytes) exceeds the {MaxArticleBytes}-byte article limit. " +
                "Lower volume_size_bytes in repo.json and re-run the upload.");
        UseClient(c => c.Post(article));
        WaitForArticle(messageId);
        _journal.RecordUpload(messageId, volumeIdHex, JournalKey);
    }

    /// <summary>
    /// Posts a packed volume as a single article, as part <paramref name="partNumber"/>
    /// of <paramref name="partCount"/> in the backup's post set (part 1 is the NFO).
    /// The viewable naming is neutral (RAR-style part name, standard multi-part
    /// subject). Idempotent via the journal fast path and STAT fallback, like
    /// <see cref="Put"/>. Refuses up front when the framed article would exceed
    /// <see cref="MaxArticleBytes"/> instead of failing mid-POST with a bare
    /// server 441.
    /// </summary>
    public void PutVolume(string volumeIdHex, byte[] volumeBytes, string setName, int partNumber, int partCount)
    {
        ValidateChunkId(volumeIdHex);
        ArgumentNullException.ThrowIfNull(volumeBytes);
        ArgumentException.ThrowIfNullOrEmpty(setName);
        if (partNumber < 2 || partCount < partNumber)
            throw new ArgumentOutOfRangeException(nameof(partNumber),
                "Volume parts start at 2 (part 1 is the NFO).");
        string messageId = ArticleCodec.MakeVolumeMessageId(volumeIdHex, _repoId);
        if (_journal.IsUploaded(messageId, JournalKey))
            return; // resume fast path: already posted
        if (UseClient(c => c.Stat(messageId)))
        {
            _journal.RecordUpload(messageId, volumeIdHex, JournalKey); // server already has it
            return;
        }
        string article = ArticleCodec.BuildVolumeArticle(
            volumeIdHex, _repoId, volumeBytes, _newsgroup, _from, setName, partNumber, partCount);
        if (article.Length > MaxArticleBytes)
            throw new InvalidDataException(
                $"Volume article ({article.Length} bytes) exceeds the {MaxArticleBytes}-byte article limit. " +
                "Lower volume_size_bytes in repo.json and re-run the upload.");
        UseClient(c => c.Post(article));
        WaitForArticle(messageId);
        _journal.RecordUpload(messageId, volumeIdHex, JournalKey);
    }

    /// <summary>
    /// Posts the backup's NFO as part 1 of the post set. The NFO carries no
    /// software branding — just the set metadata and the archival notice.
    /// Idempotent: the message-ID is derived from (backupId, repoId), so a
    /// repost is a STAT-confirmed no-op.
    /// </summary>
    public void PostNfo(string backupId, string nfoText, string setName, int totalParts,
        bool recheckServer = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupId);
        ArgumentException.ThrowIfNullOrEmpty(nfoText);
        ArgumentException.ThrowIfNullOrEmpty(setName);
        if (totalParts < 1)
            throw new ArgumentOutOfRangeException(nameof(totalParts));
        PostNfo(backupId, batchIndex: 0, nfoText, setName, totalParts, recheckServer);
    }

    /// <summary>
    /// Posts a batch NFO for batched staging. Each batch gets its own NFO
    /// article (distinct message ID per batch index) describing that
    /// batch's post set.
    /// </summary>
    public bool PostNfo(string backupId, int batchIndex, string nfoText, string setName, int totalParts,
        bool recheckServer = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupId);
        ArgumentException.ThrowIfNullOrEmpty(nfoText);
        ArgumentException.ThrowIfNullOrEmpty(setName);
        if (totalParts < 1)
            throw new ArgumentOutOfRangeException(nameof(totalParts));
        string messageId = ArticleCodec.MakeNfoMessageId(backupId, batchIndex, _repoId);
        if (!recheckServer && _journal.IsUploaded(messageId, JournalKey))
            return false;
        if (UseClient(c => c.Stat(messageId)))
        {
            _journal.RecordUpload(messageId, backupId, JournalKey);
            return false;
        }
        string article = ArticleCodec.BuildNfoArticle(
            nfoText, backupId, batchIndex, _repoId, _newsgroup, _from, setName, totalParts);
        UseClient(c => c.Post(article));
        WaitForArticle(messageId);
        _journal.RecordUpload(messageId, backupId, JournalKey);
        return true;
    }

    /// <summary>
    /// Fetches a volume article, resolving refreshed identities through the
    /// message index. Validates the X-Part header matches.
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
        _journal.RecordUpload(newMessageId, volumeIdHex, JournalKey);
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
    /// Posts an encrypted manifest article. Idempotent via the journal fast
    /// path and STAT fallback, like <see cref="Put"/>. The manifest post is
    /// journaled so interrupted uploads resume without re-posting.
    /// </summary>
    /// <summary>
    /// True when this store's provider has the backup's manifest
    /// article — per the provider-scoped journal (posted by us, or
    /// adopted via STAT). Lets the manifest index advertise only
    /// backups actually delivered to THIS provider (SF-15).
    /// </summary>
    public bool IsManifestUploaded(string backupId)
    {
        string messageId = ArticleCodec.MakeManifestMessageId(backupId, _repoId);
        return _journal.IsUploaded(messageId, JournalKey);
    }

    /// <param name="recheckServer">
    /// When true, the journal fast path is skipped and the server is
    /// STAT-checked even for a journaled article (SF-10): the journal
    /// never expires entries, so without a recheck an expired manifest
    /// article would never be reposted by anything. Returns true when
    /// an article was actually posted.
    /// </param>
    public bool PostManifest(string backupId, byte[] encryptedBlob, bool recheckServer = false)
    {
        ArgumentNullException.ThrowIfNull(encryptedBlob);
        string messageId = ArticleCodec.MakeManifestMessageId(backupId, _repoId);
        if (!recheckServer && _journal.IsUploaded(messageId, JournalKey))
            return false; // resume fast path: already posted
        if (UseClient(c => c.Stat(messageId)))
        {
            _journal.RecordUpload(messageId, backupId, JournalKey); // server already has it
            return false;
        }
        string article = ArticleCodec.BuildManifestArticle(
            backupId, _repoId, encryptedBlob, _newsgroup, _from);
        UseClient(c => c.Post(article));
        WaitForArticle(messageId);
        _journal.RecordUpload(messageId, backupId, JournalKey);
        return true;
    }

    /// <summary>
    /// True when the server currently holds the backup's manifest
    /// article (live STAT of its deterministic message ID — never the
    /// journal, which cannot know about expiry).
    /// </summary>
    public bool ManifestExistsOnServer(string backupId)
    {
        string messageId = ArticleCodec.MakeManifestMessageId(backupId, _repoId);
        return UseClient(c => c.Stat(messageId));
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
    /// <param name="forceNewVersion">
    /// Post a fresh version even when the latest has identical content.
    /// Uploads leave this off (the dedup below makes re-upload
    /// idempotent); retention sets it — restarting the index article's
    /// retention clock is the entire point there (SF-10), exactly as
    /// data articles are republished under new identities.
    /// </param>
    public int PostManifestIndex(string yearMonth, IReadOnlyList<string> backupIds,
        bool forceNewVersion = false)
    {
        // Find the latest version by probing (tolerates expired versions).
        int latestVersion = ProbeLatestVersion(
            v => ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, v));
        int version = latestVersion + 1;
        // If the latest version already has identical content, don't re-post.
        // This makes re-upload idempotent.
        if (!forceNewVersion && latestVersion > 0)
        {
            var existing = GetLatestManifestIndex(yearMonth);
            if (existing is not null && existing.SequenceEqual(backupIds))
                return latestVersion;
        }
        string messageId = ArticleCodec.MakeManifestIndexMessageId(_repoId, yearMonth, version);
        string body = string.Join("\n", backupIds);
        if (_indexAuth is not null)
        {
            // Authenticate the index (B-M1): the HMAC covers the canonical
            // payload (the ID list exactly as joined above) and rides as a
            // trailer line. Keyed readers refuse an index without a valid
            // trailer, so a forged higher version cannot hide backups.
            body += "\n" + IndexHmacLinePrefix + _indexAuth.ComputeManifestIndexHmac(body);
        }
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
        // Parse the body: one backup ID per line, plus (when posted by a
        // keyed store) a trailing HMAC line authenticating the list.
        var (_, body) = ParseIndexArticle(article);
        string[] lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_indexAuth is not null)
        {
            // Authenticated mode (B-M1): a missing or invalid HMAC means
            // this index was not posted by a holder of the repo key —
            // treat it as NOT THERE (never adopt a forged index), exactly
            // as if no index existed for the month.
            if (lines.Length == 0 ||
                !lines[^1].StartsWith(IndexHmacLinePrefix, StringComparison.Ordinal))
                return null;
            string[] ids = lines[..^1];
            string canonical = string.Join("\n", ids);
            if (!_indexAuth.VerifyManifestIndexHmac(canonical,
                    lines[^1][IndexHmacLinePrefix.Length..]))
                return null;
            return ids;
        }
        // Keyless store: accept both formats; strip an HMAC trailer this
        // store cannot verify.
        if (lines.Length > 0 &&
            lines[^1].StartsWith(IndexHmacLinePrefix, StringComparison.Ordinal))
            lines = lines[..^1];
        return lines;
    }

    /// <summary>Trailer line prefix authenticating a manifest-index body.</summary>
    private const string IndexHmacLinePrefix = "sig:";

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
        string postedBody = indexJson;
        if (_indexAuth is not null)
        {
            // Authenticate the index (B-H1): the posted body is an envelope
            // {"data": <map>, "sig": "<hex>"} whose HMAC covers the
            // canonical index JSON (the field-blanking mirror of the
            // manifest root_hmac pattern — the canonical form is the index
            // content with the HMAC field absent). Canonicalizing here
            // (parse + re-serialize) makes the dedup comparison and every
            // future verification byte-stable.
            string canonical = CanonicalizeChunkIndexJson(indexJson);
            // If the latest AUTHENTIC version already has identical
            // content, don't re-post. (A forged latest version reads as
            // "not there" and never suppresses a legitimate re-post.)
            if (latestVersion > 0)
            {
                var existing = GetLatestMessageIndex();
                if (existing is not null && existing == canonical)
                    return latestVersion;
            }
            postedBody = "{\"data\":" + canonical + ",\"sig\":\"" +
                _indexAuth.ComputeChunkIndexHmac(canonical) + "\"}";
        }
        else if (latestVersion > 0)
        {
            // If the latest version already has identical content, don't re-post.
            var existing = GetLatestMessageIndex();
            if (existing is not null && existing.Trim() == indexJson.Trim())
                return latestVersion;
        }
        string messageId = ArticleCodec.MakeMessageIndexMessageId(_repoId, version);
        var sb = new System.Text.StringBuilder();
        sb.Append("From: ").Append(ArticleCodec.FormatFrom(_from)).Append("\r\n");
        sb.Append("Date: ").Append(ArticleCodec.Rfc2822DateNow()).Append("\r\n");
        sb.Append("Newsgroups: ").Append(_newsgroup).Append("\r\n");
        sb.Append("Subject: ").Append(ArticleCodec.SingleSubject($"{_repoId[..8]}.bin")).Append("\r\n");
        sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append("\r\n");
        sb.Append(postedBody);
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
    ///
    /// When this store carries an <see cref="IndexAuth"/>, the contract
    /// tightens (B-H1): the latest version is adopted only if it is an
    /// envelope whose HMAC verifies under the repo key. Anything else —
    /// unsigned, forged, tampered, unfetchable, unparseable — is treated
    /// as NOT THERE and this returns null, because attacker noise is
    /// indistinguishable from a broken article and neither may be
    /// adopted. On success the returned string is the canonical inner
    /// index JSON (the envelope is unwrapped).
    /// </summary>
    public string? GetLatestMessageIndex()
    {
        int latestVersion = ProbeLatestVersion(
            v => ArticleCodec.MakeMessageIndexMessageId(_repoId, v));
        if (latestVersion == 0)
            return null;
        string latestId = ArticleCodec.MakeMessageIndexMessageId(_repoId, latestVersion);
        if (_indexAuth is not null)
        {
            string? raw;
            try
            {
                raw = UseClient(c => c.GetArticle(latestId, MaxArticleBytes));
            }
            catch
            {
                return null; // unauthenticated outcome: treat as not there
            }
            if (raw is null)
                return null;
            string authBody;
            try
            {
                (_, authBody) = ParseIndexArticle(raw);
            }
            catch
            {
                return null; // unparseable: cannot authenticate, not there
            }
            return TryUnwrapAuthenticatedChunkIndex(authBody);
        }
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
        // Keyless store: unwrap an authenticated envelope if one was
        // posted (the mappings are readable without the key; only
        // adoption under authentication requires it), else the raw body.
        return TryExtractChunkIndexEnvelope(body, out _) ?? body.Trim();
    }

    /// <summary>
    /// Canonical form of a chunk message index: the map parsed and
    /// re-serialized through the same source-generated context
    /// ChunkMessageIndex.ToJson uses, so poster and verifier derive
    /// byte-identical payloads for the HMAC regardless of the input
    /// string's whitespace or key ordering.
    /// </summary>
    private static string CanonicalizeChunkIndexJson(string json)
    {
        var map = JsonSerializer.Deserialize(json,
            FileKeepJsonContext.Default.DictionaryStringDictionaryStringString);
        if (map is null)
            throw new InvalidDataException("Chunk message index JSON deserialized to null.");
        return JsonSerializer.Serialize(map,
            FileKeepJsonContext.Default.DictionaryStringDictionaryStringString);
    }

    /// <summary>
    /// Extracts the payload of a chunk-index envelope
    /// ({"data": {...}, "sig": "..."}): returns the canonical
    /// inner index JSON and the presented HMAC, or null when the body
    /// is not a well-formed envelope.
    /// </summary>
    private static string? TryExtractChunkIndexEnvelope(string body, out string? hmacHex)
    {
        hmacHex = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("data", out var indexEl) ||
                indexEl.ValueKind != JsonValueKind.Object)
                return null;
            if (root.TryGetProperty("sig", out var hmacEl) &&
                hmacEl.ValueKind == JsonValueKind.String)
                hmacHex = hmacEl.GetString();
            return CanonicalizeChunkIndexJson(indexEl.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Verifies a chunk-index envelope against this store's
    /// <see cref="IndexAuth"/>: returns the canonical inner index JSON,
    /// or null when the envelope is missing, malformed, or its HMAC
    /// does not verify (forged/tampered — treated as not there).
    /// </summary>
    private string? TryUnwrapAuthenticatedChunkIndex(string body)
    {
        string? canonical = TryExtractChunkIndexEnvelope(body, out string? hmacHex);
        if (canonical is null)
            return null;
        return _indexAuth!.VerifyChunkIndexHmac(canonical, hmacHex) ? canonical : null;
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
    ///
    /// With an <see cref="IndexAuth"/> on this store, an index that
    /// fails authentication is treated as never published (B-H1): the
    /// sync no-ops and the local index is left exactly as it was —
    /// never replaced, never persisted over.
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
            bool exists = UseClient(c => c.Stat(makeMessageId(version)));
            if (exists)
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
        sb.Append("From: ").Append(ArticleCodec.FormatFrom(_from)).Append("\r\n");
        sb.Append("Date: ").Append(ArticleCodec.Rfc2822DateNow()).Append("\r\n");
        sb.Append("Newsgroups: ").Append(_newsgroup).Append("\r\n");
        sb.Append("Subject: ").Append(ArticleCodec.SingleSubject($"{_repoId[..8]}.bin")).Append("\r\n");
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
