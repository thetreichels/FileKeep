using System.Text;
using UsenetBackup.Service.Security;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// RFC 6238 TOTP validation (phone authenticator method). Uses the
/// Appendix B test vectors (SHA-1, secret "12345678901234567890"),
/// truncated to 6 digits as FileKeep does.
/// </summary>
public sealed class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 6238 Appendix B vectors (SHA-1), truncated to 6 digits (mod 10^6).
    // Written as Facts (not Theory) so the sandbox reflection runner executes them.
    [Fact] public void ComputeCode_RfcVector_T59() => AssertVector(59L, "287082");
    [Fact] public void ComputeCode_RfcVector_T1111111109() => AssertVector(1111111109L, "081804");
    [Fact] public void ComputeCode_RfcVector_T1111111111() => AssertVector(1111111111L, "050471");
    [Fact] public void ComputeCode_RfcVector_T1234567890() => AssertVector(1234567890L, "005924");
    [Fact] public void ComputeCode_RfcVector_T2000000000() => AssertVector(2000000000L, "279037");
    [Fact] public void ComputeCode_RfcVector_T20000000000() => AssertVector(20000000000L, "353130");

    private static void AssertVector(long unixTime, string expected)
    {
        long step = unixTime / TotpCrypto.TimeStepSeconds;
        Assert.Equal(expected, TotpCrypto.ComputeCode(RfcSecret, step));
    }

    [Fact]
    public void ValidateCode_AcceptsCurrentStep()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        Assert.True(TotpCrypto.ValidateCode(RfcSecret, "005924", now));
    }

    [Fact]
    public void ValidateCode_AcceptsAdjacentStepsForClockSkew()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        long step = 1234567890L / 30;
        string prev = TotpCrypto.ComputeCode(RfcSecret, step - 1);
        string next = TotpCrypto.ComputeCode(RfcSecret, step + 1);
        Assert.True(TotpCrypto.ValidateCode(RfcSecret, prev, now));
        Assert.True(TotpCrypto.ValidateCode(RfcSecret, next, now));
    }

    [Fact]
    public void ValidateCode_RejectsTwoStepsAway()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        long step = 1234567890L / 30;
        string far = TotpCrypto.ComputeCode(RfcSecret, step + 2);
        string farBack = TotpCrypto.ComputeCode(RfcSecret, step - 2);
        Assert.False(TotpCrypto.ValidateCode(RfcSecret, far, now));
        Assert.False(TotpCrypto.ValidateCode(RfcSecret, farBack, now));
    }

    [Fact]
    public void ValidateCode_RejectsWrongCode()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890L);
        Assert.False(TotpCrypto.ValidateCode(RfcSecret, "000000", now));
        Assert.False(TotpCrypto.ValidateCode(RfcSecret, "123456", now));
    }

    [Fact] public void ValidateCode_RejectsEmpty() =>
        Assert.False(TotpCrypto.ValidateCode(RfcSecret, "", DateTimeOffset.FromUnixTimeSeconds(1234567890L)));
    [Fact] public void ValidateCode_RejectsTooShort() =>
        Assert.False(TotpCrypto.ValidateCode(RfcSecret, "12345", DateTimeOffset.FromUnixTimeSeconds(1234567890L)));
    [Fact] public void ValidateCode_RejectsTooLong() =>
        Assert.False(TotpCrypto.ValidateCode(RfcSecret, "1234567", DateTimeOffset.FromUnixTimeSeconds(1234567890L)));
    [Fact] public void ValidateCode_RejectsNonDigits() =>
        Assert.False(TotpCrypto.ValidateCode(RfcSecret, "abcdef", DateTimeOffset.FromUnixTimeSeconds(1234567890L)));
    [Fact] public void ValidateCode_RejectsInnerSpace() =>
        Assert.False(TotpCrypto.ValidateCode(RfcSecret, "12 456", DateTimeOffset.FromUnixTimeSeconds(1234567890L)));

    [Fact]
    public void ValidateCode_RejectsNullOrEmptySecret()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(TotpCrypto.ValidateCode(null!, "123456", now));
        Assert.False(TotpCrypto.ValidateCode(Array.Empty<byte>(), "123456", now));
    }

    [Fact]
    public void Base32_RoundTrips()
    {
        byte[] secret = TotpCrypto.GenerateSecret();
        Assert.Equal(20, secret.Length);
        string encoded = TotpCrypto.ToBase32(secret);
        Assert.Equal(secret, TotpCrypto.FromBase32(encoded));
        // Lowercase + padding tolerated.
        Assert.Equal(secret, TotpCrypto.FromBase32(encoded.ToLowerInvariant() + "==="));
    }

    [Fact]
    public void Base32_KnownVector()
    {
        // "12345678901234567890" in base32 (RFC 4648).
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpCrypto.ToBase32(RfcSecret));
    }

    [Fact]
    public void BuildOtpAuthUri_HasExpectedShape()
    {
        string uri = TotpCrypto.BuildOtpAuthUri("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains("secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", uri);
        Assert.Contains("issuer=FileKeep", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
    }

    [Fact]
    public void GenerateSecret_ProducesUniqueSecrets()
    {
        var a = TotpCrypto.GenerateSecret();
        var b = TotpCrypto.GenerateSecret();
        Assert.NotEqual(a, b);
    }
}
