using System.Security.Cryptography;

namespace UsenetBackup.Core;

/// <summary>SHA-256 helpers. Every hash in BACKUP FORMAT v1 is SHA-256, lowercase hex.</summary>
public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    public static string Sha256Hex(Stream stream)
    {
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static byte[] Sha256Bytes(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] HexToBytes(string hex) => Convert.FromHexString(hex);
}
