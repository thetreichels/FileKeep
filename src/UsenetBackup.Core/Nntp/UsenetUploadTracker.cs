using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// Tracks when backups were uploaded to each Usenet provider, for
/// expiration monitoring. Stored as JSON in the repo root.
/// </summary>
public sealed class UsenetUploadTracker
{
    private readonly string _path;
    private Dictionary<string, UploadRecord> _records;

    public UsenetUploadTracker(string repoRoot)
    {
        _path = Path.Combine(repoRoot, "usenet-uploads.json");
        _records = Load();
    }

    public sealed class UploadRecord
    {
        [JsonPropertyName("backupId")]
        public string BackupId { get; set; } = "";
        [JsonPropertyName("providerHost")]
        public string ProviderHost { get; set; } = "";
        [JsonPropertyName("uploadedUtc")]
        public DateTime UploadedUtc { get; set; }
        [JsonPropertyName("newsgroup")]
        public string Newsgroup { get; set; } = "";
    }

    /// <summary>Records a successful upload.</summary>
    public void RecordUpload(string backupId, string providerHost, string newsgroup)
    {
        string key = $"{backupId}@{providerHost}";
        _records[key] = new UploadRecord
        {
            BackupId = backupId,
            ProviderHost = providerHost,
            UploadedUtc = DateTime.UtcNow,
            Newsgroup = newsgroup,
        };
        Save();
    }

    /// <summary>Gets all upload records.</summary>
    public IReadOnlyList<UploadRecord> GetAll() => _records.Values.ToList();

    /// <summary>
    /// Gets backups that will expire within the warning window.
    /// </summary>
    public IReadOnlyList<(UploadRecord Record, DateTime ExpiresUtc, int DaysLeft)> GetExpiring(
        Func<string, int> getRetentionDays,
        int warnDays = 90)
    {
        var result = new List<(UploadRecord, DateTime, int)>();
        DateTime now = DateTime.UtcNow;
        foreach (var record in _records.Values)
        {
            int retentionDays = getRetentionDays(record.ProviderHost);
            DateTime expires = record.UploadedUtc.AddDays(retentionDays);
            int daysLeft = (int)(expires - now).TotalDays;
            if (daysLeft <= warnDays)
                result.Add((record, expires, daysLeft));
        }
        return result.OrderBy(x => x.Item3).ToList();
    }

    private Dictionary<string, UploadRecord> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, UploadRecord>();
        try
        {
            string json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<UploadRecord>>(json);
            return list?.ToDictionary(r => $"{r.BackupId}@{r.ProviderHost}")
                ?? new Dictionary<string, UploadRecord>();
        }
        catch
        {
            return new Dictionary<string, UploadRecord>();
        }
    }

    private void Save()
    {
        string json = JsonSerializer.Serialize(_records.Values.ToList(),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
