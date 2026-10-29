using System.Text.Json.Serialization;

namespace FileKeep.Core.Service;

/// <summary>
/// One previous vault, recorded when vault migration re-points jobs
/// at fresh repos. The old repo path and old targets are kept so the
/// previous vault stays findable: its local dir is untouched, and
/// its destination pools are untouched (fresh pools were created
/// for the new vaults), so it remains restorable with the old
/// secret through a future repo-picker — it is never silently lost.
/// </summary>
public sealed class MigratedVaultRecord
{
    /// <summary>Jobs that were re-pointed away from <see cref="OldRepo"/>.</summary>
    [JsonPropertyName("jobs")]
    public List<string> Jobs { get; set; } = new();

    /// <summary>Local repo path the jobs used before migration.</summary>
    [JsonPropertyName("oldRepo")]
    public string OldRepo { get; set; } = "";

    /// <summary>Local repo path the jobs use after migration.</summary>
    [JsonPropertyName("newRepo")]
    public string NewRepo { get; set; } = "";

    /// <summary>When the migration ran (UTC, round-trip format).</summary>
    [JsonPropertyName("migratedUtc")]
    public string MigratedUtc { get; set; } = "";

    /// <summary>Target names the jobs used before migration.</summary>
    [JsonPropertyName("oldTargets")]
    public List<string> OldTargets { get; set; } = new();
}
