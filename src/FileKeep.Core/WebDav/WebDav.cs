using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace FileKeep.Core;

/// <summary>
/// Shared WebDAV helpers (D6 consolidation): the PROPFIND multistatus
/// builder, the Allow header, and Depth parsing, previously duplicated
/// between the Offline Folders handler
/// (<c>FileKeep.Service.OfflineFoldersDavHandler</c>) and the portable
/// reader handler (<c>FileKeep.Reader.ReaderDavHandler</c>).
///
/// The builder is the Offline Folders shape verbatim (DAV: +
/// urn:filekeep namespaces, displayname / resourcetype /
/// getcontentlength / getlastmodified / fk:readonly element order,
/// <c>HTTP/1.1 200 OK</c> status), with an optional <c>ReadOnly</c>
/// flag the reader never sets. What stays per-handler and is NOT
/// shared here: authentication / session-lock / open-token gates,
/// the DAV header (<c>"1"</c> vs <c>"1, 2"</c>), the ContentType
/// (<c>"application/xml; charset=utf-8"</c> + ContentLength vs
/// <c>"application/xml"</c> with neither), the LOCK XML, and the
/// MKCOL / unknown-method messages.
/// </summary>
public static class WebDav
{
    /// <summary>Byte-identical Allow header in both handlers.</summary>
    public const string AllowMethods = "OPTIONS, PROPFIND, GET, PUT, DELETE, LOCK, UNLOCK";

    /// <summary>Methods both handlers dispatch (MKCOL is refused by each, with its own message).</summary>
    public static readonly string[] SupportedMethods =
        { "OPTIONS", "PROPFIND", "GET", "PUT", "DELETE", "LOCK", "UNLOCK", "MKCOL" };

    /// <summary>Depth header value for infinity, as an int.</summary>
    public const int DepthInfinity = int.MaxValue;

    /// <summary>One PROPFIND response entry. The caller supplies the display name (the reader's root is "").</summary>
    public sealed record DavResource(
        string Href,
        string DisplayName,
        bool IsCollection,
        long? Size,
        DateTimeOffset? ModifiedUtc,
        bool ReadOnly = false);

    /// <summary>
    /// Parses a Depth header exactly as the old <c>Dashboard</c> switch did:
    /// <c>"0"</c> → 0, <c>"1"</c> → 1, <c>"infinity"</c> →
    /// <see cref="DepthInfinity"/>, anything else (including null) → 1.
    /// </summary>
    public static int ParseDepth(string? header) => header switch
    {
        "0" => 0,
        "1" => 1,
        "infinity" => DepthInfinity,
        _ => 1,
    };

    /// <summary>
    /// Builds the PROPFIND multistatus body (207) for the given resources,
    /// UTF-8 encoded. Verbatim the old OfflineFolders <c>Multistatus</c>
    /// body; <c>"R"</c> dates render identically with explicit
    /// <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    public static byte[] BuildMultistatus(IReadOnlyList<DavResource> nodes)
    {
        XNamespace d = "DAV:";
        XNamespace fk = "urn:filekeep";
        var root = new XElement(d + "multistatus");
        foreach (var node in nodes)
        {
            var response = new XElement(d + "response",
                new XElement(d + "href", node.Href),
                new XElement(d + "propstat",
                    new XElement(d + "prop",
                        new XElement(d + "displayname", node.DisplayName),
                        node.IsCollection
                            ? new XElement(d + "resourcetype", new XElement(d + "collection"))
                            : new XElement(d + "resourcetype"),
                        node.Size is not null
                            ? new XElement(d + "getcontentlength", node.Size.Value)
                            : null,
                        node.ModifiedUtc is not null
                            ? new XElement(d + "getlastmodified",
                                node.ModifiedUtc.Value.UtcDateTime.ToString("R", CultureInfo.InvariantCulture))
                            : null,
                        node.ReadOnly
                            ? new XElement(fk + "readonly", "true")
                            : null),
                    new XElement(d + "status", "HTTP/1.1 200 OK")));
            root.Add(response);
        }
        return Encoding.UTF8.GetBytes(new XDocument(root).ToString());
    }
}
