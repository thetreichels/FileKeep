using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using UsenetBackup.Core.Json;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Guards the trim-published dashboard: every request-body type read via
/// ReadFromJsonAsync in Dashboard.cs must have source-generated metadata in
/// one of the contexts on the HttpJsonOptions resolver chain. A missing type
/// makes the trimmed single-file build throw NotSupportedException, which
/// the handlers surface as "Invalid JSON." — that regression broke the
/// passphrase save, authenticator verify, backup/restore operations and
/// location/provider saves in 0.08.13 (found in real-world testing,
/// 2026-10-19).
/// </summary>
public class JsonContextRegistrationTests
{
    [Fact]
    public void DashboardBodyTypes_HaveSourceGenMetadata()
    {
        Assert.NotNull(DashboardJsonContext.Default.JsonElement);
        Assert.NotNull(FileKeepJsonContext.Default.BackupJobConfig);
        Assert.NotNull(FileKeepJsonContext.Default.BackupLocation);
        Assert.NotNull(FileKeepJsonContext.Default.NntpConfig);
        Assert.NotNull(FileKeepJsonContext.Default.ListNntpConfig);
    }

    /// <summary>
    /// Every type registered in either context must resolve through
    /// the resolver chain the service actually configures in
    /// Dashboard.cs (ConfigureHttpJsonOptions: DashboardJsonContext,
    /// then FileKeepJsonContext) — not just the handful of types named
    /// above. The registered set is enumerated from the generated
    /// contexts themselves, and its size is pinned: deregistering a
    /// response type shrinks the enumeration and fails here instead
    /// of passing silently while the trimmed build 500s (D-H2). The
    /// old version of this guard named 3 of 62 dashboard types and
    /// probed each context in isolation, so neither a deregistration
    /// nor a broken chain composition could ever fail it.
    /// </summary>
    [Fact]
    public void AllContextTypes_ResolveThroughProductionChain()
    {
        // The production resolver chain + options (see ProductionJson).
        var chain = ProductionJson.Resolver;
        var options = ProductionJson.Options;

        var dashboardTypes = RegisteredTypes(typeof(DashboardJsonContext));
        Assert.True(dashboardTypes.Count >= 58,
            $"DashboardJsonContext registrations shrank to {dashboardTypes.Count} (expected at least 58): " +
            "a deregistered response type 500s in the trimmed build.");
        var coreTypes = RegisteredTypes(typeof(FileKeepJsonContext));
        Assert.True(coreTypes.Count >= 16,
            $"FileKeepJsonContext registrations shrank to {coreTypes.Count} (expected at least 16).");

        foreach (Type type in dashboardTypes.Concat(coreTypes).Distinct())
        {
            var info = chain.GetTypeInfo(type, options);
            Assert.True(info is not null,
                $"{type.FullName} is registered in a context but does not resolve " +
                "through the production resolver chain.");
            // The metadata must be usable through the chain, not merely
            // present: serialize a value of the type through it — null
            // for reference types, a default instance for value types
            // (the generated contexts also expose dependency types
            // like Boolean, for which null is not a legal value).
            object? sample =
                type == typeof(JsonElement) ? JsonDocument.Parse("{}").RootElement
                : type.IsValueType ? Activator.CreateInstance(type)
                : null;
            try
            {
                string json = JsonSerializer.Serialize(sample, type, options);
                if (sample is null)
                    Assert.Equal("null", json);
            }
            catch (Exception ex)
            {
                Assert.Fail($"{type.FullName} does not serialize through the chain: {ex.Message}");
            }
        }

        // The highest-traffic response types, pinned by name so a
        // deregistration names its victim in the failure output.
        foreach (Type type in new[]
        {
            typeof(StatusDto), typeof(ConfigSnapshotDto), typeof(BackupDto[]),
            typeof(BrowseFoldersDto), typeof(ErrorDto), typeof(List<ActiveRestoreDto>),
            typeof(LocationDiskDto[]), typeof(YubiKeyStatusDto),
        })
        {
            Assert.True(chain.GetTypeInfo(type, options) is not null,
                $"{type.FullName} must resolve through the production resolver chain.");
        }
    }

    private static List<Type> RegisteredTypes(Type contextType) =>
        contextType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .Distinct()
            .ToList();
}
