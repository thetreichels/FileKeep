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

    /// <summary>
    /// Default read-time cap for a single chunk download (64 MiB). Set
    /// <see cref="MaxDownloadBytes"/> tighter from the repo's
    /// chunk-size-derived bound when known.
    /// </summary>
    public const long DefaultMaxDownloadBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Read-time cap (bytes) for a single chunk response. The download aborts
    /// mid-read when exceeded, so a malicious oversized response cannot
    /// exhaust memory.
    /// </summary>
    public long MaxDownloadBytes { get; set; } = DefaultMaxDownloadBytes;

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
        // ResponseHeadersRead + manual bounded copy: ReadAsByteArrayAsync would
        // buffer an unbounded malicious response before we could reject it.
        using var resp = _http.GetAsync($"{_baseUrl}/chunks/{chunkIdHex}", HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidDataException($"Chunk {chunkIdHex} not found on LAN store {_baseUrl}.");
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new InvalidDataException(
                $"Chunk {chunkIdHex} declares {resp.Content.Headers.ContentLength:N0} bytes, exceeding " +
                $"the maximum download size of {MaxDownloadBytes:N0} bytes; rejecting as malicious or corrupt.");
        using var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
                throw new InvalidDataException(
                    $"Chunk {chunkIdHex} exceeds the maximum download size of {MaxDownloadBytes:N0} bytes; " +
                    "aborting (possible malicious oversized response).");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
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
