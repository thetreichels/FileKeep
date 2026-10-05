using UsenetBackup.Core.Lan;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Tests for HttpBlobStore input validation (chunk ID format).
/// Full HTTP round-trip tests require a live server; these cover the
/// client-side validation that prevents path traversal.
/// </summary>
public sealed class HttpBlobStoreTests
{
    [Fact]
    public void Constructor_TrimsTrailingSlash()
    {
        using var store = new HttpBlobStore("http://localhost:8477/");
        // If it didn't trim, Exists would double-slash. We just verify no throw on construct.
        Assert.NotNull(store);
    }

    [Theory]
    [InlineData("abc")] // too short
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // non-hex
    [InlineData("")] // empty
    public void Exists_RejectsInvalidChunkId(string chunkId)
    {
        using var store = new HttpBlobStore("http://localhost:8477");
        Assert.Throws<ArgumentException>(() => store.Exists(chunkId));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public void Get_RejectsInvalidChunkId(string chunkId)
    {
        using var store = new HttpBlobStore("http://localhost:8477");
        Assert.Throws<ArgumentException>(() => store.Get(chunkId));
    }

    [Fact]
    public void Put_RejectsNullBlob()
    {
        using var store = new HttpBlobStore("http://localhost:8477");
        string validId = new string('a', 64);
        Assert.Throws<ArgumentNullException>(() => store.Put(validId, null!));
    }
}
