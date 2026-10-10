using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

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

        string article = ArticleCodec.BuildArticle(chunkId, repoId, blob, "alt.binaries.test", "usenet-backup");
        Assert.Contains($"Message-ID: <{chunkId}.{repoId}@usenet-backup>", article);
        Assert.Contains($"X-UsenetBackup-Chunk: {chunkId}", article);

        var (id, back) = ArticleCodec.ParseArticle(article);
        Assert.Equal(chunkId, id);
        Assert.Equal(blob, back);
    }

    [Fact]
    public void MakeMessageId_Format()
    {
        string chunkId = new('a', 64);
        Assert.Equal("<" + chunkId + ".0123456789abcdef@usenet-backup>",
            ArticleCodec.MakeMessageId(chunkId, "0123456789abcdef"));
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

    [Fact]
    public void Parse_CorruptedBody_Throws()
    {
        byte[] blob = RandomBytes(1000);
        string chunkId = ChunkIdOf(blob);
        string article = ArticleCodec.BuildArticle(chunkId, "0123456789abcdef", blob, "alt.binaries.test", "x");
        // Corrupt the first character of actual yEnc data (line after =ybegin).
        string[] lines = article.Split(new[] { "\r\n" }, StringSplitOptions.None);
        int begin = Array.FindIndex(lines, l => l.StartsWith("=ybegin", StringComparison.Ordinal));
        char[] chars = lines[begin + 1].ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        lines[begin + 1] = new string(chars);
        Assert.Throws<InvalidDataException>(() => ArticleCodec.ParseArticle(string.Join("\r\n", lines)));
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

        string article = ArticleCodec.BuildVolumeArticle(volumeId, repoId, volumeBytes, "alt.binaries.test", "usenet-backup");
        Assert.Contains($"Message-ID: <{volumeId}.{repoId}@usenet-backup>", article);
        Assert.Contains($"X-UsenetBackup-Volume: {volumeId}", article);
        Assert.DoesNotContain("X-UsenetBackup-Chunk", article);

        var (id, back) = ArticleCodec.ParseVolumeArticle(article);
        Assert.Equal(volumeId, id);
        Assert.Equal(volumeBytes, back);
    }

    [Fact]
    public void MakeVolumeMessageId_Format()
    {
        string volumeId = new('c', 64);
        Assert.Equal("<" + volumeId + ".0123456789abcdef@usenet-backup>",
            ArticleCodec.MakeVolumeMessageId(volumeId, "0123456789abcdef"));
    }

    [Fact]
    public void MakeRefreshVolumeMessageId_Format()
    {
        string volumeId = new('c', 64);
        string msgId = ArticleCodec.MakeRefreshVolumeMessageId(volumeId, "0123456789abcdef");
        Assert.StartsWith($"<{volumeId}.0123456789abcdef.refresh.", msgId, StringComparison.Ordinal);
        Assert.EndsWith("@usenet-backup>", msgId, StringComparison.Ordinal);
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
        string volumeArticle = ArticleCodec.BuildVolumeArticle(volumeId, "0123456789abcdef", volumeBytes, "alt.binaries.test", "x");
        Assert.Throws<InvalidDataException>(() => ArticleCodec.ParseArticle(volumeArticle));
    }
}
