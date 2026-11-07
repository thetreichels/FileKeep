using FileKeep.Core.Relay;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Relay par2 companion: generate / verify / repair, including the
/// fallback semantics (a bad par2 never vetoes a good bundle).
/// </summary>
public class RelayPar2Tests
{
    private static byte[] RandomBytes(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[length];
        rng.NextBytes(data);
        return data;
    }

    private static byte[] DamageAt(byte[] data, int offset)
    {
        byte[] damaged = (byte[])data.Clone();
        damaged[offset] ^= 0xFF;
        return damaged;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(200000)]
    public void Generate_ThenVerify_IsValid(int length)
    {
        byte[] data = RandomBytes(length, 42);
        byte[] par2 = RelayPar2.Generate(data);
        Par2VerifyResult result = RelayPar2.Verify(data, par2);
        Assert.Equal(Par2VerifyStatus.Valid, result.Status);
    }

    [Fact]
    public void Verify_MultiGroup_IsValid()
    {
        // 101 blocks (>1 group of 100) exercises group boundaries.
        byte[] data = RandomBytes(101 * RelayPar2.BlockSize, 7);
        byte[] par2 = RelayPar2.Generate(data);
        Assert.Equal(Par2VerifyStatus.Valid, RelayPar2.Verify(data, par2).Status);
    }

    [Fact]
    public void Verify_OneDamagedBlock_ReportsIndex()
    {
        byte[] data = RandomBytes(3 * RelayPar2.BlockSize + 123, 11);
        byte[] par2 = RelayPar2.Generate(data);
        byte[] damaged = DamageAt(data, 2 * RelayPar2.BlockSize + 10);

        Par2VerifyResult result = RelayPar2.Verify(damaged, par2);
        Assert.Equal(Par2VerifyStatus.Damaged, result.Status);
        Assert.Equal(new[] { 2 }, result.BadDataBlocks);
        Assert.Empty(result.BadParityBlocks);
    }

    [Fact]
    public void Repair_OneDamagedBlock_RestoresOriginal()
    {
        byte[] data = RandomBytes(3 * RelayPar2.BlockSize + 123, 13);
        byte[] par2 = RelayPar2.Generate(data);
        byte[] damaged = DamageAt(data, 2 * RelayPar2.BlockSize + 10);

        byte[]? repaired = RelayPar2.Repair(damaged, par2);
        Assert.NotNull(repaired);
        Assert.Equal(data, repaired);
    }

    [Fact]
    public void Repair_FiveDamagedBlocksInOneGroup_RestoresOriginal()
    {
        byte[] data = RandomBytes(10 * RelayPar2.BlockSize, 17);
        byte[] par2 = RelayPar2.Generate(data);
        byte[] damaged = (byte[])data.Clone();
        for (int b = 0; b < 5; b++)
            damaged[b * RelayPar2.BlockSize + 3] ^= 0xFF;

        byte[]? repaired = RelayPar2.Repair(damaged, par2);
        Assert.NotNull(repaired);
        Assert.Equal(data, repaired);
    }

    [Fact]
    public void Repair_SixDamagedBlocksInOneGroup_IsUnrecoverable()
    {
        byte[] data = RandomBytes(10 * RelayPar2.BlockSize, 19);
        byte[] par2 = RelayPar2.Generate(data);
        byte[] damaged = (byte[])data.Clone();
        for (int b = 0; b < 6; b++)
            damaged[b * RelayPar2.BlockSize + 3] ^= 0xFF;

        Assert.Null(RelayPar2.Repair(damaged, par2));
    }

    [Fact]
    public void Repair_DamagedBlockPlusDamagedParityBlock_StillRepairs()
    {
        byte[] data = RandomBytes(3 * RelayPar2.BlockSize, 23);
        byte[] par2 = RelayPar2.Generate(data);
        byte[] damagedData = DamageAt(data, RelayPar2.BlockSize + 5);
        // Damage the first parity block: it lives after header + hashes.
        int parityStart = 16 + 3 * 32 + 5 * 32;
        byte[] damagedPar2 = DamageAt(par2, parityStart + 100);

        Par2VerifyResult result = RelayPar2.Verify(damagedData, damagedPar2);
        Assert.Equal(Par2VerifyStatus.Damaged, result.Status);
        Assert.Equal(new[] { 1 }, result.BadDataBlocks);
        Assert.Equal(new[] { 0 }, result.BadParityBlocks);

        byte[]? repaired = RelayPar2.Repair(damagedData, damagedPar2);
        Assert.NotNull(repaired);
        Assert.Equal(data, repaired);
    }

    [Fact]
    public void Repair_DamagedLastShortBlock_RestoresOriginal()
    {
        byte[] data = RandomBytes(2 * RelayPar2.BlockSize + 17, 29);
        byte[] par2 = RelayPar2.Generate(data);
        byte[] damaged = DamageAt(data, 2 * RelayPar2.BlockSize + 5);

        byte[]? repaired = RelayPar2.Repair(damaged, par2);
        Assert.NotNull(repaired);
        Assert.Equal(data, repaired);
    }

    [Fact]
    public void Verify_BadMagic_IsInvalidPar2()
    {
        byte[] data = RandomBytes(1000, 31);
        byte[] par2 = RelayPar2.Generate(data);
        par2[0] = (byte)'X';

        Par2VerifyResult result = RelayPar2.Verify(data, par2);
        Assert.Equal(Par2VerifyStatus.InvalidPar2, result.Status);
        Assert.NotNull(result.InvalidReason);
    }

    [Fact]
    public void Verify_TruncatedPar2_IsInvalidPar2()
    {
        byte[] data = RandomBytes(1000, 37);
        byte[] par2 = RelayPar2.Generate(data);
        byte[] truncated = par2[..(par2.Length - 10)];

        Assert.Equal(Par2VerifyStatus.InvalidPar2, RelayPar2.Verify(data, truncated).Status);
    }

    [Fact]
    public void Verify_TruncatedData_IsInvalidPar2()
    {
        byte[] data = RandomBytes(2 * RelayPar2.BlockSize + 50, 41);
        byte[] par2 = RelayPar2.Generate(data);
        byte[] truncated = data[..(data.Length - 100)];

        Assert.Equal(Par2VerifyStatus.InvalidPar2, RelayPar2.Verify(truncated, par2).Status);
    }

    [Fact]
    public void Repair_InvalidPar2_ReturnsNull()
    {
        byte[] data = RandomBytes(1000, 43);
        byte[] garbage = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Null(RelayPar2.Repair(data, garbage));
    }

    [Fact]
    public void Repair_ValidData_ReturnsEqualCopy()
    {
        byte[] data = RandomBytes(5000, 47);
        byte[] par2 = RelayPar2.Generate(data);
        byte[]? repaired = RelayPar2.Repair(data, par2);
        Assert.NotNull(repaired);
        Assert.Equal(data, repaired);
        Assert.NotSame(data, repaired);
    }

    [Fact]
    public void Generate_EmptyData_VerifiesValid()
    {
        byte[] data = Array.Empty<byte>();
        byte[] par2 = RelayPar2.Generate(data);
        Assert.Equal(Par2VerifyStatus.Valid, RelayPar2.Verify(data, par2).Status);
        Assert.Equal(data, RelayPar2.Repair(data, par2));
    }
}
