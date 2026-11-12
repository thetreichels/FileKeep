using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FileKeep.Core.Json;

/// <summary>
/// Shared trim-safe JSON helpers. The <see cref="JsonTypeInfo{T}"/> overloads
/// of <see cref="JsonSerializer"/> carry no [RequiresUnreferencedCode], so
/// they serialize without IL2026 trim warnings. The type info is resolved
/// from the caller's options (via <see cref="JsonSerializerOptions.GetTypeInfo"/>)
/// so custom settings (indentation, naming policy, case-insensitivity) are
/// honored, and the resolver chain on the options decides which
/// source-generated context supplies the metadata.
/// <para>
/// <see cref="FileKeepJsonOptions"/> and <see cref="Relay.RelayJson"/> are
/// thin delegating wrappers over this class so their existing call sites
/// need no changes; the per-context options objects and contexts stay
/// separate.
/// </para>
/// </summary>
public static class TrimSafeJson
{
    /// <summary>
    /// Serializes with the given options, going through the trim-warning-free
    /// JsonTypeInfo overload. The type info is resolved from the options so
    /// custom settings (indentation, naming policy) are honored.
    /// </summary>
    public static string Serialize<T>(T value, JsonSerializerOptions options, JsonTypeInfo<T> typeInfo)
    {
        var resolved = (JsonTypeInfo<T>)options.GetTypeInfo(typeInfo.Type);
        return JsonSerializer.Serialize(value, resolved);
    }

    /// <summary>
    /// Deserializes with the given options, going through the trim-warning-free
    /// JsonTypeInfo overload.
    /// </summary>
    public static T? Deserialize<T>(string json, JsonSerializerOptions options, JsonTypeInfo<T> typeInfo)
    {
        var resolved = (JsonTypeInfo<T>)options.GetTypeInfo(typeInfo.Type);
        return JsonSerializer.Deserialize(json, resolved);
    }
}
