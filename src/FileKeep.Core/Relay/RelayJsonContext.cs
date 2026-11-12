using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FileKeep.Core.Json;

namespace FileKeep.Core.Relay;

/// <summary>
/// Source-generated JSON serialization metadata for relay file types
/// (trim-safe publishing). Deliberately separate from
/// <see cref="FileKeep.Core.Json.FileKeepJsonContext"/>: relay DTOs are
/// files on the relay, not dashboard request/response bodies, so they do
/// not belong on the dashboard's resolver chain. Use
/// <see cref="RelayJson"/> helpers at call sites, never reflection-based
/// JsonSerializer.Serialize&lt;T&gt;.
/// </summary>
[JsonSerializable(typeof(RelayIndex))]
[JsonSerializable(typeof(RelayIndexEntry))]
[JsonSerializable(typeof(List<RelayIndexEntry>))]
[JsonSerializable(typeof(RelayDropMetadata))]
[JsonSerializable(typeof(RelayInboxMetadata))]
[JsonSerializable(typeof(DropReadyBody))]
[JsonSerializable(typeof(AckBody))]
[JsonSerializable(typeof(ApprovalRequestBody))]
[JsonSerializable(typeof(ApprovalBody))]
public partial class RelayJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Pre-built options and trim-safe helpers for relay file serialization.
/// </summary>
public static class RelayJson
{
    /// <summary>Compact options (wire form).</summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        TypeInfoResolver = RelayJsonContext.Default,
    };

    /// <summary>Indented options (relay files on disk; human-readable).</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        TypeInfoResolver = RelayJsonContext.Default,
    };

    /// <summary>
    /// Serializes with the given options, going through the trim-warning-free
    /// JsonTypeInfo overload. Delegates to <see cref="TrimSafeJson"/>.
    /// </summary>
    public static string Serialize<T>(T value, JsonSerializerOptions options, JsonTypeInfo<T> typeInfo)
        => TrimSafeJson.Serialize(value, options, typeInfo);

    /// <summary>
    /// Deserializes with the given options, going through the trim-warning-free
    /// JsonTypeInfo overload. Delegates to <see cref="TrimSafeJson"/>.
    /// </summary>
    public static T? Deserialize<T>(string json, JsonSerializerOptions options, JsonTypeInfo<T> typeInfo)
        => TrimSafeJson.Deserialize(json, options, typeInfo);
}
