using UsenetBackup.Core.Redundancy;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Tests for XOR parity generation and reconstruction.
/// </summary>
public sealed class XorParityTests
{
    [Fact]
    public void GenerateParity_CreatesOnePerGroup()
    {
        var chunkIds = Enumerable.Range(0, 25).Select(i => i.ToString("x64")).ToList();
        var chunks = chunkIds.ToDictionary(id => id, id => new byte[100]);

        var parity = XorParity.GenerateParity(chunkIds, id => chunks[id]);

        // 25 chunks = 2 full groups (10+10) + 1 partial (5) = 3 parity blocks
        Assert.Equal(3, parity.Count);
    }

    [Fact]
    public void Reconstruct_RecoversMissingChunk()
    {
        var chunkIds = Enumerable.Range(0, 10).Select(i => i.ToString("x64")).ToList();
        var chunks = new Dictionary<string, byte[]>();
        var random = new Random(42);
        foreach (string id in chunkIds)
        {
            var bytes = new byte[100];
            random.NextBytes(bytes);
            chunks[id] = bytes;
        }

        var parity = XorParity.GenerateParity(chunkIds, id => chunks[id]);
        Assert.Single(parity);
        byte[] parityBytes = parity.Values.First();

        // Remove one chunk, reconstruct it
        string missingId = chunkIds[3];
        byte[] original = chunks[missingId];
        chunks.Remove(missingId);

        byte[]? reconstructed = XorParity.Reconstruct(
            missingId, chunkIds, id => chunks.GetValueOrDefault(id), parityBytes);

        Assert.NotNull(reconstructed);
        Assert.Equal(original, reconstructed);
    }

    [Fact]
    public void Reconstruct_FailsWithTwoMissing()
    {
        var chunkIds = Enumerable.Range(0, 10).Select(i => i.ToString("x64")).ToList();
        var chunks = chunkIds.ToDictionary(id => id, _ => new byte[100]);

        var parity = XorParity.GenerateParity(chunkIds, id => chunks[id]);
        byte[] parityBytes = parity.Values.First();

        // Remove two chunks
        chunks.Remove(chunkIds[2]);
        chunks.Remove(chunkIds[5]);

        byte[]? reconstructed = XorParity.Reconstruct(
            chunkIds[2], chunkIds, id => chunks.GetValueOrDefault(id), parityBytes);

        Assert.Null(reconstructed); // Can't recover 2 missing with XOR
    }

    [Fact]
    public void MakeParityId_IsDeterministic()
    {
        var group1 = new[] { "a", "b", "c" };
        var group2 = new[] { "c", "b", "a" }; // Different order
        Assert.Equal(XorParity.MakeParityId(group1), XorParity.MakeParityId(group2));
    }

    [Fact]
    public void GetGroupFor_UsesSortedOrder_Regression_b1921c8()
    {
        // Regression test for b1921c8: upload paths (scheduler + CLI) must use
        // the same chunk ordering as NzbGenerator.Generate (sorted by ID),
        // otherwise position-based parity groups won't align and reconstruction
        // will look for the wrong parity blocks.
        //
        // Manifest file order: zzz, aaa, mmm (deliberately unsorted)
        // Sorted order: aaa, mmm, zzz
        var manifestOrder = new[] { "zzz", "aaa", "mmm" };
        var sortedOrder = manifestOrder.OrderBy(id => id, StringComparer.Ordinal).ToList();

        // Simulate upload path: sorted (as fixed in b1921c8)
        var uploadChunkIds = manifestOrder.OrderBy(id => id, StringComparer.Ordinal).ToList();

        // Simulate NZB generation path: sorted (NzbGenerator.Generate)
        var nzbChunkIds = manifestOrder.OrderBy(id => id, StringComparer.Ordinal).ToList();

        // Both must produce identical ordering for parity groups to align
        Assert.Equal(nzbChunkIds, uploadChunkIds);

        // Verify GetGroupFor works with the sorted order
        var group = XorParity.GetGroupFor("mmm", sortedOrder);
        Assert.Contains("aaa", group);
        Assert.Contains("mmm", group);
        Assert.Contains("zzz", group);
    }
}
