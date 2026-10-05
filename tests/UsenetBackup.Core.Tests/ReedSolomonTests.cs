using UsenetBackup.Core.Redundancy;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Tests for Reed-Solomon erasure coding.
/// </summary>
public sealed class ReedSolomonTests
{
    [Fact]
    public void GaloisField_MulDiv_Roundtrip()
    {
        byte a = 0x53;
        byte b = 0xCA;
        byte prod = GaloisField.Mul(a, b);
        byte back = GaloisField.Div(prod, b);
        Assert.Equal(a, back);
    }

    [Fact]
    public void GaloisField_Add_IsXor()
    {
        Assert.Equal((byte)(0x53 ^ 0xCA), GaloisField.Add(0x53, 0xCA));
    }

    [Fact]
    public void EncodeDecode_NoLoss_Roundtrip()
    {
        var rs = new ReedSolomon(4, 2);
        var random = new Random(42);
        var data = new byte[4][];
        for (int i = 0; i < 4; i++)
        {
            data[i] = new byte[100];
            random.NextBytes(data[i]);
        }

        byte[][] parity = rs.Encode(data);
        Assert.Equal(2, parity.Length);

        // All shards present, reconstruct should return originals
        byte[][] allShards = new byte[6][];
        var present = new bool[6];
        for (int i = 0; i < 4; i++) { allShards[i] = data[i]; present[i] = true; }
        for (int i = 0; i < 2; i++) { allShards[4 + i] = parity[i]; present[4 + i] = true; }

        byte[][] recovered = rs.Reconstruct(allShards, present);
        for (int i = 0; i < 4; i++)
            Assert.Equal(data[i], recovered[i]);
    }

    [Fact]
    public void Reconstruct_RecoversTwoMissing()
    {
        var rs = new ReedSolomon(10, 3);
        var random = new Random(123);
        var data = new byte[10][];
        for (int i = 0; i < 10; i++)
        {
            data[i] = new byte[50];
            random.NextBytes(data[i]);
        }

        byte[][] parity = rs.Encode(data);

        // Lose 2 data shards
        byte[][] shards = new byte[13][];
        var present = new bool[13];
        for (int i = 0; i < 10; i++)
        {
            if (i == 2 || i == 7)
            {
                shards[i] = new byte[0];
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
        Assert.Equal(data[2], recovered[2]);
        Assert.Equal(data[7], recovered[7]);
    }

    [Fact]
    public void Reconstruct_FailsWithTooManyMissing()
    {
        var rs = new ReedSolomon(10, 3);
        var data = new byte[10][];
        for (int i = 0; i < 10; i++) data[i] = new byte[50];
        byte[][] parity = rs.Encode(data);

        // Lose 4 (more than 3 parity) — should fail
        byte[][] shards = new byte[13][];
        var present = new bool[13];
        for (int i = 0; i < 10; i++)
        {
            if (i < 4) { shards[i] = new byte[0]; present[i] = false; }
            else { shards[i] = data[i]; present[i] = true; }
        }
        for (int i = 0; i < 3; i++) { shards[10 + i] = parity[i]; present[10 + i] = true; }

        Assert.Throws<InvalidOperationException>(() => rs.Reconstruct(shards, present));
    }

    [Fact]
    public void Par2Redundancy_GeneratesThreePerGroup()
    {
        var chunkIds = Enumerable.Range(0, 10).Select(i => i.ToString("x64")).ToList();
        var chunks = chunkIds.ToDictionary(id => id, _ => new byte[100]);

        var parity = Par2Redundancy.GenerateParity(chunkIds, id => chunks[id]);
        Assert.Equal(3, parity.Count); // 3 parity shards per 10 data
    }
}
