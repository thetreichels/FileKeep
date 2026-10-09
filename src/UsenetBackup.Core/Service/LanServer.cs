using System.Net;
using System.Text;
using System.Text.Json;

namespace UsenetBackup.Core.Service;

/// <summary>
/// Serves a repo's chunks and manifests over HTTP for LAN restores/backups.
/// Endpoints: GET /count, GET /manifests, GET /manifests/{id},
/// GET/HEAD /chunks/{id}, POST /chunks/{id}.
/// </summary>
public sealed class LanServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _repoRoot;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int Port { get; }
    public string Bind { get; }
    public bool IsRunning => _loop is { IsCompleted: false };

    public LanServer(string repoRoot, int port = 8477, string bind = "127.0.0.1")
    {
        _repoRoot = repoRoot;
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
        string chunksDir = Path.Combine(_repoRoot, "chunks");
        string manifestsDir = Path.Combine(_repoRoot, "manifests");
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            string method = ctx.Request.HttpMethod;

            if (path == "/count" && method == "GET")
            {
                long count = Directory.Exists(chunksDir)
                    ? Directory.GetFiles(chunksDir, "*", SearchOption.AllDirectories).Length
                    : 0;
                WriteText(ctx, count.ToString(), "text/plain");
            }
            else if (path == "/manifests" && method == "GET")
            {
                var ids = Directory.Exists(manifestsDir)
                    ? Directory.GetFiles(manifestsDir, "*.json")
                        .Select(f => Path.GetFileNameWithoutExtension(f))
                        .OrderBy(id => id)
                        .ToArray()
                    : Array.Empty<string>();
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
                    string manifestPath = Path.Combine(manifestsDir, backupId + ".json");
                    if (!File.Exists(manifestPath))
                    {
                        ctx.Response.StatusCode = 404;
                    }
                    else
                    {
                        byte[] data = File.ReadAllBytes(manifestPath);
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = data.Length;
                        ctx.Response.OutputStream.Write(data, 0, data.Length);
                        ctx.Response.StatusCode = 200;
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
                    string chunkPath = Path.Combine(chunksDir, chunkId[..2], chunkId[2..]);
                    if (method == "HEAD")
                    {
                        ctx.Response.StatusCode = File.Exists(chunkPath) ? 200 : 404;
                    }
                    else if (method == "GET")
                    {
                        if (!File.Exists(chunkPath))
                        {
                            ctx.Response.StatusCode = 404;
                        }
                        else
                        {
                            byte[] data = File.ReadAllBytes(chunkPath);
                            ctx.Response.ContentType = "application/octet-stream";
                            ctx.Response.ContentLength64 = data.Length;
                            ctx.Response.OutputStream.Write(data, 0, data.Length);
                            ctx.Response.StatusCode = 200;
                        }
                    }
                    else if (method == "POST")
                    {
                        using var ms = new MemoryStream();
                        ctx.Request.InputStream.CopyTo(ms);
                        byte[] data = ms.ToArray();
                        string dir = Path.Combine(chunksDir, chunkId[..2]);
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(Path.Combine(dir, chunkId[2..]), data);
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
        ctx.Response.ContentType = contentType;
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.StatusCode = 200;
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        _listener.Close();
    }
}
