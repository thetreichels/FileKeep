using UsenetBackup.Core.Redundancy;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Adapter-level tests for <see cref="ReedSolomon"/> (thin wrapper over
/// ReedSolomonFast). These test the public contract — encode produces
/// parity, reconstruct recovers from erasures — without depending on
/// Galois-field internals (the handmade GF implementation was removed).
/// </summary>
public sealed class ReedSolomonTests
{
    private static byte[][] MakeShards(int count, int size, int seed = 42)
    {
        var random = new Random(seed);
        var shards = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            shards[i] = new byte[size];
            random.NextBytes(shards[i]);
        }
        return shards;
    }

    [Fact]
    public void Encode_ProducesParityShards()
    {
        var rs = new ReedSolomon(10, 3);
        byte[][] data = MakeShards(10, 1024);

        byte[][] parity = rs.Encode(data);

        Assert.Equal(3, parity.Length);
        foreach (byte[] p in parity)
            Assert.Equal(1024, p.Length);
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        var rs = new ReedSolomon(10, 3);
        byte[][] data = MakeShards(10, 1024);

        byte[][] p1 = rs.Encode(data);
        byte[][] p2 = rs.Encode(data);

        for (int i = 0; i < 3; i++)
            Assert.Equal(p1[i], p2[i]);
    }

    [Fact]
    public void Reconstruct_RecoversSingleMissingDataShard()
    {
        var rs = new ReedSolomon(10, 3);
        byte[][] data = MakeShards(10, 1024);
        byte[][] parity = rs.Encode(data);

        // Build full shard array with one data shard missing
        byte[][] shards = new byte[13][];
        bool[] present = new bool[13];
        for (int i = 0; i < 10; i++)
        {
            if (i == 3)
            {
                shards[i] = null!;
                present[i] = false;
            }
            else
            {
                shards[i] = data[i];
                present[i] = true;
            }
        }
        for (int i = 0; i < 3; i++)
        {
            shards[10 + i] = parity[i];
            present[10 + i] = true;
        }

        byte[][] recovered = rs.Reconstruct(shards, present);

        Assert.Equal(data[3], recovered[3]);
    }

    [Fact]
    public void Reconstruct_RecoversThreeMissingDataShards()
    {
        var rs = new ReedSolomon(10, 3);
        byte[][] data = MakeShards(10, 1024);
        byte[][] parity = rs.Encode(data);

        byte[][] shards = new byte[13][];
        bool[] present = new bool[13];
        var missing = new HashSet<int> { 1, 5, 9 };
        for (int i = 0; i < 10; i++)
        {
            if (missing.Contains(i))
            {
                shards[i] = null!;
                present[i] = false;
            }
            else
            {
                shards[i] = data[i];
                present[i] = true;
            }
        }
        for (int i = 0; i < 3; i++)
        {
            shards[10 + i] = parity[i];
            present[10 + i] = true;
        }

        byte[][] recovered = rs.Reconstruct(shards, present);

        foreach (int i in missing)
            Assert.Equal(data[i], recovered[i]);
    }

    [Fact]
    public void Reconstruct_ThrowsWhenTooManyMissing()
    {
        var rs = new ReedSolomon(10, 3);
        byte[][] data = MakeShards(10, 1024);
        byte[][] parity = rs.Encode(data);

        // 4 missing > 3 parity: not recoverable
        byte[][] shards = new byte[13][];
        bool[] present = new bool[13];
        for (int i = 0; i < 10; i++)
        {
            if (i < 4)
            {
                shards[i] = null!;
                present[i] = false;
            }
            else
            {
                shards[i] = data[i];
                present[i] = true;
            }
        }
        for (int i = 0; i < 3; i++)
        {
            shards[10 + i] = parity[i];
            present[10 + i] = true;
        }

        Assert.Throws<InvalidOperationException>(() => rs.Reconstruct(shards, present));
    }

    [Fact]
    public void Reconstruct_RecoversWhenParityShardMissing()
    {
        var rs = new ReedSolomon(10, 3);
        byte[][] data = MakeShards(10, 1024);
        byte[][] parity = rs.Encode(data);

        // One data + one parity missing: still recoverable
        byte[][] shards = new byte[13][];
        bool[] present = new bool[13];
        for (int i = 0; i < 10; i++)
        {
            if (i == 7)
            {
                shards[i] = null!;
                present[i] = false;
            }
            else
            {
                shards[i] = data[i];
                present[i] = true;
            }
        }
        for (int i = 0; i < 3; i++)
        {
            if (i == 1)
            {
                shards[10 + i] = null!;
                present[10 + i] = false;
            }
            else
            {
                shards[10 + i] = parity[i];
                present[10 + i] = true;
            }
        }

        byte[][] recovered = rs.Reconstruct(shards, present);

        Assert.Equal(data[7], recovered[7]);
    }
}
