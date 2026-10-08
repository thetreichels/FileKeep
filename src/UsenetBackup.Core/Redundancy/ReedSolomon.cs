namespace UsenetBackup.Core.Redundancy;

/// <summary>
/// Reed-Solomon erasure coding for PAR2-style redundancy.
/// Splits data into N data shards + M parity shards.
/// Can reconstruct up to M missing shards from the available shards.
/// </summary>
public sealed class ReedSolomon
{
    private readonly int _dataShards;
    private readonly int _parityShards;
    private readonly int _totalShards;
    private readonly byte[,] _matrix; // Encoding matrix
    private readonly byte[,] _parityMatrix;

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

        // Build systematic encoding matrix:
        // Top rows: identity (data shards stored as-is)
        // Bottom rows: Vandermonde (parity shards)
        _matrix = new byte[_totalShards, _dataShards];
        for (int r = 0; r < _dataShards; r++)
        {
            for (int c = 0; c < _dataShards; c++)
            {
                _matrix[r, c] = (r == c) ? (byte)1 : (byte)0;
            }
        }
        for (int r = 0; r < _parityShards; r++)
        {
            for (int c = 0; c < _dataShards; c++)
            {
                // Vandermonde: (r+1)^c, using r+1 to avoid 0^0
                _matrix[_dataShards + r, c] = GaloisField.Pow((byte)(r + 1), c);
            }
        }

        // Parity matrix is the bottom rows
        _parityMatrix = new byte[_parityShards, _dataShards];
        for (int r = 0; r < _parityShards; r++)
            for (int c = 0; c < _dataShards; c++)
                _parityMatrix[r, c] = _matrix[_dataShards + r, c];
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
        foreach (var shard in dataShards)
            if (shard.Length != shardSize)
                throw new ArgumentException("All shards must be the same size.");

        var parity = new byte[_parityShards][];
        for (int i = 0; i < _parityShards; i++)
            parity[i] = new byte[shardSize];

        // Matrix multiplication: parity = parityMatrix * data
        // Loop order optimized for cache locality: iterate data shards outer,
        // bytes middle, parity shards inner. This accesses dataShards[d]
        // sequentially (good) instead of striding across 10 arrays for each
        // byte position (bad).
        for (int d = 0; d < _dataShards; d++)
        {
            byte[] dataShard = dataShards[d];
            for (int b = 0; b < shardSize; b++)
            {
                byte val = dataShard[b];
                if (val == 0) continue; // Skip zero bytes (common in padded shards)
                for (int p = 0; p < _parityShards; p++)
                {
                    parity[p][b] = GaloisField.Add(parity[p][b],
                        GaloisField.Mul(_parityMatrix[p, d], val));
                }
            }
        }
        return parity;
    }

    /// <summary>
    /// Reconstructs missing data shards.
    /// </summary>
    /// <param name="shards">Array of shard byte arrays (null for missing). Length must be totalShards.</param>
    /// <param name="shardPresent">Boolean array indicating which shards are present.</param>
    /// <returns>Reconstructed data shards (only the missing ones are filled).</returns>
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

        int shardSize = -1;
        for (int i = 0; i < _totalShards; i++)
        {
            if (shardPresent[i])
            {
                if (shards[i] is null)
                    throw new InvalidOperationException(
                        $"Shard {i} marked present but is null.");
                if (shardSize < 0)
                    shardSize = shards[i]!.Length;
                else if (shards[i]!.Length != shardSize)
                    throw new InvalidOperationException(
                        $"Shard {i} has length {shards[i]!.Length}, expected {shardSize}. " +
                        "All shards must be the same size.");
            }
        }
        if (shardSize < 0)
            throw new InvalidOperationException("No shards present.");

        // Build decode matrix from present shards
        var decodeMatrix = new byte[_dataShards, _dataShards];
        var presentIndices = new List<int>();
        for (int i = 0; i < _totalShards && presentIndices.Count < _dataShards; i++)
        {
            if (shardPresent[i])
                presentIndices.Add(i);
        }

        for (int r = 0; r < _dataShards; r++)
        {
            int shardIdx = presentIndices[r];
            for (int c = 0; c < _dataShards; c++)
                decodeMatrix[r, c] = _matrix[shardIdx, c];
        }

        // Invert the matrix
        byte[,] invMatrix = InvertMatrix(decodeMatrix);

        // Reconstruct missing data shards
        var result = new byte[_dataShards][];
        for (int d = 0; d < _dataShards; d++)
        {
            if (d < _totalShards && shardPresent[d])
            {
                result[d] = shards[d]; // Already present
            }
            else
            {
                // Reconstruct: result[d] = invMatrix[d] * presentShards
                result[d] = new byte[shardSize];
                for (int b = 0; b < shardSize; b++)
                {
                    byte sum = 0;
                    for (int r = 0; r < _dataShards; r++)
                    {
                        int shardIdx = presentIndices[r];
                        byte shardByte = shards[shardIdx][b];
                        sum = GaloisField.Add(sum,
                            GaloisField.Mul(invMatrix[d, r], shardByte));
                    }
                    result[d][b] = sum;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Inverts a square matrix over GF(2^8) using Gauss-Jordan elimination.
    /// </summary>
    private static byte[,] InvertMatrix(byte[,] matrix)
    {
        int n = matrix.GetLength(0);
        var aug = new byte[n, 2 * n];

        // Augment with identity
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
                aug[r, c] = matrix[r, c];
            aug[r, n + r] = 1;
        }

        // Gauss-Jordan
        for (int col = 0; col < n; col++)
        {
            // Find pivot
            int pivot = -1;
            for (int r = col; r < n; r++)
            {
                if (aug[r, col] != 0)
                {
                    pivot = r;
                    break;
                }
            }
            if (pivot < 0)
                throw new InvalidOperationException("Matrix is singular.");

            // Swap rows
            if (pivot != col)
            {
                for (int c = 0; c < 2 * n; c++)
                {
                    (aug[col, c], aug[pivot, c]) = (aug[pivot, c], aug[col, c]);
                }
            }

            // Scale pivot row
            byte invPivot = GaloisField.Inverse(aug[col, col]);
            for (int c = 0; c < 2 * n; c++)
                aug[col, c] = GaloisField.Mul(aug[col, c], invPivot);

            // Eliminate other rows
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                byte factor = aug[r, col];
                if (factor != 0)
                {
                    for (int c = 0; c < 2 * n; c++)
                    {
                        aug[r, c] = GaloisField.Sub(aug[r, c],
                            GaloisField.Mul(factor, aug[col, c]));
                    }
                }
            }
        }

        // Extract inverse
        var inv = new byte[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                inv[r, c] = aug[r, n + c];
        return inv;
    }
}
