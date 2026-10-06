using System.Text;
using System.Xml;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Generates NZB 1.1 indexes for a backup: one &lt;file&gt; per unique
/// chunk, one &lt;segment&gt; per file (v1 posts one article per chunk).
/// Chunk IDs are sorted, so the same manifest always yields byte-identical
/// NZB output. Segment byte counts are exact — computed by building the
/// article the uploader would post.
/// The returned text is UTF-8 (no BOM) and its XML declaration says so.
/// </summary>
public static class NzbGenerator
{
    public sealed record Options(
        string Newsgroup,
        string Poster,
        string RepoId,
        string GeneratorVersion = "v0.4");

    /// <param name="manifest">Backup manifest to index.</param>
    /// <param name="getBlob">Raw encrypted blob per chunk ID (for exact article sizes).</param>
    /// <param name="getUploadTimeUtc">Upload time per chunk ID, or null when unknown/not uploaded.</param>
    /// <param name="options">Newsgroup, poster name, repo ID.</param>
    public static string Generate(
        BackupManifest manifest,
        Func<string, byte[]> getBlob,
        Func<string, DateTime?> getUploadTimeUtc,
        Options options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(getBlob);
        ArgumentNullException.ThrowIfNull(getUploadTimeUtc);
        ArgumentNullException.ThrowIfNull(options);

        // MUST use ChunkOrdering for parity group alignment (see b1921c8)
        string[] chunkIds = ChunkOrdering.GetOrderedChunkIds(manifest);

        // Write through a MemoryStream so the declared encoding (UTF-8, no BOM)
        // matches the actual bytes; the CLI writes the text back as UTF-8.
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteDocType("nzb", "-//newzBin//DTD NZB 1.1//EN",
                "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd", null);
            writer.WriteStartElement("nzb", "http://www.newzbin.com/DTD/nzb/nzb-1.1");

            writer.WriteStartElement("head");
            WriteMeta(writer, "title", $"usenet-backup backup {manifest.BackupId}");
            WriteMeta(writer, "x-usenetbackup-generator", $"usenet-backup {options.GeneratorVersion}");
            WriteMeta(writer, "x-usenetbackup-backup-id", manifest.BackupId);
            WriteMeta(writer, "x-usenetbackup-root-sha256", manifest.RootSha256);
            WriteMeta(writer, "x-usenetbackup-chunk-count", chunkIds.Length.ToString());
            writer.WriteEndElement(); // head

            foreach (string chunkId in chunkIds)
            {
                byte[] blob = getBlob(chunkId);
                string article = ArticleCodec.BuildArticle(
                    chunkId, options.RepoId, blob, options.Newsgroup, options.Poster);
                long bytes = Encoding.Latin1.GetByteCount(article);
                string messageId = ArticleCodec.MakeMessageId(chunkId, options.RepoId);
                DateTime date = getUploadTimeUtc(chunkId) ?? manifest.CreatedUtc;
                if (date.Kind != DateTimeKind.Utc)
                    date = date.ToUniversalTime();
                long unixDate = new DateTimeOffset(date).ToUnixTimeSeconds();

                writer.WriteStartElement("file");
                writer.WriteAttributeString("poster", options.Poster);
                writer.WriteAttributeString("date", unixDate.ToString());
                writer.WriteAttributeString("subject", $"[usenet-backup] chunk {chunkId}");

                writer.WriteStartElement("groups");
                writer.WriteElementString("group", options.Newsgroup);
                writer.WriteEndElement(); // groups

                writer.WriteStartElement("segments");
                writer.WriteStartElement("segment");
                writer.WriteAttributeString("bytes", bytes.ToString());
                writer.WriteAttributeString("number", "1");
                writer.WriteString(messageId); // XmlWriter escapes the angle brackets
                writer.WriteEndElement(); // segment
                writer.WriteEndElement(); // segments

                writer.WriteEndElement(); // file
            }

            writer.WriteEndElement(); // nzb
            writer.WriteEndDocument();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteMeta(XmlWriter writer, string type, string value)
    {
        writer.WriteStartElement("meta");
        writer.WriteAttributeString("type", type);
        writer.WriteString(value);
        writer.WriteEndElement();
    }
}
