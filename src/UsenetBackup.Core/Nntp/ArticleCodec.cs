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
    /// Parses an article back into (chunk ID, blob). Verifies the yEnc
    /// size/CRC-32 trailer; throws <see cref="InvalidDataException"/> on
    /// any corruption.
    /// </summary>
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
