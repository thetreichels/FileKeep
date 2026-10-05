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
}
