using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using UsenetBackup.Core.Json;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Maps chunk IDs to their current Usenet message IDs, per provider.
///
/// Chunk message IDs are normally deterministic
/// (&lt;chunkId.repoId@invalid&gt;, legacy @usenet-backup&gt;), but retention refresh republishes
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
    private readonly object _mapLock = new();
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
        if (TryGetMessageId(providerKey, chunkIdHex, out string? messageId))
            return messageId;
        return ArticleCodec.MakeMessageId(chunkIdHex, repoId);
    }

    /// <summary>
    /// Recorded-only lookup: returns the republished message ID for this
    /// content ID on the provider, or false if it was never republished.
    /// The key is ID-agnostic — it works for chunk IDs and volume IDs alike.
    /// </summary>
    public bool TryGetMessageId(string providerKey, string idHex, [NotNullWhen(true)] out string? messageId)
    {
        messageId = null;
        if (providerKey is null)
            return false;
        lock (_mapLock)
        {
            return _map.TryGetValue(providerKey, out var byId) &&
                byId.TryGetValue(idHex, out messageId);
        }
    }

    /// <summary>
    /// Records that a chunk was republished under a new message ID on a
    /// provider. The new ID becomes the live one for subsequent lookups
    /// against that provider.
    /// </summary>
    public void RecordNewIdentity(string providerKey, string chunkIdHex, string newMessageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerKey);
        if (!IsValidMessageId(newMessageId))
            throw new ArgumentException($"Invalid message ID: {newMessageId}", nameof(newMessageId));
        lock (_mapLock)
        {
            if (!_map.TryGetValue(providerKey, out var byChunk))
            {
                byChunk = new Dictionary<string, string>();
                _map[providerKey] = byChunk;
            }
            byChunk[chunkIdHex] = newMessageId;
        }
        Save();
    }

    /// <summary>
    /// True if the chunk has a republished (non-deterministic) identity on
    /// the given provider.
    /// </summary>
    public bool HasNewIdentity(string providerKey, string chunkIdHex)
    {
        if (providerKey is null)
            return false;
        lock (_mapLock)
        {
            return _map.TryGetValue(providerKey, out var byChunk) &&
                byChunk.ContainsKey(chunkIdHex);
        }
    }

    /// <summary>
    /// Serializes the index to JSON for remote publication. The format
    /// matches the local file format (providerKey -&gt; chunkId -&gt;
    /// messageId), so a recovering machine can deserialize it directly.
    /// </summary>
    public string ToJson()
    {
        lock (_mapLock)
        {
            return JsonSerializer.Serialize(_map, FileKeepJsonContext.Default.DictionaryStringDictionaryStringString);
        }
    }

    /// <summary>
    /// Replaces the in-memory map with the deserialized remote index and
    /// persists it locally. Used by recovery on a machine that does not
    /// have the local index file. Message IDs are validated: a malicious
    /// server must not inject CRLF into IDs that get interpolated into
    /// NNTP commands (STAT {id}).
    /// </summary>
    public void LoadFromJson(string json)
    {
        var map = JsonSerializer.Deserialize(json, FileKeepJsonContext.Default.DictionaryStringDictionaryStringString);
        if (map is null)
            return;
        foreach (var byChunk in map.Values)
        {
            foreach (string messageId in byChunk.Values)
            {
                if (!IsValidMessageId(messageId))
                    throw new InvalidDataException(
                        $"Remote message index contains an invalid message ID: {messageId}");
            }
        }
        lock (_mapLock)
        {
            _map = map;
        }
        Save();
    }

    /// <summary>
    /// Message IDs we generate look like &lt;...@invalid&gt; (legacy: &lt;...@usenet-backup&gt;). At minimum,
    /// an ID interpolated into an NNTP command must not contain CR, LF, or
    /// whitespace that would split protocol lines.
    /// </summary>
    internal static bool IsValidMessageId(string messageId) =>
        !string.IsNullOrEmpty(messageId) &&
        messageId.Length <= 256 &&
        messageId[0] == '<' &&
        messageId[^1] == '>' &&
        !messageId.Any(c => c == '\r' || c == '\n' || char.IsWhiteSpace(c));

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, Dictionary<string, string>>();
        try
        {
            string json = File.ReadAllText(_path);
            // Try the current nested format first.
            var nested = JsonSerializer.Deserialize(json, FileKeepJsonContext.Default.DictionaryStringDictionaryStringString);
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
            var flat = JsonSerializer.Deserialize(json, FileKeepJsonContext.Default.DictionaryStringString);
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
        // Unique temp name: concurrent saves must not share a temp file.
        // Snapshot under the lock so serialization sees a consistent map.
        string json;
        lock (_mapLock)
        {
            json = FileKeepJsonOptions.Serialize(_map,
                FileKeepJsonOptions.Indented, FileKeepJsonContext.Default.DictionaryStringDictionaryStringString);
        }
        string tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, json);
        try { File.Move(tmp, _path, overwrite: true); }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
