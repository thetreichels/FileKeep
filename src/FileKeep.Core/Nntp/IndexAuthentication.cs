using System.Security.Cryptography;
using System.Text;

namespace FileKeep.Core.Nntp;

/// <summary>
/// Authenticates the two versioned Usenet index articles (the chunk
/// message-identity index and the monthly manifest index) with the
/// repo key, mirroring the DL-8 manifest pattern: a labeled subkey
/// SHA-256(repo key ‖ domain label) per index, HMAC-SHA-256 over the
/// canonical payload.
///
/// Before this, both indexes were plaintext and unauthenticated, and
/// adoption was highest-version-wins: anyone able to post to the
/// newsgroup (repo IDs and chunk IDs are public — they appear in every
/// article) could publish a forged higher-version chunk index that a
/// syncing machine would adopt AND persist over its local index
/// (B-H1), or a forged manifest index that hides real backups from
/// discovery (B-M1). The HMAC makes a forged index indistinguishable
/// from no index at all: keyed readers treat a missing or invalid
/// HMAC as "not there" and never adopt or persist it.
///
/// Instances are handed to <see cref="NntpBlobStore"/> by callers that
/// hold an open <see cref="BackupRepository"/> (which derives them
/// from its key — see <see cref="BackupRepository.IndexAuth"/>). Only
/// the derived subkeys live here, never the raw repo key, so a store
/// can authenticate indexes and nothing else.
/// </summary>
public sealed class IndexAuth
{
    private static readonly byte[] ChunkIndexDomainLabel =
        Encoding.UTF8.GetBytes("FileKeep chunk message index v1");
    private static readonly byte[] ManifestIndexDomainLabel =
        Encoding.UTF8.GetBytes("FileKeep manifest index v1");

    private readonly byte[] _chunkIndexKey;
    private readonly byte[] _manifestIndexKey;

    private IndexAuth(byte[] chunkIndexKey, byte[] manifestIndexKey)
    {
        _chunkIndexKey = chunkIndexKey;
        _manifestIndexKey = manifestIndexKey;
    }

    /// <summary>
    /// Derives the per-index subkeys from the repository key:
    /// SHA-256(repo key ‖ domain label) — a labeled subkey, so the raw
    /// repo key is never used directly as an HMAC key (DL-8 pattern).
    /// </summary>
    public static IndexAuth FromRepoKey(byte[] repoKey)
    {
        ArgumentNullException.ThrowIfNull(repoKey);
        return new IndexAuth(
            DeriveSubkey(repoKey, ChunkIndexDomainLabel),
            DeriveSubkey(repoKey, ManifestIndexDomainLabel));
    }

    private static byte[] DeriveSubkey(byte[] repoKey, byte[] domainLabel)
    {
        byte[] input = new byte[repoKey.Length + domainLabel.Length];
        Buffer.BlockCopy(repoKey, 0, input, 0, repoKey.Length);
        Buffer.BlockCopy(domainLabel, 0, input, repoKey.Length, domainLabel.Length);
        return SHA256.HashData(input);
    }

    /// <summary>HMAC-SHA-256 (hex) over the canonical chunk-index JSON.</summary>
    internal string ComputeChunkIndexHmac(string canonicalIndexJson) =>
        Convert.ToHexString(HMACSHA256.HashData(
            _chunkIndexKey, Encoding.UTF8.GetBytes(canonicalIndexJson))).ToLowerInvariant();

    /// <summary>Fixed-time verification of a chunk-index HMAC (null/garbled = false).</summary>
    internal bool VerifyChunkIndexHmac(string canonicalIndexJson, string? hmacHex) =>
        Verify(_chunkIndexKey, canonicalIndexJson, hmacHex);

    /// <summary>HMAC-SHA-256 (hex) over the canonical manifest-index payload (IDs joined by "\n").</summary>
    internal string ComputeManifestIndexHmac(string canonicalPayload) =>
        Convert.ToHexString(HMACSHA256.HashData(
            _manifestIndexKey, Encoding.UTF8.GetBytes(canonicalPayload))).ToLowerInvariant();

    /// <summary>Fixed-time verification of a manifest-index HMAC (null/garbled = false).</summary>
    internal bool VerifyManifestIndexHmac(string canonicalPayload, string? hmacHex) =>
        Verify(_manifestIndexKey, canonicalPayload, hmacHex);

    private static bool Verify(byte[] subkey, string payload, string? hmacHex)
    {
        if (string.IsNullOrEmpty(hmacHex))
            return false;
        byte[] expected = HMACSHA256.HashData(subkey, Encoding.UTF8.GetBytes(payload));
        byte[] actual;
        try { actual = Convert.FromHexString(hmacHex); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
