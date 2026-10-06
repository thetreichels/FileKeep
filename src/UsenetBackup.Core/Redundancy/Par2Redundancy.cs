namespace UsenetBackup.Core.Redundancy;

/// <summary>
/// PAR2-style redundancy using Reed-Solomon erasure coding.
/// Groups chunks into sets of 10 data + 3 parity (configurable).
/// Can recover up to 3 missing chunks per group.
/// </summary>
/// <remarks>
/// Limitation: all chunks in a group must have identical encrypted sizes.
/// Groups with varying sizes (e.g., the last chunk of a file being smaller)
/// are skipped — no parity is generated for them. This is fail-closed:
/// such chunks simply have no parity protection, rather than incorrect parity.
/// </remarks>
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

            // Pad group to DataShards with zero shards if needed.
            // All chunks must have identical sizes; skip groups with varying
            // sizes (e.g., last chunk of a file) rather than generating
            // incorrect parity.
            var dataShards = new byte[DataShards][];
            int shardSize = -1;
            bool sizeMismatch = false;
            for (int j = 0; j < DataShards; j++)
            {
                if (j < group.Count)
                {
                    dataShards[j] = getChunkBytes(group[j]);
                    if (shardSize < 0)
                    {
                        shardSize = dataShards[j].Length;
                    }
                    else if (dataShards[j].Length != shardSize)
                    {
                        sizeMismatch = true;
                        break;
                    }
                }
                else
                {
                    // Pad with zeros
                    dataShards[j] = new byte[shardSize];
                }
            }
            if (sizeMismatch)
            {
                logWarning?.Invoke(
                    $"PAR2 parity: skipping group starting at {group[0]} " +
                    $"({group.Count} chunks) due to varying chunk sizes. " +
                    "These chunks will have no parity protection.");
                continue; // Skip groups with varying chunk sizes
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
