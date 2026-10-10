using System.Xml;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// One article reference inside an NZB file.
/// </summary>
public sealed record NzbSegment(string MessageId, long Bytes, int Number);

/// <summary>
/// One &lt;file&gt; in an NZB. Our generator emits one per chunk with a
/// single segment, but the parser tolerates multi-segment files.
/// </summary>
public sealed record NzbFile(
    string Subject,
    string Poster,
    DateTimeOffset Date,
    IReadOnlyList<NzbSegment> Segments)
{
    /// <summary>
    /// Chunk ID parsed from the first segment's usenet-backup message-ID,
    /// or null when the file doesn't reference our article format.
    /// </summary>
    public string? ChunkId => Segments.Count == 0
        ? null
        : NzbParser.ChunkIdFromMessageId(Segments[0].MessageId);

    /// <summary>
    /// Volume ID parsed from the first segment's usenet-backup message-ID,
    /// or null when the file doesn't reference our article format.
    /// Only meaningful for volume NZBs (<see cref="NzbDocument.IsVolumeNzb"/>);
    /// the message-ID shape is identical to chunk articles.
    /// </summary>
    public string? VolumeId => Segments.Count == 0
        ? null
        : NzbParser.ChunkIdFromMessageId(Segments[0].MessageId);
}

/// <summary>
/// A parsed NZB 1.1 document: head metadata plus the file/segment list.
/// </summary>
public sealed record NzbDocument(
    string? BackupId,
    string? RootSha256,
    string? Generator,
    IReadOnlyList<NzbFile> Files,
    bool IsVolumeNzb = false);

/// <summary>
/// Parses NZB 1.1 indexes back into chunk/segment references (the inverse
/// of <see cref="NzbGenerator"/>). Unknown elements are ignored for
/// forward compatibility; structurally invalid files throw
/// <see cref="InvalidDataException"/>.
/// </summary>
public static class NzbParser
{
    public static NzbDocument ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllText(path));
    }

    public static NzbDocument Parse(string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(xml);

        string? backupId = null, rootSha256 = null, generator = null;
        int volumeCount = 0;
        var files = new List<NzbFile>();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, // never fetch the external DTD
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);

        string? filePoster = null, fileSubject = null;
        DateTimeOffset fileDate = DateTimeOffset.UnixEpoch;
        var segments = new List<NzbSegment>();
        bool inFile = false, inSegments = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "meta":
                        string? type = reader.GetAttribute("type");
                        string value = reader.ReadElementContentAsString().Trim();
                        if (type == "x-usenetbackup-backup-id") backupId = value;
                        else if (type == "x-usenetbackup-root-sha256") rootSha256 = value;
                        else if (type == "x-usenetbackup-generator") generator = value;
                        else if (type == "x-usenetbackup-volume-count" &&
                            int.TryParse(value, out int vc)) volumeCount = vc;
                        break;

                    case "file":
                        filePoster = reader.GetAttribute("poster") ?? "";
                        fileSubject = reader.GetAttribute("subject") ?? "";
                        fileDate = ParseUnixDate(reader.GetAttribute("date"));
                        segments = new List<NzbSegment>();
                        inFile = true;
                        break;

                    case "group":
                        if (inFile && !inSegments)
                            reader.Skip(); // newsgroup not needed: message-IDs are sufficient
                        break;

                    case "segments":
                        inSegments = true;
                        break;

                    case "segment":
                        if (!inFile || !inSegments)
                            throw new InvalidDataException("<segment> outside of <file>/<segments>.");
                        long bytes = long.TryParse(reader.GetAttribute("bytes"), out long b) ? b : 0;
                        int number = int.TryParse(reader.GetAttribute("number"), out int n) ? n : 0;
                        string messageId = reader.ReadElementContentAsString().Trim();
                        if (messageId.Length == 0)
                            throw new InvalidDataException("NZB <segment> has an empty message-ID.");
                        segments.Add(new NzbSegment(messageId, bytes, number));
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "segments":
                        inSegments = false;
                        break;
                    case "file":
                        if (segments.Count == 0)
                            throw new InvalidDataException(
                                $"NZB <file> '{fileSubject}' contains no segments.");
                        files.Add(new NzbFile(fileSubject ?? "", filePoster ?? "", fileDate, segments));
                        inFile = false;
                        break;
                }
            }
        }

        return new NzbDocument(backupId, rootSha256, generator, files, IsVolumeNzb: volumeCount > 0);
    }

    private static DateTimeOffset ParseUnixDate(string? value) =>
        long.TryParse(value, out long seconds) && seconds >= 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Extracts the chunk ID from a usenet-backup deterministic message-ID
    /// (&lt;{64 hex}.{16 hex}@usenet-backup&gt;). Returns null for anything else.
    /// </summary>
    internal static string? ChunkIdFromMessageId(string messageId)
    {
        const string domain = "@usenet-backup>";
        // '<' + 64 hex + '.' + 16 hex + "@usenet-backup>"
        if (messageId.Length != 1 + 64 + 1 + 16 + domain.Length)
            return null;
        if (messageId[0] != '<' || messageId[65] != '.' ||
            !messageId.EndsWith(domain, StringComparison.Ordinal))
            return null;
        string chunkId = messageId.Substring(1, 64);
        string repoId = messageId.Substring(66, 16);
        if (!chunkId.All(Uri.IsHexDigit) || !repoId.All(Uri.IsHexDigit))
            return null;
        return chunkId.ToLowerInvariant();
    }
}
