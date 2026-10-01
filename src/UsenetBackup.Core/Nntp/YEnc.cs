using System.Text;

namespace UsenetBackup.Core.Nntp;

/// <summary>
/// yEnc encoder/decoder (the standard Usenet binary-to-text encoding).
/// Encoding: c = (b + 42) mod 256, escaping 0x00, 0x0A, 0x0D and '='
/// as '=' followed by (c + 64) mod 256. Integrity is carried by the
/// =yend size and CRC-32 (ISO 3309) trailer.
/// </summary>
public static class YEnc
{
    public const int LineLength = 128;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Encodes data into a full yEnc body (=ybegin … =yend).</summary>
    public static string Encode(byte[] data, string name)
    {
        string safeName = SanitizeName(name);
        var sb = new StringBuilder();
        sb.Append("=ybegin line=").Append(LineLength)
          .Append(" size=").Append(data.Length)
          .Append(" name=").Append(safeName).Append("\r\n");

        int col = 0;
        foreach (byte b in data)
        {
            int c = (b + 42) & 0xFF;
            if (c == 0x00 || c == 0x0A || c == 0x0D || c == 0x3D)
            {
                sb.Append('=').Append((char)((c + 64) & 0xFF));
                col += 2;
            }
            else
            {
                sb.Append((char)c);
                col += 1;
            }
            if (col >= LineLength)
            {
                sb.Append("\r\n");
                col = 0;
            }
        }
        if (col > 0)
            sb.Append("\r\n");

        sb.Append("=yend size=").Append(data.Length)
          .Append(" crc32=").Append(Crc32(data).ToString("x8"))
          .Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Decodes a yEnc body, verifying the =yend size and CRC-32.
    /// Throws <see cref="InvalidDataException"/> on any corruption.
    /// </summary>
    public static byte[] Decode(string body)
    {
        string[] lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        int begin = Array.FindIndex(lines, l => l.StartsWith("=ybegin", StringComparison.Ordinal));
        int end = Array.FindIndex(lines, l => l.StartsWith("=yend", StringComparison.Ordinal));
        if (begin < 0 || end < 0 || end <= begin)
            throw new InvalidDataException("yEnc body is missing =ybegin/=yend lines.");

        long expectedSize = ParseParam(lines[begin], "size");
        string expectedCrc = ParseParamStr(lines[end], "crc32");

        var data = new List<byte>((int)Math.Min(expectedSize, 64 * 1024 * 1024));
        for (int i = begin + 1; i < end; i++)
        {
            string line = lines[i];
            for (int j = 0; j < line.Length; j++)
            {
                int c = line[j];
                if (c == '=')
                {
                    if (++j >= line.Length)
                        throw new InvalidDataException("yEnc body ends with a dangling escape.");
                    c = (line[j] - 64) & 0xFF;
                }
                data.Add((byte)((c - 42) & 0xFF));
            }
        }

        if (data.Count != expectedSize)
            throw new InvalidDataException(
                $"yEnc size mismatch: trailer says {expectedSize}, decoded {data.Count}.");

        byte[] result = data.ToArray();
        string actualCrc = Crc32(result).ToString("x8");
        if (!actualCrc.Equals(expectedCrc, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"yEnc CRC-32 mismatch: trailer says {expectedCrc}, computed {actualCrc}.");
        return result;
    }

    private static long ParseParam(string line, string name)
    {
        string raw = ParseParamStr(line, name);
        if (!long.TryParse(raw, out long value) || value < 0)
            throw new InvalidDataException($"yEnc line has invalid {name}: '{line}'.");
        return value;
    }

    private static string ParseParamStr(string line, string name)
    {
        string key = name + "=";
        int at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidDataException($"yEnc line is missing {name}: '{line}'.");
        int start = at + key.Length;
        int stop = line.IndexOf(' ', start);
        return stop < 0 ? line[start..] : line[start..stop];
    }

    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.Length == 0 ? "chunk.bin" : sb.ToString();
    }
}
