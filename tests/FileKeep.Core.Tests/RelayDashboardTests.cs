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

    private RelayPoller PollerWithKeys() =>
        new(() => (_relayRoot, new RelayPoller.RelayKeys(_hmacKey, _encKey)));

    private void SimulatePhoneApproval(string id, string nonce, bool approved)
    {
        string mailboxDir = Path.Combine(_relayRoot, "mailbox", "laptop");
        var body = new ApprovalBody { Nonce = nonce, RefId = "req-1", Approved = approved };
        byte[] bin = RelayCrypto.EncryptMessage(_encKey, _hmacKey, id,
            System.Text.Encoding.UTF8.GetBytes(RelayJson.Serialize(body, RelayJson.Default, RelayJsonContext.Default.ApprovalBody)));
        Directory.CreateDirectory(mailboxDir);
        File.WriteAllBytes(Path.Combine(mailboxDir, id + ".bin"), bin);
        var index = new RelayIndex
        {
            Sequence = 1,
            Messages = new List<RelayIndexEntry>
            {
                new() { Id = id, Kind = RelayMessageKinds.Approval, CreatedUtc = DateTimeOffset.UtcNow, Size = bin.Length },
            },
        };
        index.Hmac = RelayCrypto.ComputeIndexHmac(_hmacKey, index.Sequence, index.Messages);
        File.WriteAllText(Path.Combine(mailboxDir, "index.json"),
            RelayJson.Serialize(index, RelayJson.Indented, RelayJsonContext.Default.RelayIndex));
    }

    [Fact]
    public void RequestPhoneApproval_NoPoller_Returns400()
    {
        var config = new ServiceConfig { RelayFolder = _relayRoot };
        var (status, _) = DashboardApi.RequestPhoneApproval(config, null);
        Assert.Equal(400, status);
    }

    [Fact]
    public void RespondToApproval_NoPoller_Returns400()
    {
        var config = new ServiceConfig { RelayFolder = _relayRoot };
        var (status, _) = DashboardApi.RespondToApproval(config, null, "m1", approved: true);
        Assert.Equal(400, status);
    }

    [Fact]
    public void RespondToApproval_UnknownId_Returns404()
    {
        var poller = PollerWithKeys();
        poller.PollOnce();
        var config = new ServiceConfig { RelayFolder = _relayRoot };
        var (status, _) = DashboardApi.RespondToApproval(config, poller, "no-such-id", approved: true);
        Assert.Equal(404, status);
    }

    [Fact]
    public void ClaimPhoneApproval_NoPoller_Returns400()
    {
        var config = new ServiceConfig();
        var session = new FileKeep.Service.RestoreSession();
        var (status, _) = DashboardApi.ClaimPhoneApproval(config, session, null, "nonce-1");
        Assert.Equal(400, status);
    }

    [Fact]
    public void ClaimPhoneApproval_EmptyNonce_Returns400()
    {
        var poller = PollerWithKeys();
        var config = new ServiceConfig();
        var session = new FileKeep.Service.RestoreSession();
        var (status, _) = DashboardApi.ClaimPhoneApproval(config, session, poller, "");
        Assert.Equal(400, status);
    }

    [Fact]
    public void ClaimPhoneApproval_Waiting_ReturnsNotClaimedNotExpired()
    {
        var poller = PollerWithKeys();
        poller.RecordApprovalRequest("nonce-w", "restore", "Approve restore authorization");
        var config = new ServiceConfig();
        var session = new FileKeep.Service.RestoreSession();
        var (status, payload) = DashboardApi.ClaimPhoneApproval(config, session, poller, "nonce-w");
        Assert.Equal(200, status);
        var dto = Assert.IsType<DashboardApi.RelayPhoneApprovalClaimStatusDto>(payload);
        Assert.False(dto.Claimed);
        Assert.False(dto.Expired);
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void ClaimPhoneApproval_UnknownNonce_ReturnsExpired()
    {
        var poller = PollerWithKeys();
        var config = new ServiceConfig();
        var session = new FileKeep.Service.RestoreSession();
        var (status, payload) = DashboardApi.ClaimPhoneApproval(config, session, poller, "nonce-nope");
        Assert.Equal(200, status);
        var dto = Assert.IsType<DashboardApi.RelayPhoneApprovalClaimStatusDto>(payload);
        Assert.False(dto.Claimed);
        Assert.True(dto.Expired);
    }

    [Fact]
    public void ClaimPhoneApproval_ApprovedUnlocksSessionWithStoredPassphrase()
    {
        string? saved = Environment.GetEnvironmentVariable(BackupScheduler.PassphraseEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, "env-passphrase");
            var poller = PollerWithKeys();
            poller.RecordApprovalRequest("nonce-ok", "restore", "Approve restore authorization");
            SimulatePhoneApproval("m-ok", "nonce-ok", approved: true);
            poller.PollOnce();

            var config = new ServiceConfig();
            var session = new FileKeep.Service.RestoreSession();
            var (status, payload) = DashboardApi.ClaimPhoneApproval(config, session, poller, "nonce-ok");
            Assert.Equal(200, status);
            var dto = Assert.IsType<DashboardApi.RelayPhoneApprovalClaimStatusDto>(payload);
            Assert.True(dto.Claimed);
            Assert.True(session.IsUnlocked);
            Assert.True(session.IsVerified);
            Assert.Equal("env-passphrase", session.GetPassphrase());
            // One approval unlocks one restore — claiming again expires.
            var (status2, payload2) = DashboardApi.ClaimPhoneApproval(config, session, poller, "nonce-ok");
            Assert.Equal(200, status2);
            Assert.True(Assert.IsType<DashboardApi.RelayPhoneApprovalClaimStatusDto>(payload2).Expired);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupScheduler.PassphraseEnvVar, saved);
        }
    }

    [Fact]
    public void ClaimPhoneApproval_Declined_Returns400AndStaysLocked()
    {
        var poller = PollerWithKeys();
        poller.RecordApprovalRequest("nonce-no", "restore", "Approve restore authorization");
        SimulatePhoneApproval("m-no", "nonce-no", approved: false);
        poller.PollOnce();

        var config = new ServiceConfig();
        var session = new FileKeep.Service.RestoreSession();
        var (status, _) = DashboardApi.ClaimPhoneApproval(config, session, poller, "nonce-no");
        Assert.Equal(400, status);
        Assert.False(session.IsUnlocked);
    }
}
