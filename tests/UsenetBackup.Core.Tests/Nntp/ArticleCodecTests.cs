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
}
