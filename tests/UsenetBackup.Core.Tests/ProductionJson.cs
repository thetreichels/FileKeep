using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UsenetBackup.Core.Json;
using UsenetBackup.Service;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// The dashboard's production JSON resolution, mirrored for tests:
/// the resolver chain Dashboard.cs configures on HttpJsonOptions —
/// DashboardJsonContext, then FileKeepJsonContext — and nothing else.
/// There is deliberately NO reflection fallback (the framework's
/// public TypeInfoResolverChain type does not exist; combining is
/// done by JsonSerializerOptions in production and by
/// <see cref="CombiningResolver"/> here). In the trimmed shipped
/// build the default reflection resolver cannot produce metadata, so
/// a DTO missing from these contexts 500s; through this helper it
/// fails in tests the same way (D-H1/D-H2). STJ's built-in metadata
/// for framework primitives/arrays is NOT part of this chain —
/// callers that serve such payloads (e.g. /api/log's string[])
/// handle that case explicitly, as DashboardTests.ToJson does.
/// </summary>
internal static class ProductionJson
{
    public static IJsonTypeInfoResolver Resolver { get; } = new CombiningResolver(
        DashboardJsonContext.Default, FileKeepJsonContext.Default);

    public static JsonSerializerOptions Options { get; } =
        new(JsonSerializerDefaults.Web) { TypeInfoResolver = Resolver };

    private sealed class CombiningResolver(params IJsonTypeInfoResolver[] resolvers)
        : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            foreach (var resolver in resolvers)
                if (resolver.GetTypeInfo(type, options) is { } info)
                    return info;
            return null;
        }
    }
}
