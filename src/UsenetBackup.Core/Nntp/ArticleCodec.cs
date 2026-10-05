using System.Text;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Usenet article format for backup chunks (documented in FORMAT-v1.md).
/// One chunk per article. The message-ID is deterministic —
/// &lt;chunkid.repoid@usenet-backup&gt; — so uploads are idempotent,
/// existence checks need no index, and downloads need only the chunk ID.
/// </summary>
public static class ArticleCodec
{
    public const string FormatVersion = "v1";
    private const string MessageIdDomain = "usenet-backup";

    /// <summary>
    /// Short repo identity: first 16 hex chars of SHA-256(repo salt).
    /// Deterministic and stable — no migration for existing repositories.
    /// </summary>
    public static string DeriveRepoId(byte[] salt) =>
        Hashing.Sha256Hex(salt)[..16];

    public static string MakeMessageId(string chunkIdHex, string repoId) =>
        $"<{chunkIdHex}.{repoId}@{MessageIdDomain}>";

    /// <summary>
    /// Message-ID for an encrypted manifest article:
    /// &lt;manifest.&lt;backup-id&gt;.&lt;repo-id&gt;@usenet-backup&gt;.
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
        string prefix = $"<manifest.";
        string suffix = $".{repoId}@{MessageIdDomain}>";
        if (!messageId.StartsWith(prefix, StringComparison.Ordinal) ||
            !messageId.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        string backupId = messageId[prefix.Length..^suffix.Length];
        return backupId.Length == 32 && backupId.All(Uri.IsHexDigit) ? backupId : null;
    }

    /// <summary>
    /// Message-ID for a versioned monthly manifest index:
    /// &lt;YYYY-MM.&lt;repo-id&gt;.index.&lt;version&gt;@usenet-backup&gt;.
    /// Each index version supersedes the previous; the wizard STATs
    /// version 1, 2, 3... until 430 to find the latest. Articles are
    /// immutable on Usenet, so updates require new versions.
    /// </summary>
    public static string MakeManifestIndexMessageId(string repoId, string yearMonth, int version) =>
        $"<{yearMonth}.{repoId}.index.{version}@{MessageIdDomain}>";

    /// <summary>
    /// Parses a manifest index message-ID, returning (yearMonth, version)
    /// if it belongs to this repo; otherwise null.
    /// </summary>
    public static (string YearMonth, int Version)? TryParseManifestIndexMessageId(string messageId, string repoId)
    {
        // Format: <YYYY-MM.<repoid>.index.<version>@usenet-backup>
        string suffix = $".{repoId}.index.";
        string domain = $"@{MessageIdDomain}>";
        if (!messageId.StartsWith("<", StringComparison.Ordinal) ||
            !messageId.EndsWith(domain, StringComparison.Ordinal))
            return null;
        string inner = messageId[1..^domain.Length]; // strip < and @usenet-backup>
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

    public static string BuildArticle(
        string chunkIdHex, string repoId, byte[] blob, string newsgroup, string from)
    {
        if (chunkIdHex.Length != 64)
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));

        var sb = new StringBuilder();
        sb.Append("From: ").Append(from).Append("\r\n");
        sb.Append("Newsgroups: ").Append(newsgroup).Append("\r\n");
        sb.Append("Subject: [usenet-backup] chunk ").Append(chunkIdHex).Append("\r\n");
        sb.Append("Message-ID: ").Append(MakeMessageId(chunkIdHex, repoId)).Append("\r\n");
        sb.Append("X-UsenetBackup-Chunk: ").Append(chunkIdHex).Append("\r\n");
        sb.Append("X-UsenetBackup-Format: ").Append(FormatVersion).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(YEnc.Encode(blob, $"chunk-{chunkIdHex[..16]}.bin"));
        return sb.ToString();
    }

    /// <summary>
    /// Builds a manifest article. The blob must already be encrypted;
    /// this only frames it with headers and yEnc.
    /// </summary>
    public static string BuildManifestArticle(
        string backupId, string repoId, byte[] encryptedBlob, string newsgroup, string from)
    {
        var sb = new StringBuilder();
        sb.Append("From: ").Append(from).Append("\r\n");
        sb.Append("Newsgroups: ").Append(newsgroup).Append("\r\n");
        sb.Append("Subject: [usenet-backup] manifest ").Append(backupId).Append("\r\n");
        sb.Append("Message-ID: ").Append(MakeManifestMessageId(backupId, repoId)).Append("\r\n");
        sb.Append("X-UsenetBackup-Manifest: ").Append(backupId).Append("\r\n");
        sb.Append("X-UsenetBackup-Format: ").Append(FormatVersion).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(YEnc.Encode(encryptedBlob, $"manifest-{backupId}.bin"));
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
        byte[] blob = YEnc.Decode(articleText[(split + 4)..]);
        return (backupId, blob);
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
