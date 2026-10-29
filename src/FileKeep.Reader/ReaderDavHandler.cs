using System.Net;
using System.Text;
using System.Xml.Linq;
using FileKeep.Core;

namespace FileKeep.Reader;

/// <summary>
/// The reader's own loopback WebDAV server (Stealth Folders slice 3,
/// part 3), ported from the main app's custom handler — no
/// third-party DAV library, no temp files, no host writes except
/// the host app's own copies. It serves the decrypted manifest
/// from memory: files stream chunk-by-chunk from the stick-first
/// composite store, and PUT-back writes new chunks to the stick
/// and re-encrypts the stick package (the original location and
/// the main catalog are untouched; DELETE orphans stick chunks,
/// like forget).
///
/// Supported: OPTIONS, PROPFIND (Depth 0/1/infinity), GET, PUT
/// (create-or-replace), DELETE (files only), LOCK, UNLOCK.
/// MKCOL is refused (405): folders are manifest entries, created
/// by the manifest, not by the share. A file with any chunk
/// missing from both sources is reported 404 per file — never
/// streamed garbled or partial. Locking the reader session aborts
/// in-flight streams and drops all lock tokens.
/// </summary>
public sealed class ReaderDavHandler
{
    /// <summary>One DAV request, already stripped of the DAV root prefix.</summary>
    public sealed class Request
    {
        public required string Method { get; init; }
        /// <summary>URL-decoded path segments below the DAV root.</summary>
        public required IReadOnlyList<string> Segments { get; init; }
        public string Depth { get; init; } = "1";
        public byte[] Body { get; init; } = Array.Empty<byte>();
        public string? LockToken { get; init; }
    }

    /// <summary>One DAV response. Exactly one of Body / StreamBody is set.</summary>
    public sealed class Response
    {
        public required int Status { get; init; }
        public string? ContentType { get; init; }
        public byte[]? Body { get; init; }
        public Func<Stream, Task>? StreamBody { get; init; }
        public Dictionary<string, string> Headers { get; } = new();
    }

    private static readonly XNamespace D = "DAV:";
    private readonly ReaderSession _session;
    private readonly Dictionary<string, string> _locks = new(StringComparer.Ordinal); // token -> path

    public ReaderDavHandler(ReaderSession session) => _session = session;

    public void DropLocks() => _locks.Clear();

    public Response Handle(Request request)
    {
        if (!_session.IsUnlocked)
            return Text(423, "The reader session is locked.");
        string path = string.Join("/", request.Segments);
        return request.Method.ToUpperInvariant() switch
        {
            "OPTIONS" => Options(),
            "PROPFIND" => Propfind(path, request.Depth),
            "GET" => Get(path),
            "PUT" => Put(path, request.Body, request.LockToken),
            "DELETE" => Delete(path, request.LockToken),
            "LOCK" => Lock(path),
            "UNLOCK" => Unlock(request.LockToken),
            "MKCOL" => Text(405, "Folders are manifest entries, not created via the share."),
            _ => Text(405, $"Method {request.Method} is not supported."),
        };
    }

    private static Response Text(int status, string message) =>
        new() { Status = status, ContentType = "text/plain", Body = Encoding.UTF8.GetBytes(message) };

    private static Response Options()
    {
        var r = new Response { Status = 200 };
        r.Headers["DAV"] = "1, 2";
        r.Headers["Allow"] = "OPTIONS, PROPFIND, GET, PUT, DELETE, LOCK, UNLOCK";
        return r;
    }

    private Response Get(string path)
    {
        var file = _session.Find(path);
        if (file is null)
            return Text(404, string.IsNullOrEmpty(path) ? "A folder cannot be downloaded." : "No such file.");
        var missing = file.ChunkIds.Where(c => !_session.Store.Exists(c)).ToList();
        if (missing.Count > 0)
            return Text(404,
                $"File '{path}' is incomplete: {missing.Count} of {file.ChunkIds.Count} chunks are missing " +
                "from the stick and the original location. Nothing was streamed.");
        return new Response
        {
            Status = 200,
            ContentType = "application/octet-stream",
            StreamBody = stream =>
            {
                OfflineChunkStore.Retrieve(file.ChunkIds, _session.Store,
                    _session.ContentKey, stream, () => _session.IsUnlocked);
                return Task.CompletedTask;
            },
        };
    }

    private Response Put(string path, byte[] body, string? lockToken)
    {
        if (string.IsNullOrEmpty(path))
            return Text(405, "A folder cannot be uploaded.");
        if (LockedByOther(path, lockToken))
            return Text(423, "The file is locked by another token.");
        bool existed = _session.Find(path) is not null;
        using var input = new MemoryStream(body);
        var (chunkIds, total) = OfflineChunkStore.Store(input, _session.StickStore, _session.ContentKey);
        var entry = new ReaderSession.Entry
        {
            Path = path,
            Size = total,
            ModifiedUtc = DateTimeOffset.UtcNow,
            ChunkIds = chunkIds,
        };
        _session.Files.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        _session.Files.Add(entry);
        _session.SavePackage();
        return Text(existed ? 204 : 201, existed ? "Replaced." : "Created.");
    }

    private Response Delete(string path, string? lockToken)
    {
        if (string.IsNullOrEmpty(path))
            return Text(405, "Folders cannot be deleted via the share.");
        var file = _session.Find(path);
        if (file is null)
            return Text(404, "No such file.");
        if (LockedByOther(path, lockToken))
            return Text(423, "The file is locked by another token.");
        _session.Files.Remove(file);
        // Chunks stay on the stick, orphaned — like forget.
        _session.SavePackage();
        return Text(204, "Deleted.");
    }

    private Response Lock(string path)
    {
        var file = _session.Find(path);
        if (file is null)
            return Text(404, "No such file.");
        if (_locks.Values.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            return Text(423, "The file is already locked.");
        string token = "opaquelocktoken:" + Guid.NewGuid().ToString("D");
        _locks[token] = path;
        var doc = new XDocument(
            new XElement(D + "prop",
                new XElement(D + "lockdiscovery",
                    new XElement(D + "activelock",
                        new XElement(D + "locktoken", new XElement(D + "href", token)),
                        new XElement(D + "lockroot", new XElement(D + "href", Href(path)))))));
        var r = new Response { Status = 200, ContentType = "application/xml", Body = Encoding.UTF8.GetBytes(doc.ToString()) };
        r.Headers["Lock-Token"] = token;
        return r;
    }

    private Response Unlock(string? lockToken)
    {
        if (string.IsNullOrEmpty(lockToken) || !_locks.Remove(lockToken))
            return Text(409, "Unknown lock token.");
        return Text(204, "Unlocked.");
    }

    private bool LockedByOther(string path, string? presented)
    {
        var holder = _locks.FirstOrDefault(kv =>
            string.Equals(kv.Value, path, StringComparison.OrdinalIgnoreCase));
        return holder.Key is not null && !string.Equals(holder.Key, presented, StringComparison.Ordinal);
    }

    private Response Propfind(string path, string depth)
    {
        // Resolve the target: root, a file, or an intermediate collection.
        bool isRoot = string.IsNullOrEmpty(path);
        var file = isRoot ? null : _session.Find(path);
        bool isCollection = isRoot || file is null;
        if (!isRoot && file is null && !_session.Files.Any(f =>
                f.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
            return Text(404, "No such file or folder.");

        var resources = new List<(string Path, bool IsCollection, ReaderSession.Entry? File)>();
        resources.Add((path, isCollection, file));
        if (isCollection && depth != "0")
        {
            bool recursive = depth == "infinity";
            foreach (var f in _session.Files)
            {
                if (!isRoot && !f.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                    continue;
                string rel = isRoot ? f.Path : f.Path[(path.Length + 1)..];
                if (!recursive && rel.Contains('/'))
                {
                    // Depth 1: advertise the intermediate collection once.
                    string dir = isRoot ? rel[..rel.IndexOf('/')] : path + "/" + rel[..rel.IndexOf('/')];
                    if (!resources.Any(r => r.Path == dir))
                        resources.Add((dir, true, null));
                    continue;
                }
                if (recursive)
                {
                    // Advertise every intermediate collection on the way down.
                    string prefix = isRoot ? "" : path + "/";
                    string walk = rel;
                    while (walk.Contains('/'))
                    {
                        string dir = prefix + walk[..walk.IndexOf('/')];
                        if (!resources.Any(r => r.Path == dir))
                            resources.Add((dir, true, null));
                        walk = walk[(walk.IndexOf('/') + 1)..];
                    }
                }
                resources.Add((f.Path, false, f));
            }
        }
        var multi = new XElement(D + "multistatus",
            resources.Select(r =>
            {
                var prop = new XElement(D + "prop",
                    new XElement(D + "displayname", r.Path.Split('/').Last()),
                    new XElement(D + "resourcetype",
                        r.IsCollection ? new XElement(D + "collection") : null));
                if (r.File is not null)
                {
                    prop.Add(new XElement(D + "getcontentlength", r.File.Size));
                    prop.Add(new XElement(D + "getlastmodified",
                        r.File.ModifiedUtc.UtcDateTime.ToString("R",
                            System.Globalization.CultureInfo.InvariantCulture)));
                }
                return new XElement(D + "response",
                    new XElement(D + "href", Href(r.Path)),
                    new XElement(D + "propstat",
                        prop,
                        new XElement(D + "status", "HTTP/1.1 200 OK")));
            }));
        var doc = new XDocument(multi);
        return new Response
        {
            Status = 207,
            ContentType = "application/xml",
            Body = Encoding.UTF8.GetBytes(doc.ToString()),
        };
    }

    private static string Href(string path) =>
        "/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
}
