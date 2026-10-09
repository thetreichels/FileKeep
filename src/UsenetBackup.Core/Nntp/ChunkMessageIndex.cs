using System.Text.Json;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Maps chunk IDs to their current Usenet message IDs, per provider.
///
/// Chunk message IDs are normally deterministic
/// (&lt;chunkId.repoId@usenet-backup&gt;), but retention refresh republishes
/// articles under NEW message IDs (servers reject duplicate IDs, so
/// reposting the same ID does not extend retention). This index records
/// which message ID is currently live for each republished chunk, so
/// download and existence checks find the refreshed copy.
///
/// The mapping is per provider (keyed by host/newsgroup): a refreshed
/// message ID posted to provider A does not exist on provider B, so a
/// global chunk→ID map would cause reads through B to request an article
/// B never received. Each provider's refresh history is tracked
/// independently.
///
/// Persisted as JSON in the repo root. Writes are atomic (write-temp-then
/// -rename) so a crash cannot leave a half-written index.
/// </summary>
public sealed class ChunkMessageIndex
{
    private readonly string _path;
    // providerKey -> (chunkIdHex -> messageId)
    private Dictionary<string, Dictionary<string, string>> _map;

    public ChunkMessageIndex(string repoRoot)
    {
        _path = Path.Combine(repoRoot, "chunk-message-ids.json");
        _map = Load();
    }

    /// <summary>
    /// Builds the provider key from host and newsgroup.
    /// </summary>
    public static string MakeProviderKey(string host, string newsgroup) =>
        $"{host?.Trim().ToLowerInvariant()}/{newsgroup?.Trim()}";

    /// <summary>
    /// Gets the live message ID for a chunk on a provider. Returns the
    /// deterministic ID if the chunk was never republished under a new
    /// identity on that provider.
    /// </summary>
    public string GetMessageId(string providerKey, string chunkIdHex, string repoId)
    {
        if (providerKey is not null &&
            _map.TryGetValue(providerKey, out var byChunk) &&
            byChunk.TryGetValue(chunkIdHex, out string? messageId))
            return messageId;
        return ArticleCodec.MakeMessageId(chunkIdHex, repoId);
    }

    /// <summary>
    /// Records that a chunk was republished under a new message ID on a
    /// provider. The new ID becomes the live one for subsequent lookups
    /// against that provider.
    /// </summary>
    public void RecordNewIdentity(string providerKey, string chunkIdHex, string newMessageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerKey);
        if (!_map.TryGetValue(providerKey, out var byChunk))
        {
            byChunk = new Dictionary<string, string>();
            _map[providerKey] = byChunk;
        }
        byChunk[chunkIdHex] = newMessageId;
        Save();
    }

    /// <summary>
    /// True if the chunk has a republished (non-deterministic) identity on
    /// the given provider.
    /// </summary>
    public bool HasNewIdentity(string providerKey, string chunkIdHex) =>
        providerKey is not null &&
        _map.TryGetValue(providerKey, out var byChunk) &&
        byChunk.ContainsKey(chunkIdHex);

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, Dictionary<string, string>>();
        try
        {
            string json = File.ReadAllText(_path);
            // Try the current nested format first.
            var nested = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
            if (nested is not null)
                return nested;
        }
        catch { /* fall through to legacy attempt */ }

        try
        {
            // Legacy flat format (chunkId -> messageId, single global map).
            // Migrate under a "legacy" provider key so old refresh records
            // are not silently dropped.
            string json = File.ReadAllText(_path);
            var flat = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (flat is not null && flat.Count > 0)
                return new Dictionary<string, Dictionary<string, string>>
                {
                    ["legacy"] = flat
                };
        }
        catch { /* corrupt file: start empty */ }

        return new Dictionary<string, Dictionary<string, string>>();
    }

    private void Save()
    {
        string tmp = _path + ".tmp";
        string json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
