using UsenetBackup.Core.Nntp;
using Xunit;

namespace UsenetBackup.Core.Tests;

public sealed class YEncTests
{
    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10000)]
    [InlineData(200000)]
    public void RoundTrip_PreservesData(int size)
    {
        byte[] data = RandomBytes(size);
        string encoded = YEnc.Encode(data, "test.bin");
        Assert.Equal(data, YEnc.Decode(encoded));
    }

    [Fact]
    public void Escapes_SpecialBytes()
    {
        // 0xD6->0x00, 0xE0->0x0A, 0xE3->0x0D, 0x13->0x3D after the +42 shift.
        byte[] data = new byte[] { 0xD6, 0xE0, 0xE3, 0x13 };
        string encoded = YEnc.Encode(data, "test.bin");
        string dataLine = encoded.Split(new[] { "\r\n" }, StringSplitOptions.None)[1];
        Assert.Equal("=@=J=M=}", dataLine);
        Assert.Equal(data, YEnc.Decode(encoded));
    }

    [Fact]
    public void KnownVector_Hello()
    {
        byte[] data = "Hello"u8.ToArray();
        string encoded = YEnc.Encode(data, "hello.txt");
        Assert.Contains("=ybegin", encoded);
        Assert.Contains("size=5", encoded);
        Assert.Equal(data, YEnc.Decode(encoded));
    }

    [Fact]
    public void Crc32_KnownValue()
    {
        Assert.Equal(0xCBF43926u, YEnc.Crc32("123456789"u8.ToArray()));
    }

    [Fact]
    public void Decode_BadCrc_Throws()
    {
        string encoded = YEnc.Encode(RandomBytes(500), "test.bin");
        string tampered = encoded.Replace("crc32=", "crc32=deadbeef");
        Assert.NotEqual(encoded, tampered);
        Assert.Throws<InvalidDataException>(() => YEnc.Decode(tampered));
    }

    [Fact]
    public void Decode_MissingTrailer_Throws()
    {
        string encoded = YEnc.Encode(RandomBytes(500), "test.bin");
        int yend = encoded.IndexOf("=yend", StringComparison.Ordinal);
        string truncated = encoded[..yend];
        Assert.Throws<InvalidDataException>(() => YEnc.Decode(truncated));
    }

    [Fact]
    public void Decode_FlippedDataByte_Throws()
    {
        byte[] data = RandomBytes(500);
        string encoded = YEnc.Encode(data, "test.bin");
        string[] lines = encoded.Split(new[] { "\r\n" }, StringSplitOptions.None);
        // Flip the first char of the first data line (never the =ybegin line).
        char[] chars = lines[1].ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        lines[1] = new string(chars);
        string tampered = string.Join("\r\n", lines);
        Assert.Throws<InvalidDataException>(() => YEnc.Decode(tampered));
    }
}
