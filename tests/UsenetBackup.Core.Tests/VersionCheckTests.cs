using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

public class VersionCheckTests
{
    [Theory]
    [InlineData("0.08.12", "0.08.13", true)]   // newer patch
    [InlineData("0.08.13", "0.08.12", false)]  // older is not newer
    [InlineData("0.08.12", "0.08.12", false)]  // same is not newer
    [InlineData("0.08.12", "0.09.0", true)]    // newer minor
    [InlineData("0.9.0", "0.08.12", false)]    // 9 > 08 numerically
    [InlineData("1.0.0", "0.99.99", false)]    // major dominates
    public void IsNewerVersion_ComparesNumerically(string current, string latest, bool expected)
    {
        Assert.Equal(expected, DashboardApi.IsNewerVersion(current, latest));
    }

    [Fact]
    public void TryParseVersion_HandlesLeadingZeros()
    {
        Assert.True(DashboardApi.TryParseVersion("0.08.12", out int[] parts));
        Assert.Equal(new[] { 0, 8, 12 }, parts);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.-2.3")]
    public void TryParseVersion_RejectsGarbage(string s)
    {
        Assert.False(DashboardApi.TryParseVersion(s, out _));
    }
}
