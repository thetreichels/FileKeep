namespace UsenetBackup.Core.Redundancy;

/// <summary>
/// XOR parity (RAID5-style) for Usenet chunk redundancy.
/// Groups chunks into sets of N data + 1 parity (XOR of the group).
/// Recovers any single missing/corrupted chunk per group.
/// </summary>
public static class XorParity
{
    /// <summary>Number of data chunks per parity group.</summary>
    public const int GroupSize = 10;

    /// <summary>
    /// Generates parity blocks for a set of chunk IDs. Returns a map from
    /// parity chunk ID to the parity bytes. The parity chunk ID is derived
    /// deterministically: "parity-" + SHA256(group chunk IDs).
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> GenerateParity(
        IReadOnlyList<string> chunkIds,
        Func<string, byte[]> getChunkBytes)
    {
        var result = new Dictionary<string, byte[]>();
        for (int i = 0; i < chunkIds.Count; i += GroupSize)
        {
            var group = chunkIds.Skip(i).Take(GroupSize).ToList();
            if (group.Count < 2)
                continue; // Need at least 2 for parity to be useful

            // XOR all chunks in the group
            byte[]? parity = null;
            foreach (string chunkId in group)
            {
                byte[] bytes = getChunkBytes(chunkId);
                if (parity is null)
                {
                    parity = (byte[])bytes.Clone();
                }
                else
                {
                    if (bytes.Length != parity.Length)
                        throw new InvalidDataException("Chunk size mismatch in parity group.");
                    for (int j = 0; j < parity.Length; j++)
                        parity[j] ^= bytes[j];
                }
            }

            string parityId = MakeParityId(group);
            result[parityId] = parity!;
        }
        return result;
    }

    /// <summary>
    /// Reconstructs a missing chunk using parity and the other chunks in its group.
    /// Returns null if reconstruction is not possible (more than 1 missing).
    /// </summary>
    public static byte[]? Reconstruct(
        string missingChunkId,
        IReadOnlyList<string> groupChunkIds,
        Func<string, byte[]?> getChunkBytesOrNull,
        byte[] parityBytes)
    {
        byte[] result = (byte[])parityBytes.Clone();
        int missingCount = 0;

        foreach (string chunkId in groupChunkIds)
        {
            if (chunkId == missingChunkId)
            {
                missingCount++;
                continue;
            }
            byte[]? bytes = getChunkBytesOrNull(chunkId);
            if (bytes is null)
            {
                missingCount++;
                if (missingCount > 1)
                    return null; // Can't recover more than 1
                continue;
            }
            if (bytes.Length != result.Length)
                return null;
            for (int i = 0; i < result.Length; i++)
                result[i] ^= bytes[i];
        }

        return missingCount == 1 ? result : null;
    }

    /// <summary>
    /// Deterministic parity chunk ID from the group members.
    /// </summary>
    public static string MakeParityId(IReadOnlyList<string> groupChunkIds)
    {
        string input = "parity:" + string.Join(",", groupChunkIds.OrderBy(id => id));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Gets the group that a chunk belongs to, given the full ordered list.
    /// </summary>
    public static IReadOnlyList<string> GetGroupFor(string chunkId, IReadOnlyList<string> allChunkIds)
    {
        int index = -1;
        for (int i = 0; i < allChunkIds.Count; i++)
        {
            if (allChunkIds[i] == chunkId)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return Array.Empty<string>();
        int groupStart = (index / GroupSize) * GroupSize;
        return allChunkIds.Skip(groupStart).Take(GroupSize).ToList();
    }
}
