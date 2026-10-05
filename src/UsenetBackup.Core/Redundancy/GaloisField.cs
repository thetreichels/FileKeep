namespace UsenetBackup.Core.Redundancy;

/// <summary>
/// Galois Field GF(2^8) arithmetic for Reed-Solomon error correction.
/// Uses the AES irreducible polynomial x^8 + x^4 + x^3 + x + 1 (0x11B).
/// Addition is XOR. Multiplication/division use log/exp tables.
/// </summary>
public static class GaloisField
{
    private const int FieldSize = 256;
    // Primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D).
    // 0x11B (AES) does not give a primitive element at 2; 0x11D does.
    private const int PrimitivePoly = 0x11D;

    private static readonly byte[] ExpTable = new byte[512];
    private static readonly byte[] LogTable = new byte[256];

    static GaloisField()
    {
        // Build exp/log tables
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            ExpTable[i] = (byte)x;
            LogTable[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
                x ^= PrimitivePoly;
        }
        // Duplicate for easy modulo
        for (int i = 255; i < 512; i++)
            ExpTable[i] = ExpTable[i - 255];
    }

    /// <summary>Addition in GF(2^8) is XOR.</summary>
    public static byte Add(byte a, byte b) => (byte)(a ^ b);

    /// <summary>Subtraction in GF(2^8) is XOR (same as addition).</summary>
    public static byte Sub(byte a, byte b) => (byte)(a ^ b);

    /// <summary>Multiplication in GF(2^8).</summary>
    public static byte Mul(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        int logA = LogTable[a];
        int logB = LogTable[b];
        return ExpTable[logA + logB];
    }

    /// <summary>Division in GF(2^8).</summary>
    public static byte Div(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException("Division by zero in GF(2^8).");
        if (a == 0) return 0;
        int logA = LogTable[a];
        int logB = LogTable[b];
        int diff = logA - logB;
        if (diff < 0) diff += 255;
        return ExpTable[diff];
    }

    /// <summary>a^n in GF(2^8).</summary>
    public static byte Pow(byte a, int n)
    {
        if (n == 0) return 1;
        if (a == 0) return 0;
        int logA = LogTable[a];
        int logResult = (logA * n) % 255;
        return ExpTable[logResult];
    }

    /// <summary>Multiplicative inverse in GF(2^8).</summary>
    public static byte Inverse(byte a)
    {
        if (a == 0) throw new DivideByZeroException("Inverse of zero.");
        return ExpTable[255 - LogTable[a]];
    }
}
