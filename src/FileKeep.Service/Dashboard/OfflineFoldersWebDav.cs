using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FileKeep.Core.Service;

namespace FileKeep.Service;

/// <summary>
/// WebDAV presentation for Offline Folders (slice 2), deliberately
/// framework-free: this handler speaks in plain requests and results
/// so it is fully testable without an ASP.NET host, and
/// <c>Dashboard.RunAsync</c> adapts it to Kestrel with a thin mapping.
/// The share is loopback-only (it lives on the dashboard host) and
/// gated by the card session: every call first checks
/// <see cref="OfflineFoldersSession.IsUnlocked"/> and, when locked,
/// refuses and drops all lock tokens — the share is gone the moment
/// the card locks, including for in-flight transfers (GET streams
/// check the session per chunk and abort).
///
/// Memory discipline: GET streams decrypted chunks straight to the
/// output with per-chunk zeroing; PUT streams the request body
/// straight into the chunker. No plaintext file is ever written by
/// this handler — not a temp file, not a cache, not a memory-mapped
/// file. The dashboard page itself cannot launch Explorer, so the
/// card's double-click copies the file's share URL for Explorer; the
/// share below is the real open path.
///
/// Supported methods: OPTIONS, PROPFIND (Depth 0/1/infinity),
/// GET, PUT (create-or-replace), DELETE (files only), LOCK, UNLOCK.
/// MKCOL is refused (folders are catalog entries, created via the
/// card API, not over WebDAV).
/// </summary>
public sealed class OfflineFoldersDavHandler
{
    public sealed class DavRequest
    {
        public required string Method { get; init; }
        /// <summary>Decoded path segments after /dav/offline (empty = root).</summary>
        public required string[] Segments { get; init; }
        /// <summary>0, 1, or int.MaxValue for infinity.</summary>
        public int Depth { get; init; } = 1;
        public Stream? Body { get; init; }
        /// <summary>Lock token from Lock-Token or If headers, if any.</summary>
        public string? LockToken { get; init; }
    }

    public sealed class DavResult
    {
        public required int Status { get; init; }
        public string? ContentType { get; init; }
        public long? ContentLength { get; init; }
        public Dictionary<string, string> Headers { get; } = new();
        public byte[]? BodyBytes { get; init; }
        /// <summary>Streaming body writer (GET). May throw mid-stream on lock.</summary>
        public Func<Stream, Task>? BodyWriter { get; set; }
    }

    /// <summary>Thrown when a streaming body fails after headers were sent.</summary>
    public sealed class DavStreamException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    private sealed record DavNode(string Href, string DisplayName, bool IsCollection, long? Size, DateTimeOffset? ModifiedUtc);

    private readonly ServiceConfig _config;
    private readonly OfflineFoldersSession _session;
    private readonly string _catalogPath;
    private readonly Action<string>? _log;
    private readonly object _locksGuard = new();
    private readonly Dictionary<string, string> _locks = new(StringComparer.Ordinal);

    public OfflineFoldersDavHandler(
        ServiceConfig config, OfflineFoldersSession session, string catalogPath,
        Action<string>? log = null)
    {
        _config = config;
        _session = session;
        _catalogPath = catalogPath;
        _log = log;
    }

    public DavResult Handle(DavRequest request)
    {
        // The share exists only while the card is unlocked. Locking
        // drops every lock token with it.
        if (!_session.IsUnlocked)
        {
            lock (_locksGuard) { _locks.Clear(); }
            return Error(403, "Offline Folders is locked.");
        }

        return request.Method.ToUpperInvariant() switch
        {
            "OPTIONS" => Options(),
            "PROPFIND" => Propfind(request),
            "GET" => Get(request),
            "PUT" => Put(request),
            "DELETE" => Delete(request),
            "LOCK" => Lock(request),
            "UNLOCK" => Unlock(request),
            "MKCOL" => Error(405, "Folders are created in the Offline Folders card, not over WebDAV."),
            _ => Error(405, "Method not supported."),
        };
    }

    private static DavResult Options()
    {
        var result = new DavResult { Status = 200, ContentLength = 0 };
        result.Headers["DAV"] = "1";
        result.Headers["Allow"] = "OPTIONS, PROPFIND, GET, PUT, DELETE, LOCK, UNLOCK";
        return result;
    }

    private static DavResult Error(int status, string message) =>
        new() { Status = status, ContentType = "text/plain", BodyBytes = Encoding.UTF8.GetBytes(message) };

    private List<OfflineFolderEntry>? Entries() => _session.GetEntries();

    private static string LockKey(string folder, string path) =>
        folder.ToLowerInvariant() + "\n" + path.ToLowerInvariant();

    private string? LockHeldBy(string folder, string path)
    {
        lock (_locksGuard)
        {
            foreach (var (token, target) in _locks)
            {
                if (string.Equals(target, LockKey(folder, path), StringComparison.Ordinal))
                    return token;
            }
            return null;
        }
    }

    private DavResult? CheckLock(string folder, string path, string? presentedToken)
    {
        string? holder = LockHeldBy(folder, path);
        if (holder is null) return null;
        if (presentedToken is not null && string.Equals(presentedToken, holder, StringComparison.Ordinal))
            return null;
        return Error(423, "The file is locked.");
    }

    private DavResult Propfind(DavRequest request)
    {
        var entries = Entries();
        if (entries is null) return Error(403, "Offline Folders is locked.");
        var nodes = new List<DavNode>();
        if (request.Segments.Length == 0)
        {
            nodes.Add(new DavNode("/dav/offline/", "Offline Folders", true, null, null));
            if (request.Depth >= 1)
            {
                foreach (var entry in entries)
                    nodes.Add(new DavNode(
                        "/dav/offline/" + Uri.EscapeDataString(entry.Name) + "/",
                        entry.Name, true, null, entry.AddedUtc));
            }
            return Multistatus(nodes);
        }

        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, request.Segments[0], StringComparison.OrdinalIgnoreCase));
        if (folder is null) return Error(404, "No such offline folder.");
        string[] rest = request.Segments[1..];
        if (rest.Length == 0)
        {
            string href = "/dav/offline/" + Uri.EscapeDataString(folder.Name) + "/";
            nodes.Add(new DavNode(href, folder.Name, true, null, folder.AddedUtc));
            if (request.Depth >= 1)
                nodes.AddRange(ChildrenOf(folder, href, "", request.Depth == int.MaxValue));
            return Multistatus(nodes);
        }

        string relPath = string.Join("/", rest);
        var file = folder.Files.FirstOrDefault(f =>
            string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase));
        if (file is not null)
        {
            nodes.Add(FileNode(folder, file));
            return Multistatus(nodes);
        }
        // Intermediate collection? It exists when some file lives under it.
        string prefix = relPath + "/";
        if (folder.Files.Any(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            string href = "/dav/offline/" + Uri.EscapeDataString(folder.Name) + "/" +
                string.Join("/", rest.Select(Uri.EscapeDataString)) + "/";
            nodes.Add(new DavNode(href, rest[^1], true, null, null));
            if (request.Depth >= 1)
                nodes.AddRange(ChildrenOf(folder, href, prefix, request.Depth == int.MaxValue));
            return Multistatus(nodes);
        }
        return Error(404, "No such offline file.");
    }

    private IEnumerable<DavNode> ChildrenOf(
        OfflineFolderEntry folder, string baseHref, string prefix, bool recursive)
    {
        var dirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<DavNode>();
        foreach (var file in folder.Files)
        {
            if (!file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string rel = file.Path[prefix.Length..];
            int slash = rel.IndexOf('/');
            if (slash < 0)
            {
                nodes.Add(FileNode(folder, file));
            }
            else
            {
                string dir = rel[..slash];
                if (dirs.Add(dir))
                {
                    nodes.Add(new DavNode(
                        baseHref + Uri.EscapeDataString(dir) + "/",
                        dir, true, null, null));
                }
            }
        }
        if (recursive)
        {
            // Depth infinity: descend one more level per directory by
            // re-listing with each directory as the prefix.
            var extra = new List<DavNode>();
            foreach (string dir in dirs)
                extra.AddRange(ChildrenOf(folder, baseHref + Uri.EscapeDataString(dir) + "/", prefix + dir + "/", recursive: true));
            nodes.AddRange(extra);
        }
        return nodes;
    }

    private DavNode FileNode(OfflineFolderEntry folder, OfflineFileEntry file)
    {
        string href = "/dav/offline/" + Uri.EscapeDataString(folder.Name) + "/" +
            string.Join("/", file.Path.Split('/').Select(Uri.EscapeDataString));
        string display = file.Path.Split('/')[^1];
        return new DavNode(href, display, false, file.Size, file.ModifiedUtc);
    }

    private static DavResult Multistatus(List<DavNode> nodes)
    {
        XNamespace d = "DAV:";
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
                                node.ModifiedUtc.Value.UtcDateTime.ToString("R"))
                            : null),
                    new XElement(d + "status", "HTTP/1.1 200 OK")));
            root.Add(response);
        }
        byte[] body = Encoding.UTF8.GetBytes(new XDocument(root).ToString());
        return new DavResult
        {
            Status = 207,
            ContentType = "application/xml; charset=utf-8",
            ContentLength = body.Length,
            BodyBytes = body,
        };
    }

    private DavResult Get(DavRequest request)
    {
        var resolved = ResolveFile(request);
        if (resolved is null) return Error(404, "No such offline file.");
        var (_, file, location) = resolved.Value;
        // Pre-check: refuse before headers when anything is already wrong.
        if (_session.CopyContentKey() is not { } keyCheck)
            return Error(403, "Offline Folders is locked.");
        CryptographicOperations.ZeroMemory(keyCheck);
        var result = new DavResult
        {
            Status = 200,
            ContentType = "application/octet-stream",
            ContentLength = file.Size,
        };
        result.BodyWriter = async (output) =>
        {
            byte[]? contentKey = _session.CopyContentKey();
            if (contentKey is null)
                throw new DavStreamException(403, "Offline Folders is locked.");
            try
            {
                OfflineFileStore.OfflineLocationStore store;
                try { store = OfflineFileStore.ResolveStore(location); }
                catch (InvalidOperationException ex) { throw new DavStreamException(400, ex.Message); }
                using (store)
                {
                    // Stream chunk by chunk; abort mid-stream on lock.
                    await Task.Run(() =>
                        OfflineFileStore.Retrieve(file.ChunkIds, store.Store, contentKey, output,
                            () => _session.IsUnlocked));
                }
            }
            catch (DavStreamException) { throw; }
            catch (OperationCanceledException) { throw new DavStreamException(403, "Offline Folders was locked during transfer."); }
            catch (InvalidDataException ex) { throw new DavStreamException(400, ex.Message); }
            catch (CryptographicException) { throw new DavStreamException(400, "The file's stored content failed verification."); }
            finally
            {
                if (contentKey is not null)
                    CryptographicOperations.ZeroMemory(contentKey);
            }
        };
        return result;
    }

    private DavResult Put(DavRequest request)
    {
        if (request.Segments.Length < 2)
            return Error(400, "A file path is required.");
        var entries = Entries();
        if (entries is null) return Error(403, "Offline Folders is locked.");
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, request.Segments[0], StringComparison.OrdinalIgnoreCase));
        if (folder is null) return Error(404, "No such offline folder.");
        string relPath = string.Join("/", request.Segments[1..]);
        string? pathError = OfflineFoldersCatalog.ValidateFilePath(relPath);
        if (pathError is not null) return Error(400, pathError);
        var locked = CheckLock(folder.Name, relPath, request.LockToken);
        if (locked is not null) return locked;
        if (request.Body is null) return Error(400, "No file content was sent.");
        bool existed = folder.Files.Any(f =>
            string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase));
        // StoreOfflineFile re-resolves the folder and persists the
        // catalog atomically; a failure there leaves the old index
        // untouched (new chunks, if any, are orphans).
        var (status, payload) = DashboardApi.StoreOfflineFile(
            _config, _session, _catalogPath, folder.Name, relPath,
            request.Body, DateTimeOffset.UtcNow, _log);
        if (status != 200)
            return Error(status, ErrorMessage(payload));
        return new DavResult { Status = existed ? 204 : 201, ContentLength = 0 };
    }

    private DavResult Delete(DavRequest request)
    {
        if (request.Segments.Length < 2)
            return Error(400, "A file path is required.");
        var entries = Entries();
        if (entries is null) return Error(403, "Offline Folders is locked.");
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, request.Segments[0], StringComparison.OrdinalIgnoreCase));
        if (folder is null) return Error(404, "No such offline folder.");
        string relPath = string.Join("/", request.Segments[1..]);
        var locked = CheckLock(folder.Name, relPath, request.LockToken);
        if (locked is not null) return locked;
        var (status, payload) = DashboardApi.DeleteOfflineFile(
            _session, _catalogPath, folder.Name, relPath, _log);
        if (status != 200)
            return Error(status, ErrorMessage(payload));
        return new DavResult { Status = 204, ContentLength = 0 };
    }

    private DavResult Lock(DavRequest request)
    {
        if (request.Segments.Length < 2)
            return Error(400, "A file path is required.");
        var entries = Entries();
        if (entries is null) return Error(403, "Offline Folders is locked.");
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, request.Segments[0], StringComparison.OrdinalIgnoreCase));
        if (folder is null) return Error(404, "No such offline folder.");
        string relPath = string.Join("/", request.Segments[1..]);
        bool exists = folder.Files.Any(f =>
            string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase));
        if (!exists) return Error(404, "No such offline file.");
        var locked = CheckLock(folder.Name, relPath, request.LockToken);
        if (locked is not null) return locked;
        string token = "opaquelocktoken:" + Guid.NewGuid().ToString("N");
        lock (_locksGuard) { _locks[token] = LockKey(folder.Name, relPath); }
        XNamespace d = "DAV:";
        var doc = new XDocument(
            new XElement(d + "prop",
                new XElement(d + "lockdiscovery",
                    new XElement(d + "activelock",
                        new XElement(d + "locktype", new XElement(d + "write")),
                        new XElement(d + "lockscope", new XElement(d + "exclusive")),
                        new XElement(d + "depth", "0"),
                        new XElement(d + "locktoken",
                            new XElement(d + "href", token))))));
        byte[] body = Encoding.UTF8.GetBytes(doc.ToString());
        var result = new DavResult
        {
            Status = 200,
            ContentType = "application/xml; charset=utf-8",
            ContentLength = body.Length,
            BodyBytes = body,
        };
        result.Headers["Lock-Token"] = "<" + token + ">";
        return result;
    }

    private DavResult Unlock(DavRequest request)
    {
        if (request.LockToken is null)
            return Error(400, "A lock token is required.");
        lock (_locksGuard)
        {
            if (!_locks.Remove(request.LockToken))
                return Error(409, "No such lock.");
        }
        return new DavResult { Status = 204, ContentLength = 0 };
    }

    private (OfflineFolderEntry Folder, OfflineFileEntry File, BackupLocation Location)? ResolveFile(DavRequest request)
    {
        if (request.Segments.Length < 2) return null;
        var entries = Entries();
        if (entries is null) return null;
        var folder = entries.FirstOrDefault(e =>
            string.Equals(e.Name, request.Segments[0], StringComparison.OrdinalIgnoreCase));
        if (folder is null) return null;
        string relPath = string.Join("/", request.Segments[1..]);
        var file = folder.Files.FirstOrDefault(f =>
            string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase));
        if (file is null) return null;
        var location = _config.SnapshotLocations().FirstOrDefault(l =>
            string.Equals(l.Name, folder.LocationId, StringComparison.OrdinalIgnoreCase));
        if (location is null) return null;
        return (folder, file, location);
    }

    private static string ErrorMessage(object payload) =>
        payload.GetType().GetProperty("Error")?.GetValue(payload) as string
            ?? payload.GetType().GetProperty("error")?.GetValue(payload) as string
            ?? "The operation failed.";
}
