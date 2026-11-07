using FileKeep.Core.Relay;
using FileKeep.Core.Service;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Relay dashboard endpoints (DashboardApi.Relay): status, pairing
/// validation, beam/inbox/drops gating. Paths that need DPAPI (pair
/// success, beam with keys) are Windows-only and covered by the laptop
/// suite; here we test the platform-independent behavior.
/// </summary>
public class RelayDashboardTests : IDisposable
{
    private readonly string _relayRoot;
    private readonly byte[] _seed = RelayCrypto.NewPairingSecret();
    private readonly byte[] _hmacKey;
    private readonly byte[] _encKey;

    public RelayDashboardTests()
    {
        _relayRoot = Path.Combine(Path.GetTempPath(), "fk-relay-dash-" + Guid.NewGuid().ToString("N"));
        _hmacKey = RelayCrypto.DeriveHmacKey(_seed);
        _encKey = RelayCrypto.DeriveEncKey(_seed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_relayRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void GetRelayStatus_Unconfigured_ReportsDisabled()
    {
        var config = new ServiceConfig();
        var (status, payload) = DashboardApi.GetRelayStatus(config, null);
        Assert.Equal(200, status);
        var dto = Assert.IsType<DashboardApi.RelayStatusDto>(payload);
        Assert.False(dto.Configured);
        Assert.False(dto.Paired);
        Assert.Equal(RelayPollerState.Disabled.ToString(), dto.State);
    }

    [Fact]
    public void GetRelayStatus_ConfiguredWithPoller_ReflectsPoller()
    {
        var config = new ServiceConfig
        {
            RelayFolder = _relayRoot,
            RelayPairingSecretProtected = "placeholder",
        };
        var poller = new RelayPoller(() => (_relayRoot, new RelayPoller.RelayKeys(_hmacKey, _encKey)));
        _ = new RelayWriter(_relayRoot, _hmacKey, _encKey); // creates the relay folders
        poller.PollOnce();
        var (status, payload) = DashboardApi.GetRelayStatus(config, poller);
        Assert.Equal(200, status);
        var dto = Assert.IsType<DashboardApi.RelayStatusDto>(payload);
        Assert.True(dto.Configured);
        Assert.True(dto.Paired);
        Assert.Equal(_relayRoot, dto.RelayFolder);
        Assert.Equal(RelayPollerState.Running.ToString(), dto.State);
        Assert.NotNull(dto.LastPollUtc);
    }

    [Fact]
    public void PairRelay_EmptyFolder_Returns400()
    {
        var config = new ServiceConfig();
        var (status, payload) = DashboardApi.PairRelay(config, null,
            new DashboardApi.RelayPairRequestDto { RelayFolder = "", RelayBaseUrl = "http://example.com/relay" });
        Assert.Equal(400, status);
        Assert.IsType<ErrorDto>(payload);
    }

    [Fact]
    public void PairRelay_BadUrl_Returns400()
    {
        var config = new ServiceConfig();
        var (status, _) = DashboardApi.PairRelay(config, null,
            new DashboardApi.RelayPairRequestDto { RelayFolder = _relayRoot, RelayBaseUrl = "not a url" });
        Assert.Equal(400, status);
    }

    [Fact]
    public void PairRelay_NonHttpUrl_Returns400()
    {
        var config = new ServiceConfig();
        var (status, _) = DashboardApi.PairRelay(config, null,
            new DashboardApi.RelayPairRequestDto { RelayFolder = _relayRoot, RelayBaseUrl = "ftp://example.com/relay" });
        Assert.Equal(400, status);
    }

    [Fact]
    public void UnpairRelay_ClearsSecret()
    {
        var config = new ServiceConfig
        {
            RelayFolder = _relayRoot,
            RelayPairingSecretProtected = "placeholder",
        };
        var (status, _) = DashboardApi.UnpairRelay(config, null);
        Assert.Equal(200, status);
        Assert.Null(config.RelayPairingSecretProtected);
        Assert.Equal(_relayRoot, config.RelayFolder); // folder is kept for re-pairing
    }

    [Fact]
    public void BeamBundle_NotConfigured_Returns400()
    {
        var config = new ServiceConfig();
        var (status, _) = DashboardApi.BeamBundle(config, "C:\\x.kfb", null, null);
        Assert.Equal(400, status);
    }

    [Fact]
    public void BeamBundle_Unpaired_Returns400()
    {
        var config = new ServiceConfig { RelayFolder = _relayRoot };
        var (status, payload) = DashboardApi.BeamBundle(config, "C:\\x.kfb", null, null);
        Assert.Equal(400, status);
        Assert.Contains("paired", ((ErrorDto)payload).Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListRelayDrops_NotConfigured_Returns400()
    {
        var config = new ServiceConfig();
        var (status, _) = DashboardApi.ListRelayDrops(config);
        Assert.Equal(400, status);
    }

    [Fact]
    public void ListRelayDrops_NoDrops_ReturnsEmpty()
    {
        var config = new ServiceConfig { RelayFolder = _relayRoot };
        Directory.CreateDirectory(Path.Combine(_relayRoot, "drops"));
        var (status, payload) = DashboardApi.ListRelayDrops(config);
        Assert.Equal(200, status);
        Assert.Empty((DashboardApi.RelayDropDto[])payload);
    }

    [Fact]
    public void ListRelayDrops_UnpairedDrop_ListsAsUnverified()
    {
        var writer = new RelayWriter(_relayRoot, _hmacKey, _encKey);
        var rng = new Random(61);
        byte[] kfb = new byte[1000];
        rng.NextBytes(kfb);
        writer.PublishDrop(kfb, "dropped.kfb");

        var config = new ServiceConfig { RelayFolder = _relayRoot };
        var (status, payload) = DashboardApi.ListRelayDrops(config);
        Assert.Equal(200, status);
        var drops = Assert.Single((DashboardApi.RelayDropDto[])payload);
        Assert.Equal("dropped.kfb", drops.Name);
        Assert.False(drops.Verified); // no pairing secret, so no HMAC check possible
    }

    [Fact]
    public void ListRelayInbox_NotConfigured_Returns400()
    {
        var config = new ServiceConfig();
        var (status, _) = DashboardApi.ListRelayInbox(config);
        Assert.Equal(400, status);
    }

    [Fact]
    public void ListRelayInbox_Unpaired_Returns400()
    {
        var config = new ServiceConfig { RelayFolder = _relayRoot };
        var (status, _) = DashboardApi.ListRelayInbox(config);
        Assert.Equal(400, status);
    }
}
