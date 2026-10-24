using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Service;

namespace UsenetBackup.Core.Json;

/// <summary>
/// Source-generated JSON serialization metadata for trim-safe publishing.
/// When the app is published with trimming enabled, runtime reflection-based
/// serialization (JsonSerializer.Serialize&lt;T&gt;) can break because the
/// trimmer cannot see which types are accessed via reflection. This context
/// generates the serialization code at compile time, so the trimmer sees
/// everything and nothing is removed.
/// </summary>
[JsonSerializable(typeof(List<UsenetUploadTracker.UploadRecord>))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
[JsonSerializable(typeof(Dictionary<string, ProviderLimits.LimitEntry>))]
[JsonSerializable(typeof(ServiceConfig))]
[JsonSerializable(typeof(BackupJobConfig))]
[JsonSerializable(typeof(NntpConfig))]
[JsonSerializable(typeof(List<NntpConfig>))]
[JsonSerializable(typeof(BackupLocation))]
[JsonSerializable(typeof(ProtectionProfileRequest))]
[JsonSerializable(typeof(ProtectionProfileResponse))]
[JsonSerializable(typeof(AddYubiKeyRequest))]
[JsonSerializable(typeof(AddYubiKeyResponse))]
[JsonSerializable(typeof(YubiKeyLinkRequest))]
[JsonSerializable(typeof(RepositoryConfig))]
[JsonSerializable(typeof(BackupManifest))]
public partial class FileKeepJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Pre-built <see cref="JsonSerializerOptions"/> instances wired to the
/// source-generated <see cref="FileKeepJsonContext"/>. Use these at call
/// sites instead of constructing options inline.
///
/// NOTE: Do not pass these directly to JsonSerializer.Serialize/Deserialize —
/// that overload carries [RequiresUnreferencedCode] and triggers IL2026 trim
/// warnings. Instead use the Serialize/Deserialize helpers below, which go
/// through the warning-free JsonTypeInfo overloads.
/// </summary>
public static class FileKeepJsonOptions
{
    /// <summary>Default options (equivalent to <c>new JsonSerializerOptions()</c>).</summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        TypeInfoResolver = FileKeepJsonContext.Default,
    };

    /// <summary>Indented output, for human-readable config files.</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        TypeInfoResolver = FileKeepJsonContext.Default,
    };

    /// <summary>
    /// Options for <see cref="ServiceConfig"/> deserialization: case-insensitive
    /// property names, and fail closed on unrecognized members (typos must not
    /// silently fall back to defaults).
    /// </summary>
    public static readonly JsonSerializerOptions ServiceConfigLoad = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = FileKeepJsonContext.Default,
    };

    /// <summary>
    /// Options for <see cref="ServiceConfig"/> serialization: indented with
    /// camelCase property names.
    /// </summary>
    public static readonly JsonSerializerOptions ServiceConfigSave = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = FileKeepJsonContext.Default,
    };

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
