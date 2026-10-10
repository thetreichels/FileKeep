using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests.Nntp;

public sealed class ProviderLimitsTests : IDisposable
{
    private readonly string _dir;

    public ProviderLimitsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void GetMaxArticleBytes_ReturnsNull_WhenNeverProbed()
    {
        var limits = new ProviderLimits(_dir);
        Assert.Null(limits.GetMaxArticleBytes("news.example.com", 119));
    }

    [Fact]
    public void Record_ThenGet_ReturnsCachedValue()
    {
        var limits = new ProviderLimits(_dir);
        limits.Record("news.example.com", 119, 32_000_000);
        Assert.Equal(32_000_000, limits.GetMaxArticleBytes("news.example.com", 119));
    }

    [Fact]
    public void Cache_PersistsAcrossInstances()
    {
        var limits = new ProviderLimits(_dir);
        limits.Record("news.example.com", 563, 64_000_000);
        var reloaded = new ProviderLimits(_dir);
        Assert.Equal(64_000_000, reloaded.GetMaxArticleBytes("news.example.com", 563));
    }

    [Fact]
    public void Cache_IsPerHostAndPort()
    {
        var limits = new ProviderLimits(_dir);
        limits.Record("news.example.com", 119, 10_000_000);
        Assert.Null(limits.GetMaxArticleBytes("news.example.com", 563));
        Assert.Null(limits.GetMaxArticleBytes("other.example.com", 119));
    }

    [Fact]
    public void Cache_IsCaseInsensitiveOnHost()
    {
        var limits = new ProviderLimits(_dir);
        limits.Record("NEWS.EXAMPLE.COM", 119, 10_000_000);
        Assert.Equal(10_000_000, limits.GetMaxArticleBytes("news.example.com", 119));
    }

    [Fact]
    public void EnsureCapacity_ReturnsImmediately_WhenCachedLimitCovers()
    {
        var limits = new ProviderLimits(_dir);
        limits.Record("news.example.com", 119, 50_000_000);
        bool probed = false;
        limits.EnsureCapacity("news.example.com", 119, 30_000_000, _ => { probed = true; return true; });
        Assert.False(probed); // no probe needed
    }

    [Fact]
    public void EnsureCapacity_ProbesAndCaches_WhenNoEntry()
    {
        var limits = new ProviderLimits(_dir);
        limits.EnsureCapacity("news.example.com", 119, 30_000_000, _ => true);
        Assert.Equal(30_000_000, limits.GetMaxArticleBytes("news.example.com", 119));
    }

    [Fact]
    public void EnsureCapacity_Throws_WhenProbeRejected()
    {
        var limits = new ProviderLimits(_dir);
        var ex = Assert.Throws<InvalidDataException>(() =>
            limits.EnsureCapacity("news.example.com", 119, 100_000_000, _ => false));
        Assert.Contains("does not accept", ex.Message);
        Assert.Contains("was not started", ex.Message);
        // Rejection is not cached as a success.
        Assert.Null(limits.GetMaxArticleBytes("news.example.com", 119));
    }

    [Fact]
    public void EnsureCapacity_Throws_WhenProbeThrows()
    {
        var limits = new ProviderLimits(_dir);
        var ex = Assert.Throws<InvalidDataException>(() =>
            limits.EnsureCapacity("news.example.com", 119, 30_000_000,
                _ => throw new TimeoutException("simulated network failure")));
        Assert.Contains("could not probe", ex.Message);
    }

    [Fact]
    public void CorruptCacheFile_DoesNotThrow()
    {
        File.WriteAllText(Path.Combine(_dir, "provider-limits.json"), "not valid json{{{");
        var limits = new ProviderLimits(_dir); // must not throw
        Assert.Null(limits.GetMaxArticleBytes("news.example.com", 119));
    }
}
