using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// Tests for the v2 neutral post naming: articles must read as ordinary
/// multi-part binary posts with no software branding in any viewable name.
/// </summary>
public sealed class NeutralNamingTests
{
    [Fact]
    public void SetName_DerivesFromBackupId()
    {
        Assert.Equal("9f2c41bd", ArticleCodec.SetName("9f2c41bd7e03a855c1d6942f6b0e3f1a"));
    }

    [Fact]
    public void SetName_SanitizesNonHex()
    {
        Assert.Equal("20261010", ArticleCodec.SetName("20261010-voltest"));
    }

    [Fact]
    public void RarPartName_FirstIsRar_ThenNumbered()
    {
        Assert.Equal("a1b2c3d4.rar", ArticleCodec.RarPartName("a1b2c3d4", 0));
        Assert.Equal("a1b2c3d4.r00", ArticleCodec.RarPartName("a1b2c3d4", 1));
        Assert.Equal("a1b2c3d4.r01", ArticleCodec.RarPartName("a1b2c3d4", 2));
    }

    [Fact]
    public void PartSubject_StandardFormat()
    {
        Assert.Equal("\"a1b2c3d4.rar\" [002/121] - yEnc",
            ArticleCodec.PartSubject("a1b2c3d4.rar", 2, 121));
    }

    [Fact]
    public void VolumeArticle_UsesNeutralNaming()
    {
        string volumeId = new('v', 64);
        byte[] bytes = new byte[64];
        string article = ArticleCodec.BuildVolumeArticle(
            volumeId, "0123456789abcdef", bytes, "alt.binaries.test", "poster-x",
            "a1b2c3d4", 2, 121);

        Assert.Contains("From: poster-x <poster-x@invalid>\r\n", article);
        Assert.Contains("Subject: \"a1b2c3d4.rar\" [002/121] - yEnc\r\n", article);
        Assert.DoesNotContain("usenet-backup] ", article);
        Assert.DoesNotContain("volume-", article);
        // yEnc filename is the RAR part name
        Assert.Contains("name=a1b2c3d4.rar", article);
    }

    [Fact]
    public void NfoText_HasNoBrandingOrLinks()
    {
        string nfo = ArticleCodec.BuildNfoText(
            "9f2c41bd7e03a855c1d6942f6b0e3f1a", "2026-10-14", 120, "poster-x");

        Assert.Contains("Backup ID : 9f2c41bd7e03a855c1d6942f6b0e3f1a", nfo);
        Assert.Contains("These articles contain private archival data generated", nfo);
        Assert.Contains("under GPL-3.0-only. Their contents are encoded for", nfo);
        Assert.Contains("authorized restoration only.", nfo);
        Assert.DoesNotContain("FileKeep", nfo);
        Assert.DoesNotContain("filekeep", nfo);
        Assert.DoesNotContain("github", nfo, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", nfo, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NfoMessageId_DeterministicAndOpaque()
    {
        string id1 = ArticleCodec.MakeNfoMessageId("backup1", "repo1");
        string id2 = ArticleCodec.MakeNfoMessageId("backup1", "repo1");
        string id3 = ArticleCodec.MakeNfoMessageId("backup2", "repo1");
        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.DoesNotContain("nfo", id1.Split('@')[0], System.StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("@invalid>", id1);
    }

    [Fact]
    public void NfoArticle_RoundTrip()
    {
        string backupId = "9f2c41bd7e03a855c1d6942f6b0e3f1a";
        string nfo = ArticleCodec.BuildNfoText(backupId, "2026-10-14", 3, "poster-x");
        string article = ArticleCodec.BuildNfoArticle(
            nfo, backupId, "0123456789abcdef", "alt.binaries.test", "poster-x", "9f2c41bd", 4);

        Assert.Contains("Subject: \"9f2c41bd.nfo\" [001/004] - yEnc\r\n", article);
        Assert.DoesNotContain("X-UsenetBackup-Nfo", article);

        string parsed = ArticleCodec.ParseNfoArticle(article, backupId);
        Assert.Equal(nfo, parsed);
    }

    [Fact]
    public void NfoArticle_WrongBackupId_Throws()
    {
        string nfo = ArticleCodec.BuildNfoText("aaaa", "2026-10-14", 1, "poster-x");
        string article = ArticleCodec.BuildNfoArticle(
            nfo, "aaaa", "0123456789abcdef", "alt.binaries.test", "poster-x", "aaaa", 2);
        Assert.Throws<System.IO.InvalidDataException>(
            () => ArticleCodec.ParseNfoArticle(article, "bbbb"));
    }

    [Fact]
    public void ProbeArticle_HasNoBranding()
    {
        string article = ArticleCodec.BuildProbeArticle(
            new byte[16], "alt.binaries.test", "poster-x", out string messageId);

        Assert.Contains("From: poster-x <poster-x@invalid>\r\n", article);
        Assert.DoesNotContain("[usenet-backup]", article);
        Assert.DoesNotContain("probe-", article);
        Assert.DoesNotContain("X-UsenetBackup-Probe", article);
        Assert.StartsWith("<", messageId);
        Assert.EndsWith("@invalid>", messageId);
    }
}
