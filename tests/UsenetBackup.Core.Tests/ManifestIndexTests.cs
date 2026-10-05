using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Tests for the versioned monthly manifest index.
/// </summary>
public sealed class ManifestIndexTests
{
    [Fact]
    public void MakeManifestIndexMessageId_FormatsCorrectly()
    {
        string msgId = ArticleCodec.MakeManifestIndexMessageId("abc123", "2026-10", 1);
        Assert.Equal("<2026-10.abc123.index.1@usenet-backup>", msgId);
    }

    [Fact]
    public void MakeManifestIndexMessageId_IncrementsVersion()
    {
        string v1 = ArticleCodec.MakeManifestIndexMessageId("abc123", "2026-10", 1);
        string v2 = ArticleCodec.MakeManifestIndexMessageId("abc123", "2026-10", 2);
        Assert.NotEqual(v1, v2);
        Assert.Contains(".index.2@", v2);
    }

    [Fact]
    public void TryParseManifestIndexMessageId_ParsesValid()
    {
        var result = ArticleCodec.TryParseManifestIndexMessageId(
            "<2026-10.abc123.index.1@usenet-backup>", "abc123");
        Assert.NotNull(result);
        Assert.Equal("2026-10", result.Value.YearMonth);
        Assert.Equal(1, result.Value.Version);
    }

    [Fact]
    public void TryParseManifestIndexMessageId_RejectsWrongRepoId()
    {
        var result = ArticleCodec.TryParseManifestIndexMessageId(
            "<2026-10.abc123.index.1@usenet-backup>", "different-repo");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseManifestIndexMessageId_RejectsInvalid()
    {
        // Not an index message
        Assert.Null(ArticleCodec.TryParseManifestIndexMessageId(
            "<not-an-index>", "abc123"));
        // Manifest (not index) message ID
        Assert.Null(ArticleCodec.TryParseManifestIndexMessageId(
            "<backup123@usenet-backup>", "abc123"));
    }

    [Fact]
    public void TryParseManifestIndexMessageId_HandlesMultiDigitVersion()
    {
        var result = ArticleCodec.TryParseManifestIndexMessageId(
            "<2026-10.abc123.index.42@usenet-backup>", "abc123");
        Assert.NotNull(result);
        Assert.Equal(42, result.Value.Version);
    }
}
