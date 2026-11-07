using System.Text;
using FileKeep.Core.Relay;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Relay writer (atomic drop publish, signed index commit, prune) and
/// relay inbox (verify phone writes: bundles + messages).
/// </summary>
public class RelayWriterTests : IDisposable
{
    private readonly string _relayRoot;
    private readonly byte[] _seed = RelayCrypto.NewPairingSecret();
    private readonly byte[] _hmacKey;
    private readonly byte[] _encKey;
    private readonly RelayWriter _writer;
    private readonly RelayInbox _inbox;

    public RelayWriterTests()
    {
        _relayRoot = Path.Combine(Path.GetTempPath(), "fk-relay-" + Guid.NewGuid().ToString("N"));
        _hmacKey = RelayCrypto.DeriveHmacKey(_seed);
        _encKey = RelayCrypto.DeriveEncKey(_seed);
        _writer = new RelayWriter(_relayRoot, _hmacKey, _encKey);
        _inbox = new RelayInbox(_relayRoot, _hmacKey, _encKey, _writer);
    }

    public void Dispose()
    {
        try { Directory.Delete(_relayRoot, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[length];
        rng.NextBytes(data);
        return data;
    }

    [Fact]
    public void PublishDrop_CreatesKfbPar2JsonAndIndexEntry()
    {
        byte[] kfb = RandomBytes(200_000, 5);
        DropPublishResult result = _writer.PublishDrop(kfb, "photos.kfb", TimeSpan.FromDays(1));

        string kfbPath = Path.Combine(_relayRoot, "drops", result.Token + ".kfb");
        string par2Path = kfbPath + ".par2";
        string jsonPath = Path.Combine(_relayRoot, "drops", result.Token + ".json");
        Assert.True(File.Exists(kfbPath));
        Assert.True(File.Exists(par2Path));
        Assert.True(File.Exists(jsonPath));
        Assert.Equal(kfb, File.ReadAllBytes(kfbPath));

        // Par2 describes the written bytes.
        Assert.Equal(Par2VerifyStatus.Valid,
            RelayPar2.Verify(File.ReadAllBytes(kfbPath), File.ReadAllBytes(par2Path)).Status);

        // Drop metadata verifies.
        var meta = RelayJson.Deserialize(File.ReadAllText(jsonPath), RelayJson.Default, RelayJsonContext.Default.RelayDropMetadata);
        Assert.NotNull(meta);
        Assert.Equal(result.Token, meta!.Token);
        Assert.Equal("photos.kfb", meta.Name);
        Assert.Equal(kfb.Length, meta.Size);
        Assert.True(RelayCrypto.VerifyDropHmac(_hmacKey, meta));

        // Index is the commit point: one drop-ready entry, signed.
        RelayIndex index = _writer.ReadPhoneIndex();
        Assert.Equal(1, index.Sequence);
        Assert.Single(index.Messages);
        Assert.Equal(RelayMessageKinds.DropReady, index.Messages[0].Kind);
        Assert.True(RelayCrypto.VerifyIndexHmac(_hmacKey, index));

        // The drop-ready message decrypts to the drop body.
        string binPath = Path.Combine(_relayRoot, "mailbox", "phone", index.Messages[0].Id + ".bin");
        byte[]? plaintext = RelayCrypto.DecryptMessage(_encKey, _hmacKey, index.Messages[0].Id, File.ReadAllBytes(binPath));
        Assert.NotNull(plaintext);
        var body = RelayJson.Deserialize(Encoding.UTF8.GetString(plaintext!), RelayJson.Default, RelayJsonContext.Default.DropReadyBody);
        Assert.NotNull(body);
        Assert.Equal(result.Token, body!.Token);
        Assert.Equal("photos.kfb", body.Name);
    }

    [Fact]
    public void PublishDrop_TwoDrops_SequenceIncrements()
    {
        _writer.PublishDrop(RandomBytes(1000, 1), "a.kfb");
        _writer.PublishDrop(RandomBytes(1000, 2), "b.kfb");
        RelayIndex index = _writer.ReadPhoneIndex();
        Assert.Equal(2, index.Sequence);
        Assert.Equal(2, index.Messages.Count);
        Assert.True(RelayCrypto.VerifyIndexHmac(_hmacKey, index));
    }

    [Fact]
    public void PublishDrop_EmptyBundle_Throws()
    {
        Assert.Throws<ArgumentException>(() => _writer.PublishDrop(Array.Empty<byte>(), "empty.kfb"));
    }

    [Fact]
    public void PublishDrop_NameWithNewline_Throws()
    {
        Assert.Throws<ArgumentException>(() => _writer.PublishDrop(RandomBytes(100, 3), "bad\nname.kfb"));
    }

    [Fact]
    public void WriteMessage_RoundTrip_DecryptsAndIndexes()
    {
        string id = _writer.WriteMessage(RelayMessageKinds.Status, Encoding.UTF8.GetBytes("{\"ok\":true}"));
        RelayIndex index = _writer.ReadPhoneIndex();
        Assert.Equal(1, index.Sequence);
        Assert.Equal(id, index.Messages[0].Id);
        Assert.Equal(RelayMessageKinds.Status, index.Messages[0].Kind);
        byte[]? plaintext = RelayCrypto.DecryptMessage(_encKey, _hmacKey, id,
            File.ReadAllBytes(Path.Combine(_relayRoot, "mailbox", "phone", id + ".bin")));
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(plaintext!));
    }

    [Fact]
    public void ReadPhoneIndex_Missing_ReturnsEmpty()
    {
        string fresh = Path.Combine(Path.GetTempPath(), "fk-relay-fresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new RelayWriter(fresh, _hmacKey, _encKey);
            RelayIndex index = writer.ReadPhoneIndex();
            Assert.Equal(0, index.Sequence);
            Assert.Empty(index.Messages);
        }
        finally
        {
            try { Directory.Delete(fresh, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ReadPhoneIndex_Tampered_Throws()
    {
        _writer.WriteMessage(RelayMessageKinds.Status, Encoding.UTF8.GetBytes("{}"));
        string indexPath = Path.Combine(_relayRoot, "mailbox", "phone", "index.json");
        string json = File.ReadAllText(indexPath);
        File.WriteAllText(indexPath, json.Replace("\"sequence\": 1", "\"sequence\": 2"));
        Assert.Throws<InvalidDataException>(() => _writer.ReadPhoneIndex());
    }

    [Fact]
    public void PruneExpiredDrops_DeletesExpiredAndKeepsLive()
    {
        DropPublishResult expired = _writer.PublishDrop(RandomBytes(1000, 9), "old.kfb", TimeSpan.FromSeconds(-1));
        DropPublishResult live = _writer.PublishDrop(RandomBytes(1000, 10), "new.kfb", TimeSpan.FromDays(1));

        int deleted = _writer.PruneExpiredDrops(DateTimeOffset.UtcNow);
        Assert.Equal(1, deleted); // one drop (kfb + par2 + json)
        Assert.False(File.Exists(Path.Combine(_relayRoot, "drops", expired.Token + ".kfb")));
        Assert.True(File.Exists(Path.Combine(_relayRoot, "drops", live.Token + ".kfb")));
    }

    [Fact]
    public void PruneExpiredDrops_DeletesOrphanedKfb()
    {
        string orphan = Path.Combine(_relayRoot, "drops", "deadbeef.kfb");
        File.WriteAllBytes(orphan, RandomBytes(500, 12));
        File.SetLastWriteTimeUtc(orphan, DateTimeOffset.UtcNow.AddHours(-2).UtcDateTime);
        int deleted = _writer.PruneExpiredDrops(DateTimeOffset.UtcNow);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void PruneExpiredDrops_KeepsFreshOrphan()
    {
        // A kfb with no metadata younger than an hour may be mid-publish.
        string orphan = Path.Combine(_relayRoot, "drops", "deadbeef.kfb");
        File.WriteAllBytes(orphan, RandomBytes(500, 12));
        int deleted = _writer.PruneExpiredDrops(DateTimeOffset.UtcNow);
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(orphan));
    }

    // ---- Inbox: phone-dropped bundles ----

    private void SimulatePhoneDrop(string token, byte[] kfb, string name, bool signCorrectly = true, bool includePar2 = true)
    {
        string inboxDir = Path.Combine(_relayRoot, "inbox", "laptop");
        File.WriteAllBytes(Path.Combine(inboxDir, token + ".kfb"), kfb);
        if (includePar2)
            File.WriteAllBytes(Path.Combine(inboxDir, token + ".kfb.par2"), RelayPar2.Generate(kfb));
        var meta = new RelayInboxMetadata
        {
            Token = token,
            Name = name,
            Size = kfb.Length,
            CreatedUtc = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero),
            Hmac = signCorrectly
                ? RelayCrypto.ComputeInboxHmac(_hmacKey, token, name, kfb.Length, new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero))
                : "00",
        };
        File.WriteAllText(Path.Combine(inboxDir, token + ".json"),
            RelayJson.Serialize(meta, RelayJson.Indented, RelayJsonContext.Default.RelayInboxMetadata));
    }

    [Fact]
    public void ScanBundles_ValidPhoneDrop_IsImportable()
    {
        byte[] kfb = RandomBytes(150_000, 21);
        SimulatePhoneDrop("aaaa", kfb, "camera.kfb");
        IReadOnlyList<InboxBundle> found = _inbox.ScanBundles();
        Assert.Single(found);
        InboxBundle item = found[0];
        Assert.True(item.SignatureValid);
        Assert.Equal(InboxPar2Status.Valid, item.Par2Status);
        Assert.Equal("camera.kfb", item.Name);
        Assert.Equal(kfb.Length, item.Size);
        Assert.Null(item.RejectionReason);
    }

    [Fact]
    public void ScanBundles_BadSignature_IsRejected()
    {
        SimulatePhoneDrop("bbbb", RandomBytes(1000, 22), "evil.kfb", signCorrectly: false);
        InboxBundle item = Assert.Single(_inbox.ScanBundles());
        Assert.False(item.SignatureValid);
        Assert.NotNull(item.RejectionReason);
    }

    [Fact]
    public void ScanBundles_MissingMetadata_IsRejected()
    {
        string inboxDir = Path.Combine(_relayRoot, "inbox", "laptop");
        File.WriteAllBytes(Path.Combine(inboxDir, "cccc.kfb"), RandomBytes(1000, 23));
        InboxBundle item = Assert.Single(_inbox.ScanBundles());
        Assert.False(item.SignatureValid);
        Assert.Contains("metadata", item.RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanBundles_MissingPar2_StillImportable()
    {
        SimulatePhoneDrop("dddd", RandomBytes(1000, 24), "nopar2.kfb", includePar2: false);
        InboxBundle item = Assert.Single(_inbox.ScanBundles());
        Assert.True(item.SignatureValid);
        Assert.Equal(InboxPar2Status.Missing, item.Par2Status);
    }

    [Fact]
    public void ScanBundles_DamagedKfb_ReportsDamagedBlocks()
    {
        byte[] kfb = RandomBytes(2 * RelayPar2.BlockSize + 10, 25);
        SimulatePhoneDrop("eeee", kfb, "damaged.kfb");
        string kfbPath = Path.Combine(_relayRoot, "inbox", "laptop", "eeee.kfb");
        byte[] damaged = File.ReadAllBytes(kfbPath);
        damaged[RelayPar2.BlockSize + 7] ^= 0xFF;
        File.WriteAllBytes(kfbPath, damaged);

        InboxBundle item = Assert.Single(_inbox.ScanBundles());
        Assert.True(item.SignatureValid); // signature is over metadata, not content
        Assert.Equal(InboxPar2Status.Damaged, item.Par2Status);
        Assert.Equal(new[] { 1 }, item.BadDataBlocks);

        // And the damage is repairable from the par2.
        byte[]? repaired = RelayPar2.Repair(damaged, File.ReadAllBytes(kfbPath + ".par2"));
        Assert.Equal(kfb, repaired);
    }

    [Fact]
    public void ScanBundles_EmptyInbox_ReturnsEmpty()
    {
        Assert.Empty(_inbox.ScanBundles());
    }

    [Fact]
    public void Acknowledge_WritesAckMessageToPhoneMailbox()
    {
        string ackId = _inbox.Acknowledge("aaaa", "imported");
        RelayIndex index = _writer.ReadPhoneIndex();
        Assert.Single(index.Messages);
        Assert.Equal(RelayMessageKinds.Ack, index.Messages[0].Kind);
        Assert.Equal(ackId, index.Messages[0].Id);
        byte[]? plaintext = RelayCrypto.DecryptMessage(_encKey, _hmacKey, ackId,
            File.ReadAllBytes(Path.Combine(_relayRoot, "mailbox", "phone", ackId + ".bin")));
        var body = RelayJson.Deserialize(Encoding.UTF8.GetString(plaintext!), RelayJson.Default, RelayJsonContext.Default.AckBody);
        Assert.Equal("aaaa", body!.RefId);
        Assert.Equal("imported", body.Status);
    }

    [Fact]
    public void DeleteBundle_RemovesAllCompanions()
    {
        SimulatePhoneDrop("ffff", RandomBytes(1000, 26), "gone.kfb");
        Assert.Single(_inbox.ScanBundles());
        _inbox.DeleteBundle("ffff");
        Assert.Empty(_inbox.ScanBundles());
    }

    // ---- Inbox: phone mailbox messages ----

    private void SimulatePhoneMessage(string id, string kind, byte[] plaintext)
    {
        string mailboxDir = Path.Combine(_relayRoot, "mailbox", "laptop");
        byte[] bin = RelayCrypto.EncryptMessage(_encKey, _hmacKey, id, plaintext);
        File.WriteAllBytes(Path.Combine(mailboxDir, id + ".bin"), bin);
        // Write a signed index for the laptop mailbox by hand.
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
    public void ReadLaptopMessages_RoundTrip_Decrypts()
    {
        SimulatePhoneMessage("m1", RelayMessageKinds.Approval, Encoding.UTF8.GetBytes("{\"approved\":true}"));
        IReadOnlyList<PeerMessage> messages = _inbox.ReadLaptopMessages();
        PeerMessage msg = Assert.Single(messages);
        Assert.Null(msg.FailureReason);
        Assert.Equal("{\"approved\":true}", Encoding.UTF8.GetString(msg.Plaintext!));
    }

    [Fact]
    public void ReadLaptopMessages_TamperedBin_ReportsFailure()
    {
        SimulatePhoneMessage("m1", RelayMessageKinds.Approval, Encoding.UTF8.GetBytes("{}"));
        string binPath = Path.Combine(_relayRoot, "mailbox", "laptop", "m1.bin");
        byte[] bin = File.ReadAllBytes(binPath);
        bin[20] ^= 0xFF;
        File.WriteAllBytes(binPath, bin);
        PeerMessage msg = Assert.Single(_inbox.ReadLaptopMessages());
        Assert.NotNull(msg.FailureReason);
        Assert.Null(msg.Plaintext);
    }

    [Fact]
    public void ReadLaptopMessages_TamperedIndex_Throws()
    {
        SimulatePhoneMessage("m1", RelayMessageKinds.Approval, Encoding.UTF8.GetBytes("{}"));
        string indexPath = Path.Combine(_relayRoot, "mailbox", "laptop", "index.json");
        string json = File.ReadAllText(indexPath);
        File.WriteAllText(indexPath, json.Replace("\"sequence\": 1", "\"sequence\": 9"));
        Assert.Throws<InvalidDataException>(() => _inbox.ReadLaptopMessages());
    }

    [Fact]
    public void ReadLaptopMessages_NoIndex_ReturnsEmpty()
    {
        Assert.Empty(_inbox.ReadLaptopMessages());
    }
}
