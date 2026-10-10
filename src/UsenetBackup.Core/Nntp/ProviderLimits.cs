using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Per-provider maximum article size cache. NNTP has no protocol command to
/// query a server's article size limit, so the only reliable method is
/// empirical: post a probe article and see if it is accepted. Probing on
/// every upload would litter the newsgroup and waste time, so successful
/// probe results are cached here (one JSON file per repository).
///
/// The cache is advisory: <see cref="EnsureCapacity"/> re-probes when there
/// is no entry for the provider, and callers should re-probe if uploads
/// start failing (the provider may have lowered its limit).
/// </summary>
public sealed class ProviderLimits
{
    private const string FileName = "provider-limits.json";

    private readonly string _path;
    private readonly Dictionary<string, LimitEntry> _entries;

    public ProviderLimits(string repoRoot)
    {
        _path = Path.Combine(repoRoot, FileName);
        _entries = Load(_path);
    }

    /// <summary>
    /// Returns the cached maximum article size for the provider, or null if
    /// the provider has never been probed.
    /// </summary>
    public long? GetMaxArticleBytes(string host, int port)
    {
        string key = MakeKey(host, port);
        return _entries.TryGetValue(key, out LimitEntry? entry) ? entry.MaxArticleBytes : null;
    }

    /// <summary>
    /// Records a successful probe result for the provider and persists it.
    /// </summary>
    public void Record(string host, int port, long maxArticleBytes)
    {
        if (maxArticleBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxArticleBytes));
        _entries[MakeKey(host, port)] = new LimitEntry
        {
            MaxArticleBytes = maxArticleBytes,
            ProbedUtc = DateTime.UtcNow,
        };
        Save(_path, _entries);
    }

    /// <summary>
    /// Ensures the provider can accept an article of <paramref name="requiredBytes"/>.
    /// If a cached limit covers it, returns immediately. Otherwise invokes
    /// <paramref name="probe"/> (which should post a probe article of exactly
    /// <paramref name="requiredBytes"/> and return true if accepted) and caches
    /// the result on success. Throws <see cref="InvalidDataException"/> with a
    /// clear message if the provider cannot accept the size.
    /// </summary>
    public void EnsureCapacity(string host, int port, long requiredBytes, Func<long, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (requiredBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));

        long? cached = GetMaxArticleBytes(host, port);
        if (cached.HasValue && cached.Value >= requiredBytes)
            return; // Known good — no probe needed.

        bool accepted;
        try
        {
            accepted = probe(requiredBytes);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Upload preflight failed: could not probe {host}:{port} for " +
                $"{requiredBytes} bytes ({ex.Message}). The upload was not started.", ex);
        }
        if (!accepted)
        {
            throw new InvalidDataException(
                $"Upload preflight failed: {host}:{port} does not accept " +
                $"{requiredBytes}-byte articles. Lower volume_size_bytes in repo.json " +
                "(or use --no-volumes) and re-run. The upload was not started.");
        }
        Record(host, port, requiredBytes);
    }

    public static string MakeKey(string host, int port) =>
        $"{host.Trim().ToLowerInvariant()}:{port}";

    private static Dictionary<string, LimitEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, LimitEntry>(StringComparer.OrdinalIgnoreCase);
            string json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<Dictionary<string, LimitEntry>>(json);
            return entries ?? new Dictionary<string, LimitEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // A corrupt cache must never block uploads — start fresh.
            return new Dictionary<string, LimitEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(string path, Dictionary<string, LimitEntry> entries)
    {
        string json = JsonSerializer.Serialize(entries,
            new JsonSerializerOptions { WriteIndented = true });
        // Atomic write: temp file + move, so a crash never leaves half a file.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class LimitEntry
    {
        [JsonPropertyName("max_article_bytes")]
        public long MaxArticleBytes { get; set; }

        [JsonPropertyName("probed_utc")]
        public DateTime ProbedUtc { get; set; }
    }
}
