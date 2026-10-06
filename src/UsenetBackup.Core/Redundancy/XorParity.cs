namespace UsenetBackup.Core.Redundancy;

/// <summary>
/// XOR parity (RAID5-style) for Usenet chunk redundancy.
/// Groups chunks into sets of N data + 1 parity (XOR of the group).
/// Recovers any single missing/corrupted chunk per group.
/// </summary>
/// <remarks>
/// Supports varying chunk sizes: smaller chunks are zero-padded to the
/// maximum size in the group, and original lengths are stored in the parity
/// header. During reconstruction, the recovered chunk is trimmed to its
/// original length before hash verification.
///
/// Parity data format:
///   [magic:4] = "XOR1"
///   [numChunks:4] (little-endian int32)
///   [len0:4][len1:4]...[lenN:4] (little-endian int32 each)
///   [parity bytes...] (maxLen bytes)
/// </remarks>
public static class XorParity
{
    /// <summary>Number of data chunks per parity group.</summary>
    public const int GroupSize = 10;

    private static readonly byte[] Magic = "XOR1"u8.ToArray();
    private const int HeaderFixedSize = 8; // magic(4) + numChunks(4)

    /// <summary>
    /// Generates parity blocks for a set of chunk IDs. Returns a map from
    /// parity chunk ID to the parity bytes (including length header).
    /// The parity chunk ID is derived deterministically from group chunk IDs.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> GenerateParity(
        IReadOnlyList<string> chunkIds,
        Func<string, byte[]> getChunkBytes,
        Action<string>? logWarning = null)
    {
        var result = new Dictionary<string, byte[]>();
        for (int i = 0; i < chunkIds.Count; i += GroupSize)
        {
            var group = chunkIds.Skip(i).Take(GroupSize).ToList();
            if (group.Count < 2)
                continue; // Need at least 2 for parity to be useful

            // Collect chunks and find max length
            var chunks = new List<byte[]>();
            int maxLen = 0;
            var originalLengths = new List<int>();
            foreach (string chunkId in group)
            {
                byte[] bytes = getChunkBytes(chunkId);
                chunks.Add(bytes);
                originalLengths.Add(bytes.Length);
                if (bytes.Length > maxLen)
                    maxLen = bytes.Length;
            }

            // XOR padded chunks
            byte[] parity = new byte[maxLen];
            foreach (byte[] bytes in chunks)
            {
                for (int j = 0; j < bytes.Length; j++)
                    parity[j] ^= bytes[j];
                // Bytes beyond bytes.Length are XORed with 0 (no-op, already zero)
            }

            // Build parity data with length header
            byte[] parityData = BuildParityData(parity, originalLengths);

            string parityId = MakeParityId(group);
            result[parityId] = parityData;
        }
        return result;
    }

    /// <summary>
    /// Reconstructs a missing chunk using parity and the other chunks in its group.
    /// Returns null if reconstruction is not possible (more than 1 missing).
    /// The returned bytes are trimmed to the original chunk length.
    /// </summary>
    public static byte[]? Reconstruct(
        string missingChunkId,
        IReadOnlyList<string> groupChunkIds,
        Func<string, byte[]?> getChunkBytesOrNull,
        byte[] parityData)
    {
        // Parse header
        if (!TryParseHeader(parityData, out int[]? originalLengths, out byte[]? parityBytes))
            return null;

        if (originalLengths!.Length != groupChunkIds.Count)
            return null;

        int missingIndex = IndexOf(groupChunkIds, missingChunkId);
        if (missingIndex < 0)
            return null;

        int maxLen = parityBytes!.Length;
        byte[] result = (byte[])parityBytes.Clone();
        int missingCount = 0;

        for (int idx = 0; idx < groupChunkIds.Count; idx++)
        {
            string chunkId = groupChunkIds[idx];
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
            // XOR available bytes (only up to their actual length; rest is zero padding)
            for (int j = 0; j < bytes.Length && j < maxLen; j++)
                result[j] ^= bytes[j];
        }

        if (missingCount != 1)
            return null;

        // Trim to original length
        int originalLen = originalLengths[missingIndex];
        if (originalLen > maxLen)
            return null; // Corrupt header

        byte[] trimmed = new byte[originalLen];
        Array.Copy(result, trimmed, originalLen);
        return trimmed;
    }

    /// <summary>
    /// Builds parity data with length header.
    /// </summary>
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

    /// <summary>
    /// Parses parity data header. Returns false if invalid.
    /// </summary>
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
    /// Finds the parity group containing the given chunk ID.
    /// </summary>
    public static List<string> GetGroupFor(string chunkId, IReadOnlyList<string> allChunkIds)
    {
        int index = IndexOf(allChunkIds, chunkId);
        if (index < 0)
            return new List<string>();
        int groupStart = (index / GroupSize) * GroupSize;
        return allChunkIds.Skip(groupStart).Take(GroupSize).ToList();
    }

    /// <summary>
    /// Deterministic parity chunk ID from group chunk IDs (order-insensitive).
    /// </summary>
    public static string MakeParityId(IReadOnlyList<string> groupChunkIds)
    {
        var sorted = groupChunkIds.OrderBy(id => id, StringComparer.Ordinal).ToList();
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (string id in sorted)
        {
            byte[] bytes = Convert.FromHexString(id);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] hash = sha.Hash!;
        return "parity-xor-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value)
                return i;
        return -1;
    }
}
