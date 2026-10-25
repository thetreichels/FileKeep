using FileKeep.Core.Nntp;
using Xunit;

namespace FileKeep.Core.Tests.Nntp;

public sealed class ArticleCodecTests
{
    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    private static string ChunkIdOf(byte[] b) => Hashing.Sha256Hex(b);

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(100000)]
    public void RoundTrip_PreservesBlob(int size)
    {
        byte[] blob = RandomBytes(size);
        string chunkId = ChunkIdOf(blob);
        const string repoId = "0123456789abcdef";

        string article = ArticleCodec.BuildArticle(chunkId, repoId, blob, "alt.binaries.test", "poster");
        Assert.Contains($"Message-ID: <{chunkId}.{repoId}@invalid>", article);
        Assert.Contains($"X-Block: {chunkId}", article);

        var (id, back) = ArticleCodec.ParseArticle(article);
        Assert.Equal(chunkId, id);
        Assert.Equal(blob, back);
    }

    [Fact]
    public void DeriveRepoId_IsStableAndHex()
    {
        byte[] salt = RandomBytes(32);
        string a = ArticleCodec.DeriveRepoId(salt);
        string b = ArticleCodec.DeriveRepoId(salt);
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
        Assert.True(a.All(Uri.IsHexDigit));
        Assert.NotEqual(a, ArticleCodec.DeriveRepoId(RandomBytes(32)));
    }

    [Fact]
    public void Parse_MissingChunkHeader_Throws()
    {
        const string article = "Subject: hi\r\n\r\nbody";
        Assert.Throws<InvalidDataException>(() => ArticleCodec.ParseArticle(article));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(100000)]
    public void VolumeArticle_RoundTrip_PreservesBytes(int size)
    {
        byte[] volumeBytes = RandomBytes(size);
        string volumeId = ChunkIdOf(volumeBytes);
        const string repoId = "0123456789abcdef";

        string article = ArticleCodec.BuildVolumeArticle(volumeId, repoId, volumeBytes, "alt.binaries.test", "poster", "a1b2c3d4", 2, 4);
        Assert.Contains($"Message-ID: <{volumeId}.{repoId}@invalid>", article);
        Assert.Contains($"X-Part: {volumeId}", article);
        Assert.DoesNotContain("X-Block", article);

        var (id, back) = ArticleCodec.ParseVolumeArticle(article);
        Assert.Equal(volumeId, id);
        Assert.Equal(volumeBytes, back);
    }

    [Fact]
    public void MakeRefreshVolumeMessageId_Format()
    {
        string volumeId = new('c', 64);
        string msgId = ArticleCodec.MakeRefreshVolumeMessageId(volumeId, "0123456789abcdef");
        Assert.StartsWith($"<{volumeId}.0123456789abcdef.re.", msgId, StringComparison.Ordinal);
        Assert.EndsWith("@invalid>", msgId, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseVolumeArticle_ChunkArticle_Throws()
    {
        byte[] blob = RandomBytes(100);
        string chunkId = ChunkIdOf(blob);
        string chunkArticle = ArticleCodec.BuildArticle(chunkId, "0123456789abcdef", blob, "alt.binaries.test", "x");
        Assert.Throws<InvalidDataException>(() => ArticleCodec.ParseVolumeArticle(chunkArticle));
    }

    [Fact]
    public void ParseArticle_VolumeArticle_Throws()
    {
        byte[] volumeBytes = RandomBytes(100);
        string volumeId = ChunkIdOf(volumeBytes);
        string volumeArticle = ArticleCodec.BuildVolumeArticle(volumeId, "0123456789abcdef", volumeBytes, "alt.binaries.test", "x", "a1b2c3d4", 2, 4);
        Assert.Throws<InvalidDataException>(() => ArticleCodec.ParseArticle(volumeArticle));
    }

    [Theory]
    [InlineData("poster", "poster <poster@invalid>")]
    [InlineData("", "poster <poster@invalid>")]
    [InlineData("alice", "alice <alice@invalid>")]
    [InlineData("alice@example.com", "alice@example.com")]
    [InlineData("Bob <bob@example.org>", "Bob <bob@example.org>")]
    public void FormatFrom_NeutralMailbox(string input, string expected)
    {
        Assert.Equal(expected, ArticleCodec.FormatFrom(input));
        // Never advertises the software in the default path.
        Assert.DoesNotContain("FileKeep", ArticleCodec.FormatFrom(input),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildArticle_HasDateAndValidFrom()
    {
        byte[] blob = RandomBytes(100);
        string chunkId = ChunkIdOf(blob);
        string article = ArticleCodec.BuildArticle(chunkId, "0123456789abcdef", blob, "alt.binaries.test", "poster");
        Assert.Contains("From: poster <poster@invalid>\r\n", article);
        Assert.Matches(@"(?m)^Date: \w{3}, \d{2} \w{3} \d{4} \d{2}:\d{2}:\d{2} GMT\r$", article);
        Assert.DoesNotContain("FileKeep", article, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildVolumeArticle_HasDateAndValidFrom()
    {
        byte[] volumeBytes = RandomBytes(100);
        string volumeId = ChunkIdOf(volumeBytes);
        string article = ArticleCodec.BuildVolumeArticle(volumeId, "0123456789abcdef", volumeBytes,
            "alt.binaries.test", "poster", "a1b2c3d4", 2, 4);
        Assert.Contains("From: poster <poster@invalid>\r\n", article);
        Assert.Contains("\r\nDate: ", article);
        Assert.DoesNotContain("FileKeep", article, StringComparison.OrdinalIgnoreCase);
    }

}
