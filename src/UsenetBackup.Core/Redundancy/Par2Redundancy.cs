namespace UsenetBackup.Core.Redundancy;

/// <summary>
/// PAR2-style redundancy using Reed-Solomon erasure coding.
/// Groups chunks into sets of 10 data + 3 parity (configurable).
/// Can recover up to 3 missing chunks per group.
/// </summary>
/// <remarks>
/// Supports varying chunk sizes: smaller chunks are zero-padded to the
/// maximum size in the group, and original lengths are stored in the parity
/// header. During reconstruction, recovered shards are trimmed to their
/// original lengths before hash verification.
///
/// Parity data format (each of the 3 parity blocks):
///   [magic:4] = "RS1 "
///   [numChunks:4] (little-endian int32)
///   [len0:4][len1:4]...[lenN:4] (little-endian int32 each)
///   [parity bytes...] (maxLen bytes)
/// </remarks>
public static class Par2Redundancy
{
    /// <summary>Number of data chunks per group.</summary>
    public const int DataShards = 10;
    /// <summary>Number of parity chunks per group.</summary>
    public const int ParityShards = 3;

    private static readonly byte[] Magic = "RS1 "u8.ToArray();
    private const int HeaderFixedSize = 8; // magic(4) + numChunks(4)

    /// <summary>
    /// Generates PAR2 parity blocks for a set of chunk IDs.
    /// Returns a map from parity chunk ID to parity bytes (including header).
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> GenerateParity(
        IReadOnlyList<string> chunkIds,
        Func<string, byte[]> getChunkBytes,
        Action<string>? logWarning = null)
    {
        var result = new Dictionary<string, byte[]>();
        var rs = new ReedSolomon(DataShards, ParityShards);

        for (int i = 0; i < chunkIds.Count; i += DataShards)
        {
            var group = chunkIds.Skip(i).Take(DataShards).ToList();
            if (group.Count < 2)
                continue;

            // Collect chunks, find max length, pad to uniform size
            var originalLengths = new List<int>();
            int maxLen = 0;
            var rawChunks = new List<byte[]>();
            foreach (string chunkId in group)
            {
                byte[] bytes = getChunkBytes(chunkId);
                rawChunks.Add(bytes);
                originalLengths.Add(bytes.Length);
                if (bytes.Length > maxLen)
                    maxLen = bytes.Length;
            }

            // Pad to DataShards with zero shards; pad short chunks with zeros
            var dataShards = new byte[DataShards][];
            for (int j = 0; j < DataShards; j++)
            {
                if (j < rawChunks.Count)
                {
                    if (rawChunks[j].Length == maxLen)
                    {
                        dataShards[j] = rawChunks[j];
                    }
                    else
                    {
                        dataShards[j] = new byte[maxLen];
                        Array.Copy(rawChunks[j], dataShards[j], rawChunks[j].Length);
                    }
                }
                else
                {
                    dataShards[j] = new byte[maxLen];
                }
            }

            byte[][] parityShards = rs.Encode(dataShards);
            for (int p = 0; p < parityShards.Length; p++)
            {
                string parityId = MakeParityId(group, p);
                byte[] parityData = BuildParityData(parityShards[p], originalLengths);
                result[parityId] = parityData;
            }
        }
        return result;
    }

    /// <summary>
    /// Reconstructs missing data shards using parity blocks.
    /// Returns dictionary of chunk ID -> reconstructed bytes (trimmed to original length),
    /// or null if reconstruction is not possible.
    /// </summary>
    public static Dictionary<string, byte[]>? Reconstruct(
        IReadOnlyList<string> groupChunkIds,
        Func<string, byte[]?> getChunkBytesOrNull,
        IReadOnlyList<byte[]> parityDatas)
    {
        if (parityDatas.Count == 0)
            return null;

        // Parse header from first parity block
        if (!TryParseHeader(parityDatas[0], out int[]? originalLengths, out _))
            return null;

        if (originalLengths!.Length != groupChunkIds.Count)
            return null;

        int maxLen = 0;
        var parityShards = new List<byte[]>();
        int[]? firstLengths = null;
        foreach (byte[] data in parityDatas)
        {
            if (!TryParseHeader(data, out int[]? lengths, out byte[]? parity))
                return null;
            // Cross-validate: all parity blocks must agree on the header.
            // A mismatched header means corruption; fail cleanly rather than
            // doing math on inconsistent inputs.
            if (firstLengths is null)
                firstLengths = lengths;
            else if (!lengths!.SequenceEqual(firstLengths))
                return null;
            parityShards.Add(parity!);
            if (parity!.Length > maxLen)
                maxLen = parity.Length;
        }

        // All parity shards must have the same length. A truncated parity
        // block would otherwise shrink maxLen and silently corrupt the
        // reconstruction math.
        if (parityShards.Any(p => p.Length != maxLen))
            return null;

        var rs = new ReedSolomon(DataShards, ParityShards);

        // Build shard array: present shards padded to maxLen, missing as null
        var shards = new byte[DataShards][];
        var shardPresent = new bool[DataShards];
        var missingIndices = new List<int>();

        for (int j = 0; j < DataShards; j++)
        {
            if (j < groupChunkIds.Count)
            {
                byte[]? bytes = getChunkBytesOrNull(groupChunkIds[j]);
                if (bytes is not null)
                {
                    // Pad to maxLen
                    if (bytes.Length == maxLen)
                    {
                        shards[j] = bytes;
                    }
                    else
                    {
                        shards[j] = new byte[maxLen];
                        Array.Copy(bytes, shards[j], Math.Min(bytes.Length, maxLen));
                    }
                    shardPresent[j] = true;
                }
                else
                {
                    missingIndices.Add(j);
                    shardPresent[j] = false;
                }
            }
            else
            {
                // Padding shard (beyond group count) — treated as present zeros
                shards[j] = new byte[maxLen];
                shardPresent[j] = true;
            }
        }

        // Add parity shards to the array for reconstruction
        // ReedSolomon.Reconstruct expects full shard array; we need to adapt
        // For now, use the parity shards directly via RS decode
        // This is a simplified approach: use first N present shards + parity

        try
        {
            byte[][] reconstructed = rs.Reconstruct(
                BuildFullShardArray(shards, shardPresent, parityShards, maxLen),
                BuildPresentArray(shardPresent, parityShards.Count, maxLen));

            var result = new Dictionary<string, byte[]>();
            foreach (int idx in missingIndices)
            {
                if (idx < groupChunkIds.Count)
                {
                    int originalLen = originalLengths[idx];
                    if (originalLen < 0 || originalLen > maxLen)
                        return null; // Corrupt header
                    byte[] trimmed = new byte[originalLen];
                    Array.Copy(reconstructed[idx], trimmed, Math.Min(originalLen, reconstructed[idx].Length));
                    result[groupChunkIds[idx]] = trimmed;
                }
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static byte[][] BuildFullShardArray(byte[][] dataShards, bool[] dataPresent, List<byte[]> parityShards, int maxLen)
    {
        var all = new byte[DataShards + ParityShards][];
        Array.Copy(dataShards, all, DataShards);
        for (int p = 0; p < parityShards.Count && p < ParityShards; p++)
            all[DataShards + p] = parityShards[p];
        return all;
    }

    private static bool[] BuildPresentArray(bool[] dataPresent, int parityCount, int maxLen)
    {
        var all = new bool[DataShards + ParityShards];
        Array.Copy(dataPresent, all, DataShards);
        for (int p = 0; p < parityCount && p < ParityShards; p++)
            all[DataShards + p] = true;
        return all;
    }

    private static byte[] BuildParityData(byte[] parity, List<int> originalLengths)
    {
        int headerSize = HeaderFixedSize + originalLengths.Count * 4;
        byte[] data = new byte[headerSize + parity.Length];
        Magic.CopyTo(data, 0);
        BitConverter.GetBytes(originalLengths.Count).CopyTo(data, 4);
        for (int i = 0; i < originalLengths.Count; i++)
            BitConverter.GetBytes(originalLengths[i]).CopyTo(data, 8 + i * 4);
        parity.CopyTo(data, headerSize);
        return data;
    }

    private static bool TryParseHeader(byte[] data, out int[]? lengths, out byte[]? parity)
    {
        lengths = null;
        parity = null;
        if (data.Length < HeaderFixedSize)
            return false;
        if (data[0] != Magic[0] || data[1] != Magic[1] || data[2] != Magic[2] || data[3] != Magic[3])
            return false;

        int numChunks = BitConverter.ToInt32(data, 4);
        if (numChunks <= 0 || numChunks > 100)
            return false;

        int headerSize = HeaderFixedSize + numChunks * 4;
        if (data.Length < headerSize)
            return false;

        lengths = new int[numChunks];
        for (int i = 0; i < numChunks; i++)
            lengths[i] = BitConverter.ToInt32(data, 8 + i * 4);

        parity = new byte[data.Length - headerSize];
        Array.Copy(data, headerSize, parity, 0, parity.Length);
        return true;
    }

    /// <summary>
    /// Deterministic parity chunk ID.
    /// </summary>
    public static string MakeParityId(IReadOnlyList<string> groupChunkIds, int parityIndex)
    {
        string input = $"par2:{parityIndex}:" + string.Join(",", groupChunkIds.OrderBy(id => id, StringComparer.Ordinal));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Finds the parity group containing the given chunk ID.
    /// </summary>
    public static List<string> GetGroupFor(string chunkId, IReadOnlyList<string> allChunkIds)
    {
        int index = IndexOf(allChunkIds, chunkId);
        if (index < 0)
            return new List<string>();
        int groupStart = (index / DataShards) * DataShards;
        return allChunkIds.Skip(groupStart).Take(DataShards).ToList();
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value)
                return i;
        return -1;
    }
}
