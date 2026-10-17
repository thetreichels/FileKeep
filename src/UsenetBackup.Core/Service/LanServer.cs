using System.Net;
using System.Text;
using System.Text.Json;

namespace UsenetBackup.Core.Service;

/// <summary>
/// Serves repos' chunks and manifests over HTTP for LAN restores/backups.
/// Endpoints: GET /count, GET /manifests, GET /manifests/{id},
/// GET/HEAD /chunks/{id}, POST /chunks/{id}.
/// Chunk IDs are content-addressed, so multiple repo roots are served as a
/// union: reads check each root in order, POSTs go to the primary (first) root.
/// </summary>
public sealed class LanServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly IReadOnlyList<string> _repoRoots;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int Port { get; }
    public string Bind { get; }
    public bool IsRunning => _loop is { IsCompleted: false };

    public LanServer(string repoRoot, int port = 8477, string bind = "127.0.0.1")
        : this(new[] { repoRoot }, port, bind)
    {
    }

    public LanServer(IReadOnlyList<string> repoRoots, int port = 8477, string bind = "127.0.0.1")
    {
        if (repoRoots is null || repoRoots.Count == 0)
            throw new ArgumentException("At least one repo root is required.", nameof(repoRoots));
        _repoRoots = repoRoots;
        Port = port;
        Bind = bind;
        _listener.Prefixes.Add($"http://{bind}:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(RunLoop);
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _loop?.Wait(TimeSpan.FromSeconds(5));
    }

    private async Task RunLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            string method = ctx.Request.HttpMethod;

            if (path == "/count" && method == "GET")
            {
                long count = 0;
                foreach (string chunksDir in _repoRoots.Select(r => Path.Combine(r, "chunks")))
                    if (Directory.Exists(chunksDir))
                        count += Directory.GetFiles(chunksDir, "*", SearchOption.AllDirectories).Length;
                WriteText(ctx, count.ToString(), "text/plain");
            }
            else if (path == "/manifests" && method == "GET")
            {
                var ids = _repoRoots
                    .Select(r => Path.Combine(r, "manifests"))
                    .Where(Directory.Exists)
                    .SelectMany(d => Directory.GetFiles(d, "*.json"))
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                WriteText(ctx, JsonSerializer.Serialize(ids), "application/json");
            }
            else if (path.StartsWith("/manifests/", StringComparison.Ordinal) && method == "GET")
            {
                string backupId = path["/manifests/".Length..];
                if (backupId.Length != 32 || !backupId.All(Uri.IsHexDigit))
                {
                    ctx.Response.StatusCode = 400;
                }
                else
                {
                    string? manifestPath = _repoRoots
                        .Select(r => Path.Combine(r, "manifests", backupId + ".json"))
                        .FirstOrDefault(File.Exists);
                    if (manifestPath is null)
                    {
                        ctx.Response.StatusCode = 404;
                    }
                    else
                    {
                        byte[] data = File.ReadAllBytes(manifestPath);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = data.Length;
                        ctx.Response.OutputStream.Write(data, 0, data.Length);
                    }
                }
            }
            else if (path.StartsWith("/chunks/", StringComparison.Ordinal))
            {
                string chunkId = path["/chunks/".Length..];
                if (chunkId.Length != 64 || !chunkId.All(Uri.IsHexDigit))
                {
                    ctx.Response.StatusCode = 400;
                }
                else
                {
                    string? chunkPath = _repoRoots
                        .Select(r => Path.Combine(r, "chunks", chunkId[..2], chunkId[2..]))
                        .FirstOrDefault(File.Exists);
                    if (method == "HEAD")
                    {
                        ctx.Response.StatusCode = chunkPath is null ? 404 : 200;
                    }
                    else if (method == "GET")
                    {
                        if (chunkPath is null)
                        {
                            ctx.Response.StatusCode = 404;
                        }
                        else
                        {
                            byte[] data = File.ReadAllBytes(chunkPath);
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/octet-stream";
                            ctx.Response.ContentLength64 = data.Length;
                            ctx.Response.OutputStream.Write(data, 0, data.Length);
                        }
                    }
                    else if (method == "POST")
                    {
                        using var ms = new MemoryStream();
                        ctx.Request.InputStream.CopyTo(ms);
                        byte[] data = ms.ToArray();
                        // POSTs (backup target) go to the primary root.
                        string dir = Path.Combine(_repoRoots[0], "chunks", chunkId[..2]);
                        Directory.CreateDirectory(dir);
                        // Atomic write: temp file + move. If another writer
                        // won the race, the content-addressed target already
                        // exists with identical bytes (dedup).
                        string finalPath = Path.Combine(dir, chunkId[2..]);
                        string tmpPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
                        File.WriteAllBytes(tmpPath, data);
                        try { File.Move(tmpPath, finalPath); }
                        catch (IOException) when (File.Exists(finalPath))
                        {
                            File.Delete(tmpPath);
                        }
                        catch
                        {
                            try { File.Delete(tmpPath); } catch { }
                            throw;
                        }
                        ctx.Response.StatusCode = 200;
                    }
                    else
                    {
                        ctx.Response.StatusCode = 405;
                    }
                }
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static void WriteText(HttpListenerContext ctx, string text, string contentType)
    {
        byte[] buf = Encoding.UTF8.GetBytes(text);
        // Status code and headers must be set before the first body write;
        // setting them after aborts the connection on some hosts.
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        _listener.Close();
    }
}
