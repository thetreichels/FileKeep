using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// CSRF synchronizer-token contract (OWASP pattern): the token is a random
/// per-start secret, compared in fixed time, and any missing or mismatched
/// value is rejected. Every dashboard POST route checks it first; these
/// tests lock the comparison itself.
/// </summary>
public sealed class CsrfTokenTests
{
    [Fact]
    public void MatchingToken_ReturnsTrue()
    {
        Assert.True(DashboardApi.ValidateCsrfToken("abc123", "abc123"));
    }

    [Fact]
    public void MismatchedToken_ReturnsFalse()
    {
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", "abc124"));
    }

    [Fact]
    public void EmptyExpected_ReturnsFalse()
    {
        Assert.False(DashboardApi.ValidateCsrfToken("", "abc123"));
    }

    [Fact]
    public void EmptyProvided_ReturnsFalse()
    {
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", ""));
    }

    [Fact]
    public void NullProvided_ReturnsFalse()
    {
        Assert.False(DashboardApi.ValidateCsrfToken("abc123", null));
    }

    [Fact]
    public void DifferentLength_ReturnsFalse()
    {
        Assert.False(DashboardApi.ValidateCsrfToken("abc", "abcd"));
    }
}
