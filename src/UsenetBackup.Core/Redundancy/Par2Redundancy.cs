namespace UsenetBackup.Core.Redundancy;

/// <summary>
/// PAR2-style redundancy using Reed-Solomon erasure coding.
/// Groups chunks into sets of 10 data + 3 parity (configurable).
/// Can recover up to 3 missing chunks per group.
/// </summary>
public static class Par2Redundancy
{
    /// <summary>Number of data chunks per group.</summary>
    public const int DataShards = 10;
    /// <summary>Number of parity chunks per group.</summary>
    public const int ParityShards = 3;

    /// <summary>
    /// Generates PAR2 parity blocks for a set of chunk IDs.
    /// Returns a map from parity chunk ID to parity bytes.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> GenerateParity(
        IReadOnlyList<string> chunkIds,
        Func<string, byte[]> getChunkBytes)
    {
        var result = new Dictionary<string, byte[]>();
        var rs = new ReedSolomon(DataShards, ParityShards);

        for (int i = 0; i < chunkIds.Count; i += DataShards)
        {
            var group = chunkIds.Skip(i).Take(DataShards).ToList();
            if (group.Count < 2)
                continue;

            // Pad group to DataShards with zero shards if needed
            var dataShards = new byte[DataShards][];
            int shardSize = -1;
            for (int j = 0; j < DataShards; j++)
            {
                if (j < group.Count)
                {
                    dataShards[j] = getChunkBytes(group[j]);
                    if (shardSize < 0) shardSize = dataShards[j].Length;
                }
                else
                {
                    // Pad with zeros
                    dataShards[j] = new byte[shardSize];
                }
            }

            byte[][] parityShards = rs.Encode(dataShards);
            for (int p = 0; p < parityShards.Length; p++)
            {
                string parityId = MakeParityId(group, p);
                result[parityId] = parityShards[p];
            }
        }
        return result;
    }

    /// <summary>
    /// Deterministic parity chunk ID.
    /// </summary>
    public static string MakeParityId(IReadOnlyList<string> groupChunkIds, int parityIndex)
    {
        string input = $"par2:{parityIndex}:" + string.Join(",", groupChunkIds.OrderBy(id => id));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
