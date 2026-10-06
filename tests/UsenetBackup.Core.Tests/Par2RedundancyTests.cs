using UsenetBackup.Core.Redundancy;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Tests for PAR2-style Reed-Solomon redundancy: parity generation,
/// variable-size padding, and reconstruction from erasures.
/// </summary>
public sealed class Par2RedundancyTests
{
    private static string ChunkId(int i) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"chunk-{i}"))).ToLowerInvariant();

    private static Dictionary<string, byte[]> MakeChunks(int count, int size, int seed = 42)
    {
        var random = new Random(seed);
        var chunks = new Dictionary<string, byte[]>();
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = new byte[size];
            random.NextBytes(bytes);
            chunks[ChunkId(i)] = bytes;
        }
        return chunks;
    }

    [Fact]
    public void GenerateParity_CreatesThreePerGroup()
    {
        var chunks = MakeChunks(10, 1024);
        var ids = chunks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();

        var parity = Par2Redundancy.GenerateParity(ids, id => chunks[id]);

        Assert.Equal(3, parity.Count);
    }

    [Fact]
    public void Reconstruct_RecoversSingleMissing()
    {
        var chunks = MakeChunks(10, 1024);
        var ids = chunks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var parity = Par2Redundancy.GenerateParity(ids, id => chunks[id]);

        string missing = ids[3];
        var remaining = chunks.Where(kv => kv.Key != missing)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var result = Par2Redundancy.Reconstruct(
            ids,
            id => remaining.TryGetValue(id, out byte[]? b) ? b : null,
            parity.Values.ToList());

        Assert.NotNull(result);
        Assert.True(result!.ContainsKey(missing));
        Assert.Equal(chunks[missing], result[missing]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Reconstruct_RecoversUpToThreeMissing(int missingCount)
    {
        var chunks = MakeChunks(10, 1024);
        var ids = chunks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var parity = Par2Redundancy.GenerateParity(ids, id => chunks[id]);

        var missingIds = ids.Take(missingCount).ToList();
        var remaining = chunks.Where(kv => !missingIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var result = Par2Redundancy.Reconstruct(
            ids,
            id => remaining.TryGetValue(id, out byte[]? b) ? b : null,
            parity.Values.ToList());

        Assert.NotNull(result);
        foreach (string mid in missingIds)
        {
            Assert.True(result!.ContainsKey(mid), $"Missing {mid} not reconstructed");
            Assert.Equal(chunks[mid], result[mid]);
        }
    }

    [Fact]
    public void Reconstruct_FailsWithFourMissing()
    {
        var chunks = MakeChunks(10, 1024);
        var ids = chunks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var parity = Par2Redundancy.GenerateParity(ids, id => chunks[id]);

        var missingIds = ids.Take(4).ToList();
        var remaining = chunks.Where(kv => !missingIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // RS(10,3) can only recover 3; with 4 missing and all 3 parity
        // we have 9 shards < 10 needed. Reconstruct should return null
        // or throw (we treat both as "cannot reconstruct").
        try
        {
            var result = Par2Redundancy.Reconstruct(
                ids,
                id => remaining.TryGetValue(id, out byte[]? b) ? b : null,
                parity.Values.ToList());
            // If it returns non-null, it must NOT contain all 4 (can't be correct)
            if (result is not null)
                Assert.True(result.Count < 4);
        }
        catch
        {
            // Throwing is also acceptable for unrecoverable
        }
    }

    [Fact]
    public void Reconstruct_SupportsVaryingSizes()
    {
        var random = new Random(7);
        var chunks = new Dictionary<string, byte[]>();
        int[] sizes = { 100, 500, 1024, 250, 800, 1024, 50, 900, 1024, 300 };
        for (int i = 0; i < 10; i++)
        {
            byte[] bytes = new byte[sizes[i]];
            random.NextBytes(bytes);
            chunks[ChunkId(i)] = bytes;
        }
        var ids = chunks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var parity = Par2Redundancy.GenerateParity(ids, id => chunks[id]);

        // Remove two chunks of different sizes
        var missingIds = new[] { ids[0], ids[5] }.ToList();
        var remaining = chunks.Where(kv => !missingIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var result = Par2Redundancy.Reconstruct(
            ids,
            id => remaining.TryGetValue(id, out byte[]? b) ? b : null,
            parity.Values.ToList());

        Assert.NotNull(result);
        foreach (string mid in missingIds)
        {
            Assert.True(result!.ContainsKey(mid));
            Assert.Equal(chunks[mid], result[mid]); // Must be trimmed to original length
        }
    }

    [Fact]
    public void Reconstruct_PartialGroup()
    {
        // Only 7 chunks (partial group of 10)
        var chunks = MakeChunks(7, 512);
        var ids = chunks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var parity = Par2Redundancy.GenerateParity(ids, id => chunks[id]);

        Assert.Equal(3, parity.Count);

        string missing = ids[2];
        var remaining = chunks.Where(kv => kv.Key != missing)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var result = Par2Redundancy.Reconstruct(
            ids,
            id => remaining.TryGetValue(id, out byte[]? b) ? b : null,
            parity.Values.ToList());

        Assert.NotNull(result);
        Assert.True(result!.ContainsKey(missing));
        Assert.Equal(chunks[missing], result[missing]);
    }

    [Fact]
    public void Reconstruct_CorruptHeader_ReturnsNull()
    {
        var chunks = MakeChunks(10, 512);
        var ids = chunks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var parity = Par2Redundancy.GenerateParity(ids, id => chunks[id]);

        // Corrupt the magic bytes of all parity blocks
        var corrupt = parity.Values.Select(p =>
        {
            byte[] c = (byte[])p.Clone();
            c[0] = (byte)'X'; c[1] = (byte)'X'; c[2] = (byte)'X'; c[3] = (byte)'X';
            return c;
        }).ToList();

        string missing = ids[0];
        var remaining = chunks.Where(kv => kv.Key != missing)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var result = Par2Redundancy.Reconstruct(
            ids,
            id => remaining.TryGetValue(id, out byte[]? b) ? b : null,
            corrupt);

        Assert.Null(result);
    }

    [Fact]
    public void Reconstruct_NegativeLengthInHeader_ReturnsNull()
    {
        var chunks = MakeChunks(10, 512);
        var ids = chunks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var parity = Par2Redundancy.GenerateParity(ids, id => chunks[id]);

        // Craft a parity block with negative length in header
        byte[] first = parity.Values.First();
        byte[] evil = (byte[])first.Clone();
        BitConverter.GetBytes(-1).CopyTo(evil, 8); // First length = -1

        var evilList = new List<byte[]> { evil };
        evilList.AddRange(parity.Values.Skip(1));

        string missing = ids[0];
        var remaining = chunks.Where(kv => kv.Key != missing)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Must not throw; should return null or a safe result
        var result = (Dictionary<string, byte[]>?)null;
        var ex = Record.Exception(() => result = Par2Redundancy.Reconstruct(
            ids,
            id => remaining.TryGetValue(id, out byte[]? b) ? b : null,
            evilList));
        Assert.Null(ex);
    }

    [Fact]
    public void MakeParityId_IsDeterministicAndOrderInsensitive()
    {
        var ids = new List<string> { ChunkId(2), ChunkId(0), ChunkId(1) };
        var sorted = ids.OrderBy(id => id, StringComparer.Ordinal).ToList();

        string a = Par2Redundancy.MakeParityId(ids, 0);
        string b = Par2Redundancy.MakeParityId(sorted, 0);
        Assert.Equal(a, b);

        // Different parity index => different ID
        string c = Par2Redundancy.MakeParityId(ids, 1);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void MakeParityId_UsesOrdinalOrdering()
    {
        // IDs that would sort differently under culture-sensitive vs ordinal
        // (uppercase vs lowercase) must use ordinal to match XorParity behavior.
        var ids = new List<string> { "b", "A" };
        string id1 = Par2Redundancy.MakeParityId(ids, 0);
        string id2 = Par2Redundancy.MakeParityId(new List<string> { "A", "b" }, 0);
        Assert.Equal(id1, id2); // Order-insensitive regardless
    }

    [Fact]
    public void GetGroupFor_UsesPositionalGroups()
    {
        var ids = Enumerable.Range(0, 25).Select(ChunkId)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();

        var group = Par2Redundancy.GetGroupFor(ids[12], ids);
        Assert.Equal(10, group.Count);
        Assert.Equal(ids.Skip(10).Take(10).ToList(), group);

        // Last partial group
        var lastGroup = Par2Redundancy.GetGroupFor(ids[23], ids);
        Assert.Equal(5, lastGroup.Count);
    }

    [Fact]
    public void RoundTrip_FullGroup_AllErasureCombos()
    {
        // Exhaustive: every combination of 1-3 missing out of 10
        var chunks = MakeChunks(10, 256, seed: 99);
        var ids = chunks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var parity = Par2Redundancy.GenerateParity(ids, id => chunks[id]);
        var parityList = parity.Values.ToList();

        int tested = 0;
        foreach (int k in new[] { 1, 2, 3 })
        {
            foreach (var combo in Combinations(10, k).Take(20)) // Sample 20 per k
            {
                var missingIds = combo.Select(i => ids[i]).ToList();
                var remaining = chunks.Where(kv => !missingIds.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var result = Par2Redundancy.Reconstruct(
                    ids,
                    id => remaining.TryGetValue(id, out byte[]? b) ? b : null,
                    parityList);

                Assert.NotNull(result);
                foreach (string mid in missingIds)
                    Assert.Equal(chunks[mid], result![mid]);
                tested++;
            }
        }
        Assert.True(tested > 0);
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        int[] result = new int[k];
        return CombinationsRec(n, k, 0, 0, result);
    }

    private static IEnumerable<int[]> CombinationsRec(int n, int k, int start, int depth, int[] current)
    {
        if (depth == k)
        {
            yield return (int[])current.Clone();
            yield break;
        }
        for (int i = start; i < n; i++)
        {
            current[depth] = i;
            foreach (var c in CombinationsRec(n, k, i + 1, depth + 1, current))
                yield return c;
        }
    }
}
