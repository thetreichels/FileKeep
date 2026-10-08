namespace UsenetBackup.Core.Redundancy;

/// <summary>
/// Reed-Solomon erasure coding for PAR2-style redundancy.
/// Splits data into N data shards + M parity shards.
/// Can reconstruct up to M missing shards from the available shards.
///
/// This is a thin adapter over the ReedSolomonFast NuGet package
/// (MIT licensed, pure managed C#). The handmade implementation it
/// replaces had a singular-matrix bug for certain erasure patterns.
/// The public API is preserved so Par2Redundancy needs no changes.
/// </summary>
public sealed class ReedSolomon
{
    private readonly ReedSolomonFast.ReedSolomon _inner;
    private readonly int _dataShards;
    private readonly int _parityShards;
    private readonly int _totalShards;

    /// <param name="dataShards">Number of data shards (e.g., 10).</param>
    /// <param name="parityShards">Number of parity shards (e.g., 3).</param>
    public ReedSolomon(int dataShards, int parityShards)
    {
        if (dataShards <= 0) throw new ArgumentOutOfRangeException(nameof(dataShards));
        if (parityShards <= 0) throw new ArgumentOutOfRangeException(nameof(parityShards));
        if (dataShards + parityShards > 256) throw new ArgumentException("Total shards cannot exceed 256.");

        _dataShards = dataShards;
        _parityShards = parityShards;
        _totalShards = dataShards + parityShards;
        _inner = new ReedSolomonFast.ReedSolomon(dataShards, parityShards);
    }

    /// <summary>
    /// Encodes data shards into parity shards.
    /// </summary>
    /// <param name="dataShards">Array of data shard byte arrays (all same length).</param>
    /// <returns>Array of parity shard byte arrays.</returns>
    public byte[][] Encode(byte[][] dataShards)
    {
        if (dataShards.Length != _dataShards)
            throw new ArgumentException($"Expected {_dataShards} data shards.");
        int shardSize = dataShards[0].Length;

        // Build full shard array: data + empty parity
        byte[][] shards = new byte[_totalShards][];
        Array.Copy(dataShards, shards, _dataShards);
        for (int i = 0; i < _parityShards; i++)
            shards[_dataShards + i] = new byte[shardSize];

        _inner.Encode(shards);

        // Extract parity shards
        byte[][] parity = new byte[_parityShards][];
        Array.Copy(shards, _dataShards, parity, 0, _parityShards);
        return parity;
    }

    /// <summary>
    /// Reconstructs missing data shards from available shards.
    /// </summary>
    /// <param name="shards">Array of all shards (data + parity); missing entries may be null.</param>
    /// <param name="shardPresent">Flags indicating which shards are present.</param>
    /// <returns>Array of reconstructed data shards.</returns>
    public byte[][] Reconstruct(byte[][] shards, bool[] shardPresent)
    {
        if (shards.Length != _totalShards)
            throw new ArgumentException($"Expected {_totalShards} shards.");
        if (shardPresent.Length != _totalShards)
            throw new ArgumentException("shardPresent length mismatch.");

        int presentCount = shardPresent.Count(p => p);
        if (presentCount < _dataShards)
            throw new InvalidOperationException(
                $"Not enough shards to reconstruct: have {presentCount}, need {_dataShards}.");

        // ReedSolomonFast.Reconstruct fills missing shards in place into
        // caller-provided buffers. Allocate buffers for missing shards.
        int shardSize = 0;
        for (int i = 0; i < _totalShards; i++)
        {
            if (shardPresent[i])
            {
                if (shards[i] is null)
                    throw new InvalidOperationException(
                        $"Shard {i} marked present but is null.");
                shardSize = shards[i]!.Length;
                break;
            }
        }
        if (shardSize == 0)
            throw new InvalidOperationException("Cannot determine shard size: no present shards.");

        byte[][] working = new byte[_totalShards][];
        for (int i = 0; i < _totalShards; i++)
        {
            if (shardPresent[i])
                working[i] = shards[i]!;
            else
                working[i] = new byte[shardSize]; // allocated, filled in place
        }

        bool[] present = (bool[])shardPresent.Clone();
        _inner.Reconstruct(working, present);

        // Return only the data shards
        byte[][] result = new byte[_dataShards][];
        Array.Copy(working, result, _dataShards);
        return result;
    }
}
