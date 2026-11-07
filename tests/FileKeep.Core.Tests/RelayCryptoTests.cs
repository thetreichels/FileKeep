using System.Text;
using FileKeep.Core.Relay;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Relay crypto: key derivation, index/drop HMACs, message envelopes.
/// </summary>
public class RelayCryptoTests
{
    private static readonly byte[] Seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void DeriveKeys_AreDistinctAndDeterministic()
    {
        byte[] hmac1 = RelayCrypto.DeriveHmacKey(Seed);
        byte[] hmac2 = RelayCrypto.DeriveHmacKey(Seed);
        byte[] enc = RelayCrypto.DeriveEncKey(Seed);
        Assert.Equal(hmac1, hmac2);
        Assert.NotEqual(hmac1, enc);
        Assert.Equal(32, hmac1.Length);
        Assert.Equal(32, enc.Length);
    }

    [Fact]
    public void DeriveKeys_WrongSeedLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => RelayCrypto.DeriveHmacKey(new byte[16]));
        Assert.Throws<ArgumentException>(() => RelayCrypto.DeriveEncKey(new byte[31]));
    }

    [Fact]
    public void NewPairingSecret_Is32RandomBytes()
    {
        byte[] a = RelayCrypto.NewPairingSecret();
        byte[] b = RelayCrypto.NewPairingSecret();
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void IndexHmac_RoundTrip_Verifies()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        var messages = new List<RelayIndexEntry>
        {
            new() { Id = "aaa", Kind = RelayMessageKinds.DropReady, CreatedUtc = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), Size = 123 },
            new() { Id = "bbb", Kind = RelayMessageKinds.Ack, CreatedUtc = new DateTimeOffset(2026, 11, 4, 12, 0, 5, TimeSpan.Zero), Size = 456 },
        };
        var index = new RelayIndex { Sequence = 7, Messages = messages };
        index.Hmac = RelayCrypto.ComputeIndexHmac(hmacKey, index.Sequence, index.Messages);
        Assert.True(RelayCrypto.VerifyIndexHmac(hmacKey, index));
    }

    [Fact]
    public void IndexHmac_TamperedSequence_Fails()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        var index = new RelayIndex
        {
            Sequence = 7,
            Messages = new List<RelayIndexEntry>
            {
                new() { Id = "aaa", Kind = RelayMessageKinds.Status, CreatedUtc = DateTimeOffset.UtcNow, Size = 10 },
            },
        };
        index.Hmac = RelayCrypto.ComputeIndexHmac(hmacKey, index.Sequence, index.Messages);
        index.Sequence = 8;
        Assert.False(RelayCrypto.VerifyIndexHmac(hmacKey, index));
    }

    [Fact]
    public void IndexHmac_TamperedMessage_Fails()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        var index = new RelayIndex
        {
            Sequence = 1,
            Messages = new List<RelayIndexEntry>
            {
                new() { Id = "aaa", Kind = RelayMessageKinds.Status, CreatedUtc = DateTimeOffset.UtcNow, Size = 10 },
            },
        };
        index.Hmac = RelayCrypto.ComputeIndexHmac(hmacKey, index.Sequence, index.Messages);
        index.Messages[0].Size = 11;
        Assert.False(RelayCrypto.VerifyIndexHmac(hmacKey, index));
    }

    [Fact]
    public void IndexHmac_WrongKey_Fails()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        byte[] otherKey = RelayCrypto.DeriveHmacKey(RelayCrypto.NewPairingSecret());
        var index = new RelayIndex { Sequence = 1, Messages = new() };
        index.Hmac = RelayCrypto.ComputeIndexHmac(hmacKey, index.Sequence, index.Messages);
        Assert.False(RelayCrypto.VerifyIndexHmac(otherKey, index));
    }

    [Fact]
    public void IndexHmac_MissingHmac_Fails()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        var index = new RelayIndex { Sequence = 1, Messages = new(), Hmac = null };
        Assert.False(RelayCrypto.VerifyIndexHmac(hmacKey, index));
    }

    [Fact]
    public void DropHmac_RoundTrip_Verifies()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        var meta = new RelayDropMetadata
        {
            Token = "abc123",
            Name = "photos.kfb",
            Size = 999,
            ExpiryUtc = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero),
        };
        meta.Hmac = RelayCrypto.ComputeDropHmac(hmacKey, meta.Token, meta.Name, meta.Size, meta.ExpiryUtc);
        Assert.True(RelayCrypto.VerifyDropHmac(hmacKey, meta));
    }

    [Fact]
    public void DropHmac_TamperedName_Fails()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        var meta = new RelayDropMetadata
        {
            Token = "abc123",
            Name = "photos.kfb",
            Size = 999,
            ExpiryUtc = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero),
        };
        meta.Hmac = RelayCrypto.ComputeDropHmac(hmacKey, meta.Token, meta.Name, meta.Size, meta.ExpiryUtc);
        meta.Name = "photos2.kfb";
        Assert.False(RelayCrypto.VerifyDropHmac(hmacKey, meta));
    }

    [Fact]
    public void DropCanonicalString_NameWithNewline_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RelayCrypto.DropCanonicalString("t", "bad\nname", 1, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Message_RoundTrip_Decrypts()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        byte[] encKey = RelayCrypto.DeriveEncKey(Seed);
        byte[] plaintext = Encoding.UTF8.GetBytes("{\"token\":\"abc\"}");
        byte[] bin = RelayCrypto.EncryptMessage(encKey, hmacKey, "msg-1", plaintext);
        byte[]? decrypted = RelayCrypto.DecryptMessage(encKey, hmacKey, "msg-1", bin);
        Assert.NotNull(decrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Message_TamperedCiphertext_ReturnsNull()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        byte[] encKey = RelayCrypto.DeriveEncKey(Seed);
        byte[] bin = RelayCrypto.EncryptMessage(encKey, hmacKey, "msg-1", Encoding.UTF8.GetBytes("hello"));
        bin[20] ^= 0xFF; // inside ciphertext
        Assert.Null(RelayCrypto.DecryptMessage(encKey, hmacKey, "msg-1", bin));
    }

    [Fact]
    public void Message_TamperedHmac_ReturnsNull()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        byte[] encKey = RelayCrypto.DeriveEncKey(Seed);
        byte[] bin = RelayCrypto.EncryptMessage(encKey, hmacKey, "msg-1", Encoding.UTF8.GetBytes("hello"));
        bin[^1] ^= 0xFF; // inside trailing HMAC
        Assert.Null(RelayCrypto.DecryptMessage(encKey, hmacKey, "msg-1", bin));
    }

    [Fact]
    public void Message_WrongId_ReturnsNull()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        byte[] encKey = RelayCrypto.DeriveEncKey(Seed);
        byte[] bin = RelayCrypto.EncryptMessage(encKey, hmacKey, "msg-1", Encoding.UTF8.GetBytes("hello"));
        Assert.Null(RelayCrypto.DecryptMessage(encKey, hmacKey, "msg-2", bin));
    }

    [Fact]
    public void Message_WrongKey_ReturnsNull()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        byte[] encKey = RelayCrypto.DeriveEncKey(Seed);
        byte[] otherHmac = RelayCrypto.DeriveHmacKey(RelayCrypto.NewPairingSecret());
        byte[] bin = RelayCrypto.EncryptMessage(encKey, hmacKey, "msg-1", Encoding.UTF8.GetBytes("hello"));
        Assert.Null(RelayCrypto.DecryptMessage(encKey, otherHmac, "msg-1", bin));
    }

    [Fact]
    public void Message_TruncatedBin_ReturnsNull()
    {
        byte[] hmacKey = RelayCrypto.DeriveHmacKey(Seed);
        byte[] encKey = RelayCrypto.DeriveEncKey(Seed);
        Assert.Null(RelayCrypto.DecryptMessage(encKey, hmacKey, "msg-1", new byte[10]));
    }
}
