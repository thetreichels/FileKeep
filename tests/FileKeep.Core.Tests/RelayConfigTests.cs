using FileKeep.Core.Json;
using FileKeep.Core.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Relay config fields on ServiceConfig: JSON round-trip through the
/// production source-generated context (no context change was needed —
/// FileKeepJsonContext already covers ServiceConfig).
/// </summary>
public class RelayConfigTests
{
    [Fact]
    public void RelayFields_RoundTrip_ThroughServiceConfigJson()
    {
        var config = new ServiceConfig
        {
            RelayFolder = @"C:\relay",
            RelayPairingSecretProtected = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        };
        string json = FileKeepJsonOptions.Serialize(
            config, FileKeepJsonOptions.ServiceConfigSave, FileKeepJsonContext.Default.ServiceConfig);
        ServiceConfig? loaded = FileKeepJsonOptions.Deserialize(
            json, FileKeepJsonOptions.ServiceConfigLoad, FileKeepJsonContext.Default.ServiceConfig);
        Assert.NotNull(loaded);
        Assert.Equal(@"C:\relay", loaded!.RelayFolder);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", loaded.RelayPairingSecretProtected);
    }

    [Fact]
    public void RelayFields_Unset_AreOmittedFromJson()
    {
        var config = new ServiceConfig();
        string json = FileKeepJsonOptions.Serialize(
            config, FileKeepJsonOptions.ServiceConfigSave, FileKeepJsonContext.Default.ServiceConfig);
        Assert.DoesNotContain("relayFolder", json);
        Assert.DoesNotContain("relayPairingSecretProtected", json);
        ServiceConfig? loaded = FileKeepJsonOptions.Deserialize(
            json, FileKeepJsonOptions.ServiceConfigLoad, FileKeepJsonContext.Default.ServiceConfig);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.RelayFolder);
        Assert.Null(loaded.RelayPairingSecretProtected);
    }
}
