using System.Buffers;
using System.Security.Cryptography;
using UsenetBackup.Core;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Tests for the zero-allocation crypto path: <see cref="ChunkCrypto.EncryptInto"/>
/// / <see cref="ChunkCrypto.DecryptInto"/> and the span-based
/// <see cref="IBlobStore.Put(string, ReadOnlySpan{byte})"/> overload used by
/// the backup hot loop.
/// </summary>
public sealed class ChunkCryptoTests
{
    private static readonly byte[] Key = KeyDerivation.DeriveKey(
        "buffer-pooling-test", KeyDerivation.NewSalt(), 10_000);
    private static readonly byte[] ChunkId32 = SHA256.HashData("chunk-id-aad"u8.ToArray());

    [Fact]
    public void EncryptInto_RoundTripsThroughDecryptInto()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(1024);
        byte[] blob = new byte[ChunkCrypto.NonceSizeBytes + plaintext.Length + ChunkCrypto.TagSizeBytes];
        ChunkCrypto.EncryptInto(plaintext, Key, ChunkId32, blob);

        byte[] recovered = new byte[plaintext.Length];
        ChunkCrypto.DecryptInto(blob, Key, ChunkId32, recovered);

        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void EncryptInto_MatchesEncryptLayout()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(777);
        byte[] blob = new byte[ChunkCrypto.NonceSizeBytes + plaintext.Length + ChunkCrypto.TagSizeBytes];
        ChunkCrypto.EncryptInto(plaintext, Key, ChunkId32, blob);

        Assert.Equal(ChunkCrypto.NonceSizeBytes + plaintext.Length + ChunkCrypto.TagSizeBytes, blob.Length);
        // The Into-produced blob must decrypt through the allocating API too.
        Assert.Equal(plaintext, ChunkCrypto.Decrypt(blob, Key, ChunkId32));
        // And vice versa.
        byte[] viaEncrypt = ChunkCrypto.Encrypt(plaintext, Key, ChunkId32);
        byte[] recovered = new byte[plaintext.Length];
        ChunkCrypto.DecryptInto(viaEncrypt, Key, ChunkId32, recovered);
        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void EncryptInto_EmptyPlaintext_RoundTrips()
    {
        byte[] blob = new byte[ChunkCrypto.NonceSizeBytes + ChunkCrypto.TagSizeBytes];
        ChunkCrypto.EncryptInto(ReadOnlySpan<byte>.Empty, Key, ChunkId32, blob);
        byte[] recovered = Array.Empty<byte>();
        ChunkCrypto.DecryptInto(blob, Key, ChunkId32, recovered);
        Assert.Empty(recovered);
    }

    [Fact]
    public void EncryptInto_RejectsWrongSizeBuffer()
    {
        byte[] plaintext = new byte[16];
        byte[] tooSmall = new byte[ChunkCrypto.NonceSizeBytes + plaintext.Length + ChunkCrypto.TagSizeBytes - 1];
        Assert.Throws<ArgumentException>(() =>
            ChunkCrypto.EncryptInto(plaintext, Key, ChunkId32, tooSmall));
    }

    [Fact]
    public void DecryptInto_RejectsWrongSizePlaintextBuffer()
    {
        byte[] plaintext = new byte[16];
        byte[] blob = ChunkCrypto.Encrypt(plaintext, Key, ChunkId32);
        byte[] wrongSize = new byte[plaintext.Length + 1];
        Assert.Throws<ArgumentException>(() =>
            ChunkCrypto.DecryptInto(blob, Key, ChunkId32, wrongSize));
    }

    [Fact]
    public void DecryptInto_FailsClosedOnTamperedBlob()
    {
        byte[] plaintext = new byte[64];
        byte[] blob = ChunkCrypto.Encrypt(plaintext, Key, ChunkId32);
        blob[ChunkCrypto.NonceSizeBytes + 3] ^= 0xFF; // flip a ciphertext bit
        byte[] recovered = new byte[plaintext.Length];
        // .NET 9+ throws AuthenticationTagMismatchException, which derives
        // from CryptographicException; ThrowsAny accepts the derived type.
        Assert.ThrowsAny<CryptographicException>(() =>
            ChunkCrypto.DecryptInto(blob, Key, ChunkId32, recovered));
    }

    [Fact]
    public void DecryptInto_FailsClosedOnShortBlob()
    {
        byte[] recovered = new byte[8];
        Assert.Throws<CryptographicException>(() =>
            ChunkCrypto.DecryptInto(new byte[10], Key, ChunkId32, recovered));
    }

    [Fact]
    public void PooledHotLoop_BackupAndRestoreRoundTrip()
    {
        // Mirrors BackupRepository.BackupStream: one pooled blob buffer reused
        // across chunks, written through the span Put overload.
        string repoDir = Path.Combine(Path.GetTempPath(), "ub-crypto-test-" + Guid.NewGuid().ToString("N"));
        var store = new LocalBlobStore(repoDir);
        const int chunkSize = 64 * 1024;
        byte[] blobBuf = ArrayPool<byte>.Shared.Rent(chunkSize + ChunkCrypto.NonceSizeBytes + ChunkCrypto.TagSizeBytes);
        try
        {
            var chunkIds = new List<string>();
            for (int c = 0; c < 4; c++)
            {
                byte[] chunk = RandomNumberGenerator.GetBytes(chunkSize);
                string chunkId = Hashing.Sha256Hex(chunk);
                var blobSpan = blobBuf.AsSpan(0, chunk.Length + ChunkCrypto.NonceSizeBytes + ChunkCrypto.TagSizeBytes);
                ChunkCrypto.EncryptInto(chunk, Key, Hashing.HexToBytes(chunkId), blobSpan);
                store.Put(chunkId, blobSpan);
                chunkIds.Add(chunkId);

                // Restore side: decrypt into a pooled buffer.
                byte[] stored = store.Get(chunkId);
                byte[] plainBuf = ArrayPool<byte>.Shared.Rent(chunkSize);
                try
                {
                    var plainSpan = plainBuf.AsSpan(0, stored.Length - ChunkCrypto.NonceSizeBytes - ChunkCrypto.TagSizeBytes);
                    ChunkCrypto.DecryptInto(stored, Key, Hashing.HexToBytes(chunkId), plainSpan);
                    Assert.Equal(chunkId, Hashing.Sha256Hex(plainSpan));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(plainBuf);
                }
            }
            Assert.Equal(4, store.StoredCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blobBuf);
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public void LocalBlobStore_SpanPut_DeduplicatesLikeArrayPut()
    {
        string repoDir = Path.Combine(Path.GetTempPath(), "ub-spanput-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalBlobStore(repoDir);
            byte[] plaintext = RandomNumberGenerator.GetBytes(100);
            byte[] blob = ChunkCrypto.Encrypt(plaintext, Key, ChunkId32);
            string chunkId = Hashing.Sha256Hex(plaintext);

            store.Put(chunkId, blob.AsSpan());
            string firstPath = store.PathFor(chunkId);
            DateTime firstWrite = File.GetLastWriteTimeUtc(firstPath);

            // Second put of the same blob is a dedup no-op.
            store.Put(chunkId, blob.AsSpan());
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(firstPath));
            Assert.Equal(blob, store.Get(chunkId));
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
