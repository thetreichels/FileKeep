using System.Security.Cryptography;
using System.Text;

namespace FileKeep.Core.Relay;

/// <summary>
/// Cryptography for the iOS ↔ desktop relay (docs/ios-relay-protocol.md §2–4).
///
/// One pairing secret (32 random bytes, exchanged once via QR) derives two
/// keys by HMAC with domain labels, so a break in one use never leaks the
/// other:
///   hmacKey = HMAC-SHA256(seed, "FileKeep-Relay-HMAC-v1")
///   encKey  = HMAC-SHA256(seed, "FileKeep-Relay-ENC-v1")
///
/// HMACs are computed over fixed canonical strings, NOT over JSON bytes:
/// JSON key order and escaping differ across platforms, and the iOS side
/// must reproduce the exact bytes. Timestamps in canonical strings are
/// Unix epoch seconds (no fractional/second-format ambiguity); the JSON
/// files still carry ISO 8601 for readability. Names in canonical strings
/// must not contain line breaks (enforced at publish time; Windows file
/// names cannot contain them anyway).
/// </summary>
public static class RelayCrypto
{
    /// <summary>Pairing secret length in bytes.</summary>
    public const int PairingSecretBytes = 32;

    private static readonly byte[] HmacLabel = "FileKeep-Relay-HMAC-v1"u8.ToArray();
    private static readonly byte[] EncLabel = "FileKeep-Relay-ENC-v1"u8.ToArray();

    /// <summary>Generates a fresh pairing secret (32 random bytes).</summary>
    public static byte[] NewPairingSecret() => RandomNumberGenerator.GetBytes(PairingSecretBytes);

    /// <summary>Derives the HMAC (signing) key from the pairing secret.</summary>
    public static byte[] DeriveHmacKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != PairingSecretBytes)
            throw new ArgumentException($"Pairing secret must be {PairingSecretBytes} bytes.", nameof(seed));
        return HMACSHA256.HashData(seed.ToArray(), HmacLabel);
    }

    /// <summary>Derives the message-encryption key from the pairing secret.</summary>
    public static byte[] DeriveEncKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != PairingSecretBytes)
            throw new ArgumentException($"Pairing secret must be {PairingSecretBytes} bytes.", nameof(seed));
        return HMACSHA256.HashData(seed.ToArray(), EncLabel);
    }

    /// <summary>
    /// Canonical string for a mailbox index HMAC:
    /// <c>v1:index\n{sequence}\n{id}|{kind}|{createdUnixSeconds}|{size}\n...</c>
    /// one line per message, in index order.
    /// </summary>
    public static string IndexCanonicalString(long sequence, IReadOnlyList<RelayIndexEntry> messages)
    {
        var sb = new StringBuilder();
        sb.Append("v1:index\n");
        sb.Append(sequence).Append('\n');
        foreach (RelayIndexEntry m in messages)
        {
            sb.Append(m.Id).Append('|');
            sb.Append(m.Kind).Append('|');
            sb.Append(ToUnixSeconds(m.CreatedUtc)).Append('|');
            sb.Append(m.Size).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Computes the index HMAC (lowercase hex) for the given entries.</summary>
    public static string ComputeIndexHmac(byte[] hmacKey, long sequence, IReadOnlyList<RelayIndexEntry> messages)
    {
        string canonical = IndexCanonicalString(sequence, messages);
        return Convert.ToHexString(HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Verifies an index HMAC (constant-time comparison).</summary>
    public static bool VerifyIndexHmac(byte[] hmacKey, RelayIndex index)
    {
        if (index.Hmac is null)
            return false;
        string expected = ComputeIndexHmac(hmacKey, index.Sequence, index.Messages);
        return FixedTimeEqualsHex(expected, index.Hmac);
    }

    /// <summary>
    /// Canonical string for drop metadata HMAC:
    /// <c>v1:drop\n{token}\n{name}\n{size}\n{expiryUnixSeconds}</c>.
    /// </summary>
    public static string DropCanonicalString(string token, string name, long size, DateTimeOffset expiryUtc)
    {
        if (name.Contains('\n') || name.Contains('\r'))
            throw new ArgumentException("Drop name must not contain line breaks.", nameof(name));
        return $"v1:drop\n{token}\n{name}\n{size}\n{ToUnixSeconds(expiryUtc)}";
    }

    /// <summary>Computes the drop-metadata HMAC (lowercase hex).</summary>
    public static string ComputeDropHmac(byte[] hmacKey, string token, string name, long size, DateTimeOffset expiryUtc)
    {
        string canonical = DropCanonicalString(token, name, size, expiryUtc);
        return Convert.ToHexString(HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Verifies a drop-metadata HMAC (constant-time comparison).</summary>
    public static bool VerifyDropHmac(byte[] hmacKey, RelayDropMetadata metadata)
    {
        if (metadata.Hmac is null)
            return false;
        string expected = ComputeDropHmac(hmacKey, metadata.Token, metadata.Name, metadata.Size, metadata.ExpiryUtc);
        return FixedTimeEqualsHex(expected, metadata.Hmac);
    }

    /// <summary>
    /// Canonical string for inbox-bundle metadata HMAC:
    /// <c>v1:inbox\n{token}\n{name}\n{size}\n{createdUnixSeconds}</c>.
    /// Same shape as the drop form, with its own label so a drop
    /// signature can never validate as an inbox signature.
    /// </summary>
    public static string InboxCanonicalString(string token, string name, long size, DateTimeOffset createdUtc)
    {
        if (name.Contains('\n') || name.Contains('\r'))
            throw new ArgumentException("Inbox name must not contain line breaks.", nameof(name));
        return $"v1:inbox\n{token}\n{name}\n{size}\n{ToUnixSeconds(createdUtc)}";
    }

    /// <summary>Computes the inbox-metadata HMAC (lowercase hex).</summary>
    public static string ComputeInboxHmac(byte[] hmacKey, string token, string name, long size, DateTimeOffset createdUtc)
    {
        string canonical = InboxCanonicalString(token, name, size, createdUtc);
        return Convert.ToHexString(HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Verifies an inbox-metadata HMAC (constant-time comparison).</summary>
    public static bool VerifyInboxHmac(byte[] hmacKey, RelayInboxMetadata metadata)
    {
        if (metadata.Hmac is null)
            return false;
        string expected = ComputeInboxHmac(hmacKey, metadata.Token, metadata.Name, metadata.Size, metadata.CreatedUtc);
        return FixedTimeEqualsHex(expected, metadata.Hmac);
    }

    /// <summary>
    /// Encrypts a mailbox message body. Layout of the returned .bin:
    /// <c>nonce(12) || ciphertext || tag(16) || hmac(32)</c>, where the
    /// HMAC covers everything before it. The AES-GCM associated data is
    /// SHA-256 of the message id, binding the ciphertext to its id.
    /// </summary>
    public static byte[] EncryptMessage(byte[] encKey, byte[] hmacKey, string messageId, ReadOnlySpan<byte> plaintext)
    {
        byte[] aad = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        byte[] blob = ChunkCrypto.Encrypt(plaintext, encKey, aad);
        byte[] mac = HMACSHA256.HashData(hmacKey, blob);
        byte[] bin = new byte[blob.Length + 32];
        blob.CopyTo(bin, 0);
        mac.CopyTo(bin, blob.Length);
        return bin;
    }

    /// <summary>
    /// Decrypts a mailbox message body. Returns null on any failure
    /// (bad HMAC, bad GCM tag, malformed blob) — fail closed, no partial
    /// plaintext is ever returned.
    /// </summary>
    public static byte[]? DecryptMessage(byte[] encKey, byte[] hmacKey, string messageId, ReadOnlySpan<byte> bin)
    {
        if (bin.Length < ChunkCrypto.NonceSizeBytes + ChunkCrypto.TagSizeBytes + 32)
            return null;
        ReadOnlySpan<byte> blob = bin[..^32];
        ReadOnlySpan<byte> mac = bin[^32..];
        byte[] expected = HMACSHA256.HashData(hmacKey, blob);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac))
            return null;
        byte[] aad = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        try
        {
            return ChunkCrypto.Decrypt(blob, encKey, aad);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Unix epoch seconds for a timestamp (canonical-string form).</summary>
    public static long ToUnixSeconds(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private static bool FixedTimeEqualsHex(string expectedHex, string actualHex)
    {
        byte[] expected;
        byte[] actual;
        try
        {
            expected = Convert.FromHexString(expectedHex);
            actual = Convert.FromHexString(actualHex);
        }
        catch (FormatException)
        {
            return false;
        }
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
