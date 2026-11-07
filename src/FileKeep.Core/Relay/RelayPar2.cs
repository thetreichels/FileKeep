using System.Security.Cryptography;
using FileKeep.Core.Redundancy;

namespace FileKeep.Core.Relay;

/// <summary>
/// The `.par2` companion for relay bundles (see docs/ios-relay-protocol.md §6).
///
/// Proves integrity (per-block SHA-256) and enables repair (Reed-Solomon
/// parity) of a `.kfb` bundle. It does NOT prove authorship — anyone can
/// par2 any file; the signed index does the "is this my server?" job.
///
/// Format (all integers little-endian):
///   [magic "FKP2":4][blockSize:4][blockCount:4][parityBlockCount:4]
///   [sha256 per data block: blockCount * 32]
///   [sha256 per parity block: parityBlockCount * 32]
///   [parity blocks: parityBlockCount * blockSize]
///
/// Data blocks are 64 KiB; groups of 100 data blocks get 5 parity blocks
/// (~5% overhead). A group with more than 5 damaged data blocks is
/// unrecoverable from this pair. The last data block may be short; its
/// hash covers its actual bytes, and repair trims the reconstructed block
/// to the same length. A damaged par2 never vetoes a good bundle: callers
/// treat <see cref="Par2VerifyStatus.InvalidPar2"/> as "ignore the par2,
/// open the bundle" (the bundle's own AES-GCM authentication proves it).
/// </summary>
public static class RelayPar2
{
    /// <summary>Bytes per data block.</summary>
    public const int BlockSize = 65536;

    /// <summary>Data blocks per Reed-Solomon group.</summary>
    public const int DataShards = 100;

    /// <summary>Parity blocks per Reed-Solomon group (~5% overhead).</summary>
    public const int ParityShards = 5;

    private static readonly byte[] Magic = "FKP2"u8.ToArray();
    private const int HeaderSize = 16; // magic(4) + blockSize(4) + blockCount(4) + parityBlockCount(4)

    /// <summary>
    /// Generates a par2 companion for <paramref name="data"/>.
    /// </summary>
    public static byte[] Generate(ReadOnlySpan<byte> data)
    {
        int blockCount = (data.Length + BlockSize - 1) / BlockSize;
        int groups = (blockCount + DataShards - 1) / DataShards;
        int parityBlockCount = groups * ParityShards;

        // Hash data blocks (actual bytes; the last block may be short).
        byte[][] dataHashes = new byte[blockCount][];
        for (int i = 0; i < blockCount; i++)
        {
            int len = Math.Min(BlockSize, data.Length - i * BlockSize);
            dataHashes[i] = SHA256.HashData(data.Slice(i * BlockSize, len));
        }

        // RS-encode each group; parity blocks are full BlockSize bytes.
        byte[][] parityBlocks = new byte[parityBlockCount][];
        byte[][] parityHashes = new byte[parityBlockCount][];
        var rs = new ReedSolomon(DataShards, ParityShards);
        for (int g = 0; g < groups; g++)
        {
            byte[][] dataShards = new byte[DataShards][];
            for (int j = 0; j < DataShards; j++)
            {
                int blockIndex = g * DataShards + j;
                if (blockIndex < blockCount)
                {
                    int len = Math.Min(BlockSize, data.Length - blockIndex * BlockSize);
                    dataShards[j] = new byte[BlockSize];
                    data.Slice(blockIndex * BlockSize, len).CopyTo(dataShards[j]);
                }
                else
                {
                    dataShards[j] = new byte[BlockSize]; // zero pad block
                }
            }

            byte[][] parity = rs.Encode(dataShards);
            for (int p = 0; p < ParityShards; p++)
            {
                int parityIndex = g * ParityShards + p;
                parityBlocks[parityIndex] = parity[p];
                parityHashes[parityIndex] = SHA256.HashData(parity[p]);
            }
        }

        int totalSize = HeaderSize
            + blockCount * 32
            + parityBlockCount * 32
            + parityBlockCount * BlockSize;
        byte[] par2 = new byte[totalSize];
        Magic.CopyTo(par2, 0);
        BitConverter.GetBytes(BlockSize).CopyTo(par2, 4);
        BitConverter.GetBytes(blockCount).CopyTo(par2, 8);
        BitConverter.GetBytes(parityBlockCount).CopyTo(par2, 12);
        int offset = HeaderSize;
        for (int i = 0; i < blockCount; i++)
        {
            dataHashes[i].CopyTo(par2, offset);
            offset += 32;
        }
        for (int i = 0; i < parityBlockCount; i++)
        {
            parityHashes[i].CopyTo(par2, offset);
            offset += 32;
        }
        for (int i = 0; i < parityBlockCount; i++)
        {
            parityBlocks[i].CopyTo(par2, offset);
            offset += BlockSize;
        }
        return par2;
    }

    /// <summary>
    /// Verifies <paramref name="data"/> against its par2 companion.
    /// Never throws on malformed input: structural problems report
    /// <see cref="Par2VerifyStatus.InvalidPar2"/>.
    /// </summary>
    public static Par2VerifyResult Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> par2)
    {
        if (!TryParseHeader(par2, out int blockSize, out int blockCount, out int parityBlockCount, out string? headerError))
            return Par2VerifyResult.Invalid(headerError!);

        if (blockSize != BlockSize)
            return Par2VerifyResult.Invalid($"Unsupported block size {blockSize} (expected {BlockSize}).");

        int expectedBlockCount = (data.Length + BlockSize - 1) / BlockSize;
        if (blockCount != expectedBlockCount)
            return Par2VerifyResult.Invalid(
                $"Data length {data.Length} implies {expectedBlockCount} blocks but par2 says {blockCount}.");

        int expectedParity = ((blockCount + DataShards - 1) / DataShards) * ParityShards;
        if (parityBlockCount != expectedParity)
            return Par2VerifyResult.Invalid(
                $"Par2 has {parityBlockCount} parity blocks but {blockCount} data blocks need {expectedParity}.");

        int expectedLength = HeaderSize
            + blockCount * 32
            + parityBlockCount * 32
            + parityBlockCount * BlockSize;
        if (par2.Length != expectedLength)
            return Par2VerifyResult.Invalid(
                $"Par2 length {par2.Length} does not match its header ({expectedLength}).");

        var badData = new List<int>();
        int offset = HeaderSize;
        for (int i = 0; i < blockCount; i++)
        {
            int len = Math.Min(BlockSize, data.Length - i * BlockSize);
            byte[] actual = SHA256.HashData(data.Slice(i * BlockSize, len));
            if (!actual.SequenceEqual(par2.Slice(offset, 32)))
                badData.Add(i);
            offset += 32;
        }

        var badParity = new List<int>();
        int parityStart = offset + parityBlockCount * 32;
        for (int i = 0; i < parityBlockCount; i++)
        {
            byte[] actual = SHA256.HashData(par2.Slice(parityStart + i * BlockSize, BlockSize));
            if (!actual.SequenceEqual(par2.Slice(offset, 32)))
                badParity.Add(i);
            offset += 32;
        }

        if (badData.Count == 0 && badParity.Count == 0)
            return Par2VerifyResult.Valid();
        return Par2VerifyResult.Damaged(badData, badParity);
    }

    /// <summary>
    /// Repairs damaged data blocks in <paramref name="data"/> using the
    /// par2 companion. Returns a new array with the repaired bytes, or
    /// null when the damage is unrecoverable from this pair (too many bad
    /// blocks in a group, a bad par2 structure, or a length mismatch).
    /// The input is never modified.
    /// </summary>
    public static byte[]? Repair(ReadOnlySpan<byte> data, ReadOnlySpan<byte> par2)
    {
        Par2VerifyResult verify = Verify(data, par2);
        if (verify.Status == Par2VerifyStatus.InvalidPar2)
            return null;
        if (verify.Status == Par2VerifyStatus.Valid)
            return data.ToArray();

        // verify.Status == Damaged
        var badData = new HashSet<int>(verify.BadDataBlocks);
        var badParity = new HashSet<int>(verify.BadParityBlocks);
        int blockCount = (data.Length + BlockSize - 1) / BlockSize;
        int groups = (blockCount + DataShards - 1) / DataShards;

        byte[] repaired = data.ToArray();
        var rs = new ReedSolomon(DataShards, ParityShards);
        int parityStart = HeaderSize
            + blockCount * 32
            + ((groups * ParityShards) * 32);

        for (int g = 0; g < groups; g++)
        {
            int groupStart = g * DataShards;
            int groupBlocks = Math.Min(DataShards, blockCount - groupStart);
            var groupBad = new List<int>();
            for (int j = 0; j < groupBlocks; j++)
                if (badData.Contains(groupStart + j))
                    groupBad.Add(j);
            if (groupBad.Count == 0)
                continue;

            // Build the full shard array: present data shards (zero-padded
            // to BlockSize), missing data shards as null, present parity
            // shards, missing parity as null. Zero pad blocks beyond the
            // group are known-good and count as present.
            var shards = new byte[DataShards + ParityShards][];
            var present = new bool[DataShards + ParityShards];
            for (int j = 0; j < DataShards; j++)
            {
                int blockIndex = groupStart + j;
                if (blockIndex < blockCount)
                {
                    if (badData.Contains(blockIndex))
                    {
                        shards[j] = null!;
                        present[j] = false;
                    }
                    else
                    {
                        int len = Math.Min(BlockSize, data.Length - blockIndex * BlockSize);
                        shards[j] = new byte[BlockSize];
                        data.Slice(blockIndex * BlockSize, len).CopyTo(shards[j]);
                        present[j] = true;
                    }
                }
                else
                {
                    shards[j] = new byte[BlockSize]; // zero pad block
                    present[j] = true;
                }
            }
            for (int p = 0; p < ParityShards; p++)
            {
                int parityIndex = g * ParityShards + p;
                if (badParity.Contains(parityIndex))
                {
                    shards[DataShards + p] = null!;
                    present[DataShards + p] = false;
                }
                else
                {
                    shards[DataShards + p] = par2.Slice(parityStart + parityIndex * BlockSize, BlockSize).ToArray();
                    present[DataShards + p] = true;
                }
            }

            int presentCount = present.Count(p => p);
            if (presentCount < DataShards)
                return null; // not enough shards to reconstruct this group

            byte[][] reconstructed;
            try
            {
                reconstructed = rs.Reconstruct(shards, present);
            }
            catch
            {
                return null;
            }

            foreach (int j in groupBad)
            {
                int blockIndex = groupStart + j;
                int len = Math.Min(BlockSize, data.Length - blockIndex * BlockSize);
                reconstructed[j].AsSpan(0, len).CopyTo(repaired.AsSpan(blockIndex * BlockSize, len));
            }
        }

        // Confirm the repair actually fixed every data block before
        // returning. A still-damaged parity block does NOT fail the
        // repair: the data is what matters, and the caller re-pairs
        // (regenerates the par2) to restore the pair.
        Par2VerifyResult after = Verify(repaired, par2);
        return after.Status != Par2VerifyStatus.InvalidPar2 && after.BadDataBlocks.Count == 0
            ? repaired
            : null;
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> par2, out int blockSize, out int blockCount, out int parityBlockCount, out string? error)
    {
        blockSize = 0;
        blockCount = 0;
        parityBlockCount = 0;
        error = null;
        if (par2.Length < HeaderSize)
        {
            error = $"Par2 is {par2.Length} bytes, shorter than the {HeaderSize}-byte header.";
            return false;
        }
        if (!par2.Slice(0, 4).SequenceEqual(Magic))
        {
            error = "Par2 magic is not FKP2.";
            return false;
        }
        blockSize = BitConverter.ToInt32(par2.Slice(4, 4));
        blockCount = BitConverter.ToInt32(par2.Slice(8, 4));
        parityBlockCount = BitConverter.ToInt32(par2.Slice(12, 4));
        if (blockSize <= 0 || blockCount < 0 || parityBlockCount < 0)
        {
            error = $"Par2 header has invalid counts (blockSize={blockSize}, blockCount={blockCount}, parityBlockCount={parityBlockCount}).";
            return false;
        }
        return true;
    }
}

/// <summary>Outcome of a par2 verification.</summary>
public enum Par2VerifyStatus
{
    /// <summary>Every data block hashes correctly.</summary>
    Valid,
    /// <summary>Some blocks are damaged; see the index lists.</summary>
    Damaged,
    /// <summary>The par2 itself is malformed or does not describe this data.</summary>
    InvalidPar2,
}

/// <summary>Result of <see cref="RelayPar2.Verify"/>.</summary>
public sealed class Par2VerifyResult
{
    public required Par2VerifyStatus Status { get; init; }
    public required IReadOnlyList<int> BadDataBlocks { get; init; }
    public required IReadOnlyList<int> BadParityBlocks { get; init; }
    public string? InvalidReason { get; init; }

    public static Par2VerifyResult Valid() => new()
    {
        Status = Par2VerifyStatus.Valid,
        BadDataBlocks = Array.Empty<int>(),
        BadParityBlocks = Array.Empty<int>(),
    };

    public static Par2VerifyResult Damaged(IReadOnlyList<int> badData, IReadOnlyList<int> badParity) => new()
    {
        Status = Par2VerifyStatus.Damaged,
        BadDataBlocks = badData,
        BadParityBlocks = badParity,
    };

    public static Par2VerifyResult Invalid(string reason) => new()
    {
        Status = Par2VerifyStatus.InvalidPar2,
        BadDataBlocks = Array.Empty<int>(),
        BadParityBlocks = Array.Empty<int>(),
        InvalidReason = reason,
    };
}
