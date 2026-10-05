namespace UsenetBackup.Core.Lan;

/// <summary>
/// IBlobStore over HTTP for LAN restores. Fetches encrypted chunks from a
/// `usenet-backup serve` instance on the local network. Read-heavy: Exists
/// uses HEAD, Get uses GET. Put is supported (for LAN backup targets) via POST.
/// The store never sees plaintext or keys — blobs are encrypted chunks.
/// </summary>
public sealed class HttpBlobStore : IBlobStore, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private bool _disposed;

    public HttpBlobStore(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool Exists(string chunkIdHex)
    {
        ValidateChunkId(chunkIdHex);
        using var req = new HttpRequestMessage(HttpMethod.Head, $"{_baseUrl}/chunks/{chunkIdHex}");
        using var resp = _http.Send(req);
        return resp.IsSuccessStatusCode;
    }

    public void Put(string chunkIdHex, byte[] blob)
    {
        ValidateChunkId(chunkIdHex);
        ArgumentNullException.ThrowIfNull(blob);
        using var content = new ByteArrayContent(blob);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var resp = _http.PostAsync($"{_baseUrl}/chunks/{chunkIdHex}", content).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
    }

    public byte[] Get(string chunkIdHex)
    {
        ValidateChunkId(chunkIdHex);
        using var resp = _http.GetAsync($"{_baseUrl}/chunks/{chunkIdHex}").GetAwaiter().GetResult();
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidDataException($"Chunk {chunkIdHex} not found on LAN store {_baseUrl}.");
        resp.EnsureSuccessStatusCode();
        return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    public long StoredCount
    {
        get
        {
            using var resp = _http.GetAsync($"{_baseUrl}/count").GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return long.TryParse(text.Trim(), out long n) ? n : 0;
        }
    }

    /// <summary>
    /// Lists backup IDs available on the LAN server.
    /// </summary>
    public IReadOnlyList<string> ListManifestIds()
    {
        using var resp = _http.GetAsync($"{_baseUrl}/manifests").GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return System.Text.Json.JsonSerializer.Deserialize<string[]>(json)
            ?? Array.Empty<string>();
    }

    /// <summary>
    /// Fetches a manifest JSON from the LAN server. Returns null if not found.
    /// </summary>
    public string? GetManifestJson(string backupId)
    {
        if (backupId.Length != 32 || !backupId.All(Uri.IsHexDigit))
            throw new ArgumentException("Backup ID must be 32 hex chars.", nameof(backupId));
        using var resp = _http.GetAsync($"{_baseUrl}/manifests/{backupId}").GetAwaiter().GetResult();
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static void ValidateChunkId(string chunkIdHex)
    {
        if (chunkIdHex.Length != 64 || !chunkIdHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Chunk ID must be 64 hex chars.", nameof(chunkIdHex));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _http.Dispose();
            _disposed = true;
        }
    }
}
