using System.Text.Json;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Maps chunk IDs to their current Usenet message IDs.
///
/// Chunk message IDs are normally deterministic
/// (&lt;chunkId.repoId@usenet-backup&gt;), but retention refresh republishes
/// articles under NEW message IDs (servers reject duplicate IDs, so
/// reposting the same ID does not extend retention). This index records
/// which message ID is currently live for each republished chunk, so
/// download and existence checks find the refreshed copy.
///
/// Persisted as JSON in the repo root. Writes are atomic (write-temp-then
/// -rename) so a crash cannot leave a half-written index.
/// </summary>
public sealed class ChunkMessageIndex
{
    private readonly string _path;
    private Dictionary<string, string> _map; // chunkIdHex -> messageId

    public ChunkMessageIndex(string repoRoot)
    {
        _path = Path.Combine(repoRoot, "chunk-message-ids.json");
        _map = Load();
    }

    /// <summary>
    /// Gets the live message ID for a chunk. Returns the deterministic ID
    /// if the chunk was never republished under a new identity.
    /// </summary>
    public string GetMessageId(string chunkIdHex, string repoId)
    {
        if (_map.TryGetValue(chunkIdHex, out string? messageId))
            return messageId;
        return ArticleCodec.MakeMessageId(chunkIdHex, repoId);
    }

    /// <summary>
    /// Records that a chunk was republished under a new message ID.
    /// The new ID becomes the live one for subsequent lookups.
    /// </summary>
    public void RecordNewIdentity(string chunkIdHex, string newMessageId)
    {
        _map[chunkIdHex] = newMessageId;
        Save();
    }

    /// <summary>True if the chunk has a republished (non-deterministic) identity.</summary>
    public bool HasNewIdentity(string chunkIdHex) => _map.ContainsKey(chunkIdHex);

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, string>();
        try
        {
            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private void Save()
    {
        string tmp = _path + ".tmp";
        string json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
