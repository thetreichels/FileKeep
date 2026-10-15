using System.Text;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Usenet article format for backup chunks (documented in FORMAT-v1.md,
/// v2 neutralizes the viewable naming: subjects, From, and attachment
/// names no longer advertise the software).
/// One chunk per article. The message-ID is deterministic —
/// &lt;chunkid.repoid@invalid&gt; — so uploads are idempotent,
/// existence checks need no index, and downloads need only the chunk ID.
/// </summary>
public static class ArticleCodec
{
    public const string FormatVersion = "v2";
    /// <summary>
    /// Neutral message-ID domain (.invalid is RFC 2606 reserved: guaranteed
    /// unresolvable, and advertises nothing). Was "usenet-backup" before the
    /// v2 neutralization; <see cref="LegacyMessageIdDomain"/> keeps old
    /// articles discoverable.
    /// </summary>
    private const string MessageIdDomain = "invalid";
    /// <summary>Message-ID domain used before neutralization. Still probed
    /// and parsed so articles posted by older versions remain reachable.</summary>
    public const string LegacyMessageIdDomain = "usenet-backup";

    /// <summary>
    /// Rewrites a generated message ID to its legacy-domain form, for
    /// probing/fetching articles posted before the domain was neutralized.
    /// </summary>
    public static string WithLegacyDomain(string messageId) =>
        messageId.EndsWith($"@{MessageIdDomain}>", StringComparison.Ordinal)
            ? string.Concat(messageId.AsSpan(0, messageId.Length - MessageIdDomain.Length - 1),
                LegacyMessageIdDomain, ">")
            : messageId;

    /// <summary>
    /// Neutral post-set name: first 8 alphanumeric chars of the backup ID,
    /// lowercased. Used for RAR-style part filenames (set.rar, set.r00, ...)
    /// and the NFO, so a backup's articles read as an ordinary multi-part
    /// binary post. Contains no software or backup branding.
    /// </summary>
    public static string SetName(string backupId)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupId);
        string clean = new string(backupId.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (clean.Length < 4)
            throw new ArgumentException("Backup ID must contain at least 4 alphanumeric chars.", nameof(backupId));
        return clean[..Math.Min(8, clean.Length)];
    }

    /// <summary>
    /// RAR-style part filename for a volume: index 0 is "set.rar",
    /// subsequent volumes are "set.r00", "set.r01", ...
    /// </summary>
    public static string RarPartName(string setName, int volumeIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(setName);
        if (volumeIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(volumeIndex));
        return volumeIndex == 0 ? $"{setName}.rar" : $"{setName}.r{volumeIndex - 1:00}";
    }

    /// <summary>Standard multi-part binary subject: "name" [i/n] - yEnc</summary>
    public static string PartSubject(string fileName, int partNumber, int partCount) =>
        $"\"{fileName}\" [{partNumber:000}/{partCount:000}] - yEnc";

    /// <summary>Single-article neutral subject: "name" - yEnc</summary>
    public static string SingleSubject(string fileName) => $"\"{fileName}\" - yEnc";

    /// <summary>
    /// Short repo identity: first 16 hex chars of SHA-256(repo salt).
    /// Deterministic and stable — no migration for existing repositories.
    /// </summary>
    public static string DeriveRepoId(byte[] salt) =>
        Hashing.Sha256Hex(salt)[..16];

    public static string MakeMessageId(string chunkIdHex, string repoId) =>
        $"<{chunkIdHex}.{repoId}@{MessageIdDomain}>";

    /// <summary>
    /// Message-ID for a retention-refresh republication.
    /// Servers reject duplicate message IDs, so refreshing retention requires
    /// a NEW identity. The timestamp + random suffix guarantees uniqueness
    /// while keeping the chunk ID recoverable from the ID itself.
    /// Format: &lt;chunkId.repoId.refresh.&lt;unixTime&gt;.&lt;nonce&gt;@invalid&gt;.
    /// </summary>
    public static string MakeRefreshMessageId(string chunkIdHex, string repoId)
    {
        long unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Guid.NewGuid().ToString("N")[..8];
        return $"<{chunkIdHex}.{repoId}.refresh.{unixTime}.{nonce}@{MessageIdDomain}>";
    }

    /// <summary>
    /// Message-ID for an encrypted manifest article:
    /// &lt;manifest.&lt;backup-id&gt;.&lt;repo-id&gt;@invalid&gt;.
    /// Backup IDs are 32-char GUIDs ("N" format). Lets the recovery wizard
    /// discover backups newer than the USB stick.
    /// </summary>
    public static string MakeManifestMessageId(string backupId, string repoId) =>
        $"<manifest.{backupId}.{repoId}@{MessageIdDomain}>";

    /// <summary>
    /// If <paramref name="messageId"/> is a manifest article for this repo,
    /// returns the backup ID; otherwise null.
    /// </summary>
    public static string? TryParseManifestMessageId(string messageId, string repoId)
    {
        // Accepts both the current and legacy domains (old articles).
        string prefix = $"<manifest.";
        foreach (string domain in new[] { MessageIdDomain, LegacyMessageIdDomain })
        {
            string suffix = $".{repoId}@{domain}>";
            if (!messageId.StartsWith(prefix, StringComparison.Ordinal) ||
                !messageId.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            string backupId = messageId[prefix.Length..^suffix.Length];
            return backupId.Length == 32 && backupId.All(Uri.IsHexDigit) ? backupId : null;
        }
        return null;
    }

    /// <summary>
    /// Message-ID for a versioned monthly manifest index:
    /// &lt;YYYY-MM.&lt;repo-id&gt;.index.&lt;version&gt;@invalid&gt;.
    /// Each index version supersedes the previous; the wizard STATs
    /// version 1, 2, 3... until 430 to find the latest. Articles are
    /// immutable on Usenet, so updates require new versions.
    /// </summary>
    public static string MakeManifestIndexMessageId(string repoId, string yearMonth, int version) =>
        $"<{yearMonth}.{repoId}.index.{version}@{MessageIdDomain}>";

    /// <summary>
    /// Message-ID for a versioned chunk-message-identity index. Each
    /// retention refresh (or upload) that changes message identities posts
    /// a new version; recovery probes versions to find the latest.
    /// Format: &lt;msgindex.&lt;repoId&gt;.&lt;version&gt;@invalid&gt;.
    /// </summary>
    public static string MakeMessageIndexMessageId(string repoId, int version) =>
        $"<msgindex.{repoId}.{version}@{MessageIdDomain}>";

    /// <summary>
    /// Parses a manifest index message-ID, returning (yearMonth, version)
    /// if it belongs to this repo; otherwise null.
    /// </summary>
    public static (string YearMonth, int Version)? TryParseManifestIndexMessageId(string messageId, string repoId)
    {
        // Format: <YYYY-MM.<repoid>.index.<version>@invalid> (or legacy @usenet-backup>)
        if (!messageId.StartsWith("<", StringComparison.Ordinal))
            return null;
        string? domain = null;
        foreach (string d in new[] { MessageIdDomain, LegacyMessageIdDomain })
        {
            if (messageId.EndsWith($"@{d}>", StringComparison.Ordinal)) { domain = d; break; }
        }
        if (domain is null)
            return null;
        string suffix = $".{repoId}.index.";
        string inner = messageId[1..^(domain.Length + 2)]; // strip < and @domain>
        int idx = inner.IndexOf(suffix, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        string yearMonth = inner[..idx];
        string versionStr = inner[(idx + suffix.Length)..];
        // Validate YYYY-MM format
        if (yearMonth.Length != 7 || yearMonth[4] != '-' ||
            !yearMonth[..4].All(char.IsDigit) || !yearMonth[5..].All(char.IsDigit))
            return null;
        if (!int.TryParse(versionStr, out int version) || version < 1)
            return null;
        return (yearMonth, version);
    }

    /// <summary>
    /// Formats a From header value as a valid RFC 5322 mailbox without
    /// advertising the software: a bare name becomes
    /// "name &lt;name@invalid&gt;" (.invalid is RFC 2606 reserved, guaranteed
    /// unresolvable and neutral). Values that already contain '@' are passed
    /// through unchanged — an explicitly configured address is the user's own.
    /// </summary>
    public static string FormatFrom(string from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return "poster <poster@invalid>";
        string clean = from.Trim();
        if (clean.Contains('@'))
            return clean;
        string safe = new string(clean.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        if (safe.Length == 0)
            safe = "poster";
        return $"{safe} <{safe.ToLowerInvariant()}@invalid>";
    }

    /// <summary>
    /// Current UTC time formatted for a Date: header (RFC 2822 / RFC 1123).
    /// Date is a required header (RFC 5322 §3.6.1); some spools file or
    /// expire dateless articles immediately.
    /// </summary>
    public static string Rfc2822DateNow() =>
        DateTimeOffset.UtcNow.ToString("r", System.Globalization.CultureInfo.InvariantCulture);

    public static string BuildArticle(
        string chunkIdHex, string repoId, byte[] blob, string newsgroup, string from)
    {
        return BuildArticleWithMessageId(
            chunkIdHex, MakeMessageId(chunkIdHex, repoId), blob, newsgroup, from);
    }

    /// <summary>
    /// Builds an article with an explicit message ID (for retention-refresh
    /// republications, which must use a NEW identity — servers reject
    /// duplicate message IDs, so reposting the same ID does not extend
    /// retention).
    /// </summary>
    public static string BuildArticleWithMessageId(
        string chunkIdHex, string messageId, byte[] blob, string newsgroup, string from)
    {
        if (chunkIdHex.Length != 64)
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));

        var sb = new StringBuilder();
        sb.Append("From: ").Append(FormatFrom(from)).Append("\r\n");
        sb.Append("Date: ").Append(Rfc2822DateNow()).Append("\r\n");
        sb.Append("Newsgroups: ").Append(newsgroup).Append("\r\n");
        sb.Append("Subject: ").Append(SingleSubject($"{chunkIdHex[..16].ToLowerInvariant()}.bin")).Append("\r\n");
        sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        sb.Append("X-UsenetBackup-Chunk: ").Append(chunkIdHex).Append("\r\n");
        sb.Append("X-UsenetBackup-Format: ").Append(FormatVersion).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(YEnc.Encode(blob, $"{chunkIdHex[..16].ToLowerInvariant()}.bin"));
        return sb.ToString();
    }

    /// <summary>
    /// Builds a manifest article. The blob must already be encrypted;
    /// this only frames it with headers and yEnc.
    /// </summary>
    public static string BuildManifestArticle(
        string backupId, string repoId, byte[] encryptedBlob, string newsgroup, string from)
    {
        string fileName = $"{SetName(backupId)}.dat";
        var sb = new StringBuilder();
        sb.Append("From: ").Append(FormatFrom(from)).Append("\r\n");
        sb.Append("Date: ").Append(Rfc2822DateNow()).Append("\r\n");
        sb.Append("Newsgroups: ").Append(newsgroup).Append("\r\n");
        sb.Append("Subject: ").Append(SingleSubject(fileName)).Append("\r\n");
        sb.Append("Message-ID: ").Append(MakeManifestMessageId(backupId, repoId)).Append("\r\n");
        sb.Append("X-UsenetBackup-Manifest: ").Append(backupId).Append("\r\n");
        sb.Append("X-UsenetBackup-Format: ").Append(FormatVersion).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(YEnc.Encode(encryptedBlob, fileName));
        return sb.ToString();
    }

    /// <summary>
    /// Parses a manifest article back into (backup ID, encrypted blob).
    /// Verifies yEnc framing; decryption is the caller's job (fails closed).
    /// </summary>
    public static (string BackupId, byte[] EncryptedBlob) ParseManifestArticle(string articleText)
    {
        int split = articleText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (split < 0)
        {
            split = articleText.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0)
                throw new InvalidDataException("Article has no header/body separator.");
        }

        var headers = ParseHeaders(articleText[..split]);
        if (!headers.TryGetValue("X-UsenetBackup-Manifest", out string? backupId) ||
            string.IsNullOrEmpty(backupId))
            throw new InvalidDataException("Article is not a usenet-backup manifest.");
        string body = articleText[(split + (articleText[split..].StartsWith("\r\n\r\n") ? 4 : 2))..];
        byte[] blob = YEnc.Decode(body);
        return (backupId, blob);
    }
    /// <summary>
    /// Message-ID for a packed volume article:
    /// &lt;volumeid.repoid@invalid&gt; — deliberately the same shape as
    /// chunk message-IDs, so the NZB parser, message index, and catalog
    /// journal work for volumes unchanged.
    /// </summary>
    public static string MakeVolumeMessageId(string volumeIdHex, string repoId) =>
        $"<{volumeIdHex}.{repoId}@{MessageIdDomain}>";

    /// <summary>
    /// Message-ID for a retention-refresh republication of a volume.
    /// Servers reject duplicate message IDs, so refreshing retention requires
    /// a NEW identity.
    /// Format: &lt;volumeId.repoId.refresh.&lt;unixTime&gt;.&lt;nonce&gt;@invalid&gt;.
    /// </summary>
    public static string MakeRefreshVolumeMessageId(string volumeIdHex, string repoId)
    {
        long unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Guid.NewGuid().ToString("N")[..8];
        return $"<{volumeIdHex}.{repoId}.refresh.{unixTime}.{nonce}@{MessageIdDomain}>";
    }

    public static string BuildVolumeArticle(
        string volumeIdHex, string repoId, byte[] volumeBytes, string newsgroup, string from,
        string setName, int partNumber, int partCount)
    {
        return BuildVolumeArticleWithMessageId(
            volumeIdHex, MakeVolumeMessageId(volumeIdHex, repoId), volumeBytes, newsgroup, from,
            setName, partNumber, partCount);
    }

    /// <summary>
    /// Builds a volume article with an explicit message ID (for retention-refresh
    /// republications, which must use a NEW identity).
    /// Viewable naming is neutral: RAR-style part filename and a standard
    /// multi-part subject. When set/part info is unavailable (refresh path),
    /// falls back to a neutral unnumbered subject.
    /// </summary>
    public static string BuildVolumeArticleWithMessageId(
        string volumeIdHex, string messageId, byte[] volumeBytes, string newsgroup, string from,
        string? setName = null, int partNumber = 0, int partCount = 0)
    {
        if (volumeIdHex.Length != 64)
            throw new ArgumentException("Volume ID must be 64 hex chars.", nameof(volumeIdHex));

        string fileName, subject;
        if (!string.IsNullOrEmpty(setName) && partNumber > 0 && partCount > 0)
        {
            // volumeIndex 0-based: part 2 of the set is volume index 0
            // (part 1 is the NFO)
            fileName = RarPartName(setName, partNumber - 2);
            subject = PartSubject(fileName, partNumber, partCount);
        }
        else
        {
            fileName = $"{volumeIdHex[..16].ToLowerInvariant()}.bin";
            subject = SingleSubject(fileName);
        }

        var sb = new StringBuilder();
        sb.Append("From: ").Append(FormatFrom(from)).Append("\r\n");
        sb.Append("Date: ").Append(Rfc2822DateNow()).Append("\r\n");
        sb.Append("Newsgroups: ").Append(newsgroup).Append("\r\n");
        sb.Append("Subject: ").Append(subject).Append("\r\n");
        sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        sb.Append("X-UsenetBackup-Volume: ").Append(volumeIdHex).Append("\r\n");
        sb.Append("X-UsenetBackup-Format: ").Append(FormatVersion).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(YEnc.Encode(volumeBytes, fileName));
        return sb.ToString();
    }

    /// <summary>
    /// Parses a volume article back into (volume ID, volume bytes).
    /// Verifies yEnc framing; unpacking and per-chunk verification are the
    /// caller's job (fails closed).
    /// </summary>
    public static (string VolumeId, byte[] VolumeBytes) ParseVolumeArticle(string articleText)
    {
        int split = articleText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (split < 0)
        {
            split = articleText.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0)
                throw new InvalidDataException("Article has no header/body separator.");
        }

        var headers = ParseHeaders(articleText[..split]);
        if (!headers.TryGetValue("X-UsenetBackup-Volume", out string? volumeId) ||
            volumeId.Length != 64 || !volumeId.All(Uri.IsHexDigit))
            throw new InvalidDataException("Article is missing a valid X-UsenetBackup-Volume header.");

        string body = articleText[(split + (articleText[split..].StartsWith("\r\n\r\n") ? 4 : 2))..];
        byte[] blob = YEnc.Decode(body);
        return (volumeId.ToLowerInvariant(), blob);
    }

    public static (string ChunkId, byte[] Blob) ParseArticle(string articleText)
    {
        int split = articleText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (split < 0)
        {
            split = articleText.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0)
                throw new InvalidDataException("Article has no header/body separator.");
        }

        var headers = ParseHeaders(articleText[..split]);
        if (!headers.TryGetValue("X-UsenetBackup-Chunk", out string? chunkId) ||
            chunkId.Length != 64 || !chunkId.All(Uri.IsHexDigit))
            throw new InvalidDataException("Article is missing a valid X-UsenetBackup-Chunk header.");

        string body = articleText[(split + (articleText[split..].StartsWith("\r\n\r\n") ? 4 : 2))..];
        byte[] blob = YEnc.Decode(body);
        return (chunkId.ToLowerInvariant(), blob);
    }

    /// <summary>
    /// Builds the plaintext NFO posted as part 1 of a backup's article set.
    /// Carries no software branding and no links — just the set metadata and
    /// the archival notice. Fully deterministic from its inputs.
    /// </summary>
    public static string BuildNfoText(string backupId, string dateIso, int volumeCount, string poster)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupId);
        ArgumentException.ThrowIfNullOrEmpty(dateIso);
        ArgumentException.ThrowIfNullOrEmpty(poster);
        if (volumeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(volumeCount));

        var sb = new StringBuilder();
        sb.AppendLine("============================================================");
        sb.AppendLine();
        sb.Append("  Backup ID : ").AppendLine(backupId.ToLowerInvariant());
        sb.Append("  Date      : ").AppendLine(dateIso);
        sb.Append("  Volumes   : ").AppendLine(volumeCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("  Poster    : ").AppendLine(poster);
        sb.AppendLine();
        sb.AppendLine("------------------------------------------------------------");
        sb.AppendLine("These articles contain private archival data generated");
        sb.AppendLine("under GPL-3.0-only. Their contents are encoded for");
        sb.AppendLine("authorized restoration only.");
        sb.Append("============================================================");
        return sb.ToString();
    }

    /// <summary>
    /// Message-ID for the NFO article. Derived from (backupId, repoId) via
    /// SHA-256 — deterministic and opaque, with no "nfo" label and no
    /// X- header on the article. The NFO is self-validating: the parser
    /// checks the body carries the expected backup ID.
    /// Format: &lt;64hex.repoid@invalid&gt;.
    /// </summary>
    public static string MakeNfoMessageId(string backupId, string repoId)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupId);
        ArgumentException.ThrowIfNullOrEmpty(repoId);
        string hash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes($"nfo:{backupId}:{repoId}"));
        return $"<{hash}.{repoId}@{MessageIdDomain}>";
    }

    /// <summary>
    /// Builds the NFO article: part 1 of the set ("set.nfo" [001/n]).
    /// Plaintext body, yEnc-framed like any binary post.
    /// </summary>
    public static string BuildNfoArticle(
        string nfoText, string backupId, string repoId,
        string newsgroup, string from, string setName, int totalParts)
    {
        ArgumentException.ThrowIfNullOrEmpty(nfoText);
        string fileName = $"{setName}.nfo";
        var sb = new StringBuilder();
        sb.Append("From: ").Append(FormatFrom(from)).Append("\r\n");
        sb.Append("Date: ").Append(Rfc2822DateNow()).Append("\r\n");
        sb.Append("Newsgroups: ").Append(newsgroup).Append("\r\n");
        sb.Append("Subject: ").Append(PartSubject(fileName, 1, totalParts)).Append("\r\n");
        sb.Append("Message-ID: ").Append(MakeNfoMessageId(backupId, repoId)).Append("\r\n");
        sb.Append("X-UsenetBackup-Format: ").Append(FormatVersion).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(YEnc.Encode(Encoding.UTF8.GetBytes(nfoText), fileName));
        return sb.ToString();
    }

    /// <summary>
    /// Parses an NFO article, returning its text. Fails closed unless the
    /// body carries the expected backup ID.
    /// </summary>
    public static string ParseNfoArticle(string articleText, string expectedBackupId)
    {
        int split = articleText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (split < 0)
        {
            split = articleText.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0)
                throw new InvalidDataException("Article has no header/body separator.");
        }
        string body = articleText[(split + (articleText[split..].StartsWith("\r\n\r\n") ? 4 : 2))..];
        byte[] raw = YEnc.Decode(body);
        string text = Encoding.UTF8.GetString(raw);
        if (!text.Contains(expectedBackupId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NFO article does not reference the expected backup ID.");
        return text;
    }

    /// <summary>
    /// Builds a size-probe article (random payload) used to verify a provider
    /// accepts articles of a given size before a real upload. Viewable naming
    /// is neutral: no software branding in From, Subject, filename, or
    /// message-ID. Returns the article text; the caller posts it.
    /// </summary>
    public static string BuildProbeArticle(byte[] payload, string newsgroup, string from, out string messageId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrEmpty(newsgroup);
        string nonce = Guid.NewGuid().ToString("N");
        messageId = $"<{nonce}@{MessageIdDomain}>";
        string fileName = $"{nonce}.bin";
        var sb = new StringBuilder();
        sb.Append("From: ").Append(FormatFrom(from)).Append("\r\n");
        sb.Append("Date: ").Append(Rfc2822DateNow()).Append("\r\n");
        sb.Append("Newsgroups: ").Append(newsgroup).Append("\r\n");
        sb.Append("Subject: ").Append(SingleSubject(fileName)).Append("\r\n");
        sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(YEnc.Encode(payload, fileName));
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseHeaders(string headerBlock)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (string rawLine in headerBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (rawLine.Length == 0)
                continue;
            if ((rawLine[0] == ' ' || rawLine[0] == '\t') && current is not null)
            {
                headers[current] += " " + rawLine.Trim(); // unfolded continuation
                continue;
            }
            int colon = rawLine.IndexOf(':');
            if (colon <= 0)
                continue;
            current = rawLine[..colon].Trim();
            headers[current] = rawLine[(colon + 1)..].Trim();
        }
        return headers;
    }
}
