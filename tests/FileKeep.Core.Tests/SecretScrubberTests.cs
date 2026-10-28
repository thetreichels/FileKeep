using FileKeep.Core.Security;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// The single scrub function for log/startup/audit output: secret-shaped
/// content must not survive verbatim, while paths, chunk IDs, and
/// ordinary messages stay readable.
/// </summary>
public sealed class SecretScrubberTests
{
    // Realistic fake DPAPI blob: long base64 with mixed case, digits,
    // '+' and '/', padded — deliberately not pure hex.
    private const string FakeBlob =
        "Ab3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/Z" +
        "Ab3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/ZAb3+/Z==";

    private const string CanonicalSecret = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRST"; // 52
    private const string TotpSecret = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; // 32

    [Fact]
    public void FakeDpapiBlob_IsRedacted_RestIntact()
    {
        string input = $"config load failed at C:\\ProgramData\\FileKeep\\service.json: {FakeBlob}";
        string? result = SecretScrubber.Scrub(input);
        Assert.NotNull(result);
        Assert.DoesNotContain(FakeBlob, result);
        Assert.Contains("[REDACTED]", result);
        Assert.Contains(@"C:\ProgramData\FileKeep\service.json", result);
    }

    [Fact]
    public void CanonicalBase32Secret_IsRedacted()
    {
        string? result = SecretScrubber.Scrub($"recovery secret was {CanonicalSecret} here");
        Assert.NotNull(result);
        Assert.DoesNotContain(CanonicalSecret, result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void TotpBase32Secret_IsRedacted()
    {
        string? result = SecretScrubber.Scrub($"totp secret {TotpSecret} leaked");
        Assert.NotNull(result);
        Assert.DoesNotContain(TotpSecret, result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Paths_ArePreserved()
    {
        Assert.Equal(@"C:\ProgramData\FileKeep\service.json",
            SecretScrubber.Scrub(@"C:\ProgramData\FileKeep\service.json"));
        Assert.Equal("/home/user/filekeep/service.json",
            SecretScrubber.Scrub("/home/user/filekeep/service.json"));
        Assert.Equal(@"\\server\share\backup",
            SecretScrubber.Scrub(@"\\server\share\backup"));
    }

    [Fact]
    public void HexChunkIds_ArePreserved()
    {
        string lower = new string('a', 32) + new string('1', 32); // 64 lowercase hex
        string upper = new string('A', 32) + new string('F', 32); // 64 uppercase hex
        Assert.Equal(lower, SecretScrubber.Scrub(lower));
        Assert.Equal(upper, SecretScrubber.Scrub(upper));
    }

    [Fact]
    public void OrdinaryMessage_IsUntouched()
    {
        const string msg = "job 'photos': backup complete (12 files, 3.2 MB)";
        Assert.Equal(msg, SecretScrubber.Scrub(msg));
    }

    [Fact]
    public void NullAndEmpty_PassThrough()
    {
        Assert.Null(SecretScrubber.Scrub(null));
        Assert.Equal("", SecretScrubber.Scrub(""));
    }

    [Fact]
    public void KnownValueOverload_RedactsShortValues_AndSkipsTinyOnes()
    {
        string? result = SecretScrubber.Scrub("password is s3cret-pw ok", "s3cret-pw");
        Assert.NotNull(result);
        Assert.DoesNotContain("s3cret-pw", result);
        // Values under 8 chars are skipped: they are too likely to be
        // ordinary substrings.
        Assert.Equal("password is hunter2 ok",
            SecretScrubber.Scrub("password is hunter2 ok", "hunter2"));
    }
}
