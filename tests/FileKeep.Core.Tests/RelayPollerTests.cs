using System.Text;
using FileKeep.Core.Relay;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Relay poller: inbox import + ack, prune, approval surfacing, states.
/// Keys are injected directly (no DPAPI), so these run on Linux too.
/// </summary>
public class RelayPollerTests : IDisposable
{
    private readonly string _relayRoot;
    private readonly byte[] _seed = RelayCrypto.NewPairingSecret();
    private readonly byte[] _hmacKey;
    private readonly byte[] _encKey;
    private readonly RelayWriter _writer;
    private readonly List<string> _log = new();

    public RelayPollerTests()
    {
        _relayRoot = Path.Combine(Path.GetTempPath(), "fk-relay-poller-" + Guid.NewGuid().ToString("N"));
        _hmacKey = RelayCrypto.DeriveHmacKey(_seed);
        _encKey = RelayCrypto.DeriveEncKey(_seed);
        _writer = new RelayWriter(_relayRoot, _hmacKey, _encKey);
    }

    public void Dispose()
    {
        try { Directory.Delete(_relayRoot, recursive: true); } catch { /* best effort */ }
    }

    private RelayPoller Poller(string? folder, RelayPoller.RelayKeys? keys) =>
        new(() => (folder, keys), _log.Add);

    private RelayPoller.RelayKeys Keys => new(_hmacKey, _encKey);

    private static byte[] RandomBytes(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[length];
        rng.NextBytes(data);
        return data;
    }

    private void SimulatePhoneDrop(string token, byte[] kfb, string name, bool signCorrectly = true)
    {
        string inboxDir = Path.Combine(_relayRoot, "inbox", "laptop");
        File.WriteAllBytes(Path.Combine(inboxDir, token + ".kfb"), kfb);
        File.WriteAllBytes(Path.Combine(inboxDir, token + ".kfb.par2"), RelayPar2.Generate(kfb));
        var created = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var meta = new RelayInboxMetadata
        {
            Token = token,
            Name = name,
            Size = kfb.Length,
            CreatedUtc = created,
            Hmac = signCorrectly
                ? RelayCrypto.ComputeInboxHmac(_hmacKey, token, name, kfb.Length, created)
                : "00",
        };
        File.WriteAllText(Path.Combine(inboxDir, token + ".json"),
            RelayJson.Serialize(meta, RelayJson.Indented, RelayJsonContext.Default.RelayInboxMetadata));
    }

    private void SimulatePhoneMessage(string id, string kind, byte[] plaintext)
    {
        string mailboxDir = Path.Combine(_relayRoot, "mailbox", "laptop");
        byte[] bin = RelayCrypto.EncryptMessage(_encKey, _hmacKey, id, plaintext);
        File.WriteAllBytes(Path.Combine(mailboxDir, id + ".bin"), bin);
        var index = new RelayIndex
        {
            Sequence = 1,
            Messages = new List<RelayIndexEntry>
            {
                new() { Id = id, Kind = kind, CreatedUtc = DateTimeOffset.UtcNow, Size = bin.Length },
            },
        };
        index.Hmac = RelayCrypto.ComputeIndexHmac(_hmacKey, index.Sequence, index.Messages);
        File.WriteAllText(Path.Combine(mailboxDir, "index.json"),
            RelayJson.Serialize(index, RelayJson.Indented, RelayJsonContext.Default.RelayIndex));
    }

    [Fact]
    public void PollOnce_NoFolder_IsDisabled()
    {
        var poller = Poller(null, null);
        poller.PollOnce();
        Assert.Equal(RelayPollerState.Disabled, poller.Status.State);
    }

    [Fact]
    public void PollOnce_FolderButNoKeys_IsUnpaired()
    {
        var poller = Poller(_relayRoot, null);
        poller.PollOnce();
        Assert.Equal(RelayPollerState.Unpaired, poller.Status.State);
    }

    [Fact]
    public void PollOnce_MissingFolder_IsError()
    {
        var poller = Poller(Path.Combine(_relayRoot, "nope"), Keys);
        poller.PollOnce();
        Assert.Equal(RelayPollerState.Error, poller.Status.State);
        Assert.Contains("does not exist", poller.Status.LastError!);
    }

    [Fact]
    public void PollOnce_ValidDrop_ImportsAcksAndMovesToReceived()
    {
        byte[] kfb = RandomBytes(150_000, 51);
        SimulatePhoneDrop("aaaa", kfb, "camera.kfb");
        var poller = Poller(_relayRoot, Keys);
        poller.PollOnce();

        Assert.Equal(RelayPollerState.Running, poller.Status.State);
        Assert.Equal(1, poller.Status.BundlesImported);
        Assert.Equal(0, poller.Status.BundlesRejected);

        // Inbox is empty; the bundle lives in received/ as a verified pair.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_relayRoot, "inbox", "laptop")));
        string receivedKfb = Path.Combine(_relayRoot, "received", "aaaa.kfb");
        Assert.True(File.Exists(receivedKfb));
        Assert.Equal(kfb, File.ReadAllBytes(receivedKfb));
        Assert.True(File.Exists(receivedKfb + ".par2"));
        Assert.True(File.Exists(Path.Combine(_relayRoot, "received", "aaaa.json")));

        // An ack went to the phone's mailbox.
        RelayIndex phoneIndex = _writer.ReadPhoneIndex();
        Assert.Single(phoneIndex.Messages);
        Assert.Equal(RelayMessageKinds.Ack, phoneIndex.Messages[0].Kind);
    }

    [Fact]
    public void PollOnce_BadSignature_RejectsAndLeavesInPlace()
    {
        SimulatePhoneDrop("bbbb", RandomBytes(1000, 52), "evil.kfb", signCorrectly: false);
        var poller = Poller(_relayRoot, Keys);
        poller.PollOnce();

        Assert.Equal(0, poller.Status.BundlesImported);
        Assert.Equal(1, poller.Status.BundlesRejected);
        Assert.True(File.Exists(Path.Combine(_relayRoot, "inbox", "laptop", "bbbb.kfb")));
        Assert.Empty(_writer.ReadPhoneIndex().Messages); // no ack
    }

    [Fact]
    public void PollOnce_DamagedBundle_RepairsOnImport()
    {
        byte[] kfb = RandomBytes(2 * RelayPar2.BlockSize + 10, 53);
        SimulatePhoneDrop("cccc", kfb, "damaged.kfb");
        string kfbPath = Path.Combine(_relayRoot, "inbox", "laptop", "cccc.kfb");
        byte[] damaged = File.ReadAllBytes(kfbPath);
        damaged[RelayPar2.BlockSize + 7] ^= 0xFF;
        File.WriteAllBytes(kfbPath, damaged);

        var poller = Poller(_relayRoot, Keys);
        poller.PollOnce();

        Assert.Equal(1, poller.Status.BundlesImported);
        Assert.Equal(kfb, File.ReadAllBytes(Path.Combine(_relayRoot, "received", "cccc.kfb")));
    }

    [Fact]
    public void PollOnce_ExpiredDrop_IsPruned()
    {
        _writer.PublishDrop(RandomBytes(1000, 54), "old.kfb", TimeSpan.FromSeconds(-1));
        var poller = Poller(_relayRoot, Keys);
        poller.PollOnce();
        Assert.Equal(1, poller.Status.DropsPruned);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_relayRoot, "drops")));
    }

    [Fact]
    public void PollOnce_ApprovalRequest_IsSurfacedAsPending()
    {
        SimulatePhoneMessage("m1", RelayMessageKinds.ApprovalRequest,
            Encoding.UTF8.GetBytes("{\"challenge\":\"abc\",\"description\":\"Approve restore of X?\"}"));
        var poller = Poller(_relayRoot, Keys);
        poller.PollOnce();

        PendingApproval approval = Assert.Single(poller.Status.PendingApprovals);
        Assert.Equal("m1", approval.Id);
        Assert.Contains("Approve restore", approval.BodyJson);
    }

    private void SimulatePhoneApproval(string id, string nonce, bool approved)
    {
        var body = new ApprovalBody { Nonce = nonce, RefId = "req-1", Approved = approved };
        SimulatePhoneMessage(id, RelayMessageKinds.Approval,
            Encoding.UTF8.GetBytes(RelayJson.Serialize(body, RelayJson.Default, RelayJsonContext.Default.ApprovalBody)));
    }

    [Fact]
    public void PollOnce_PhoneApproval_MatchedToRequest_IsConsumedAndClaimable()
    {
        var poller = Poller(_relayRoot, Keys);
        poller.RecordApprovalRequest("nonce-1", "restore", "Approve restore authorization");
        SimulatePhoneApproval("m2", "nonce-1", approved: true);
        poller.PollOnce();

        ReceivedApproval? taken = poller.TryTakeApproval("nonce-1");
        Assert.NotNull(taken);
        Assert.True(taken.Approved);
        Assert.Equal("restore", taken.What);
        // Consumed: the message is gone from the phone's mailbox.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_relayRoot, "mailbox", "laptop"), "*.bin"));
        // And one approval unlocks one restore — no replay.
        Assert.Null(poller.TryTakeApproval("nonce-1"));
    }

    [Fact]
    public void PollOnce_PhoneApproval_Unmatched_IsLeftInPlace()
    {
        var poller = Poller(_relayRoot, Keys);
        poller.RecordApprovalRequest("nonce-1", "restore", "Approve restore authorization");
        SimulatePhoneApproval("m3", "nonce-other", approved: true);
        poller.PollOnce();

        Assert.Null(poller.TryTakeApproval("nonce-other"));
        Assert.Null(poller.TryTakeApproval("nonce-1"));
        Assert.True(poller.HasPendingApprovalRequest("nonce-1"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_relayRoot, "mailbox", "laptop"), "*.bin"));
    }

    [Fact]
    public void PollOnce_PhoneApproval_Declined_IsClaimableAsDeclined()
    {
        var poller = Poller(_relayRoot, Keys);
        poller.RecordApprovalRequest("nonce-2", "restore", "Approve restore authorization");
        SimulatePhoneApproval("m4", "nonce-2", approved: false);
        poller.PollOnce();

        ReceivedApproval? taken = poller.TryTakeApproval("nonce-2");
        Assert.NotNull(taken);
        Assert.False(taken.Approved);
    }

    [Fact]
    public void TryTakeApproval_WithoutPoll_IsNull()
    {
        var poller = Poller(_relayRoot, Keys);
        poller.RecordApprovalRequest("nonce-3", "restore", "Approve restore authorization");
        Assert.Null(poller.TryTakeApproval("nonce-3"));
        Assert.True(poller.HasPendingApprovalRequest("nonce-3"));
    }

    [Fact]
    public void PollOnce_EmptyRelay_RunsClean()
    {
        var poller = Poller(_relayRoot, Keys);
        poller.PollOnce();
        Assert.Equal(RelayPollerState.Running, poller.Status.State);
        Assert.NotNull(poller.Status.LastPollUtc);
        Assert.Null(poller.Status.LastError);
    }

    [Fact]
    public async Task RunAsync_StopsOnCancellation()
    {
        var poller = Poller(_relayRoot, Keys);
        using var cts = new CancellationTokenSource();
        Task run = poller.RunAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RelayPollerState.Running, poller.Status.State);
    }
}
