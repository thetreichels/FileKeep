using System.Collections.Generic;

namespace UsenetBackup.Core;

/// <summary>
/// Content-defined chunker based on a Gear rolling hash.
/// Chunk boundaries are a pure function of the byte stream: a cut is made
/// when the rolling hash hits a mask target (after a minimum size) or when
/// the maximum size is reached. Inserting or deleting bytes therefore only
/// disturbs chunk boundaries near the edit — unlike fixed-size chunking,
/// where one inserted byte shifts every later boundary and changes every
/// chunk hash. Chunk IDs remain SHA-256 of plaintext, so deduplication,
/// encryption, restore and verification are chunking-scheme agnostic.
///
/// The Gear table is a fixed constant (SplitMix64 output with a fixed
/// seed). It MUST NOT change between versions: changing it would change
/// every chunk boundary and silently break deduplication against
/// previously stored chunks.
/// </summary>
public sealed class CdcChunker
{
    // Fixed Gear table: 256 pseudo-random 32-bit values (SplitMix64,
    // seed 0x243F6A8885A308D3). Generated once; embedded as constants.
    private static readonly uint[] GearTable = new uint[256]
    {
            0x4ABEA221u, 0x23148989u, 0x609DFE03u, 0xDEB12800u,
            0x6C442CB6u, 0xC7E4F8C4u, 0xF8FBA7E4u, 0x3CDB9EEAu,
            0xF9520068u, 0xE116868Bu, 0xA2023CBDu, 0xA37B51B9u,
            0x524F3905u, 0x6CA3B276u, 0x5104E85Au, 0x9FD533B3u,
            0x50AB4B56u, 0x80FE62C5u, 0xA6BCEDC7u, 0x332C7C88u,
            0x20BD47DAu, 0xE0BB7C4Fu, 0x434A346Du, 0x60416D7Fu,
            0xE0E10A5Eu, 0xD064E22Fu, 0x07DF3AF2u, 0x89EA30E4u,
            0xCC6C522Bu, 0x4E37087Cu, 0x9F51F27Bu, 0xCEE481EAu,
            0x3C59A1F1u, 0x651EA4DAu, 0x8FC01AC6u, 0xA31B717Cu,
            0x9797D3A2u, 0xC4AC1880u, 0x42C28A7Du, 0x64327351u,
            0xD9E9C06Du, 0x46524825u, 0x62A51616u, 0xC53F2B3Au,
            0x94B9C33Eu, 0x2447E8ABu, 0x2A1A245Eu, 0x7D7B3B98u,
            0x4536912Fu, 0x6CF9881Au, 0x99F932DAu, 0xAF93E7F0u,
            0x1E7F46ABu, 0x3EDDB4A0u, 0x14554798u, 0xEF84BDE2u,
            0x1FEFB566u, 0xC7BF19F1u, 0x3D4A8EC5u, 0xBA62DCCBu,
            0xF530844Bu, 0x4D57959Cu, 0xFE90B68Eu, 0xA696FBD3u,
            0x85A45644u, 0x1520DD73u, 0xAE0F8464u, 0x4B8B735Cu,
            0xC2774058u, 0x9A613341u, 0x7390FD42u, 0x8A8CAE7Cu,
            0x163B976Fu, 0x1926A565u, 0x4C38672Au, 0x9AA1FF24u,
            0xC01F36EDu, 0x92E313EFu, 0x8AAB8B70u, 0x22EA0CBFu,
            0x2F21BAA6u, 0xE36F3046u, 0xDBF0F0C0u, 0xD89B430Bu,
            0x6EFD6C18u, 0xB6B9345Eu, 0x6EE5762Bu, 0x7932DCBDu,
            0x741FE7D9u, 0xEDFFC430u, 0x842100A4u, 0xF4E09A16u,
            0x17C97212u, 0x69C8B3ACu, 0x4DCEE280u, 0x78000997u,
            0x56756763u, 0x719DC103u, 0xE0DF3FEEu, 0xC72F7146u,
            0x9149CBBAu, 0x6FD6612Du, 0x650FA766u, 0xCAC3ADC2u,
            0x5CE17A0Cu, 0xE2191DF9u, 0x57DA4AEFu, 0x3367F9DFu,
            0xFBD33CAEu, 0xB64A8FA9u, 0x1BA45656u, 0xB6EE2C5Fu,
            0xD69B05CCu, 0x71A39E83u, 0x4081919Au, 0x0BF14A28u,
            0x4E040F10u, 0x7ADFC8B3u, 0x276C88E4u, 0x20811612u,
            0xB0B2D28Cu, 0x830215A8u, 0x78D039FEu, 0x6E08C0B5u,
            0xE905324Fu, 0x1E945862u, 0x439547EEu, 0x8A7E3EF0u,
            0xD40D6E72u, 0x1FA25861u, 0xBF0BC4A3u, 0x63CE82B4u,
            0xE14404E4u, 0xFF1A68CEu, 0x224E0D71u, 0xBCDDE6B5u,
            0xD5B688CDu, 0x0CEA70BAu, 0xC2286C53u, 0xBAA5C994u,
            0x8191BD29u, 0x38B555D6u, 0x411E6D7Au, 0x806E77F0u,
            0x456B20B8u, 0x1A13CABDu, 0x44F1E484u, 0xA5491F08u,
            0x95A32B72u, 0x04AEA72Au, 0x6E07D38Du, 0xC5069EFCu,
            0xF21852DFu, 0xFF5B74A8u, 0xC9093294u, 0x02158220u,
            0xB6811522u, 0xB526A978u, 0x7AFA2772u, 0x2B45D376u,
            0x86BD2CBBu, 0x8ED84107u, 0x6CB535EFu, 0xC82D1E2Au,
            0xA01EF5ACu, 0xFD0B27E8u, 0x97C510EAu, 0x1861575Fu,
            0x190C3646u, 0x230B6282u, 0x99509C3Au, 0xDB7B532Bu,
            0x4924350Au, 0x5D4B823Eu, 0x9C1F45DAu, 0x6A846E20u,
            0xCD9BBF45u, 0x113C68AEu, 0x6F484328u, 0x37B72076u,
            0xC7108BA6u, 0x3D5C0CA0u, 0x73E81D09u, 0x250BC6B9u,
            0x9A68740Bu, 0x20F932CCu, 0x01EE6D4Eu, 0x90F3F2EFu,
            0x1485BD62u, 0xE6F9B6DEu, 0xF28C5483u, 0x70076B77u,
            0xA7A448F6u, 0x95FEB53Au, 0xA7803150u, 0x3D21581Cu,
            0x7A85A239u, 0x73B129AEu, 0xCFE53920u, 0x973F9B5Fu,
            0x83C64F11u, 0x19E23471u, 0x98FE1E04u, 0xB39C8CD9u,
            0xAB7AEE65u, 0x01A7469Eu, 0x42D2E216u, 0x39485E0Cu,
            0x6EEDD5EDu, 0x5142564Du, 0x2E70F466u, 0x526D6EE5u,
            0xA0E0C8A9u, 0x36D9566Bu, 0x730A5BDFu, 0x6877EA75u,
            0x8DE61FC1u, 0x7A2F1241u, 0xFEFA8BC5u, 0xEFD63F9Au,
            0x43CC4204u, 0x0D63AB58u, 0xB38B81B5u, 0x269E9752u,
            0x0AA2C9E6u, 0x0640C9CBu, 0x83C9456Eu, 0xE128036Cu,
            0x6BB0F074u, 0x44846ECAu, 0x3F05A58Bu, 0x81E03A5Au,
            0x4B758485u, 0xF005A534u, 0x2AA99D70u, 0xC1397E0Fu,
            0x86DE6E03u, 0x59388A9Fu, 0xE4266EE2u, 0x412EAE2Du,
            0x4F9B080Eu, 0xFC776923u, 0x5D4DADE2u, 0xC3B36609u,
            0x7F964EF1u, 0x9B276C63u, 0x69A70CFBu, 0x05A819C8u,
            0x3FB26034u, 0x2E8578DDu, 0x6D8701DFu, 0x854B1DFCu,
            0x644845D6u, 0xE347F3D0u, 0x1941872Bu, 0xBA84472Eu,
    };

    public int MinSize { get; }
    public int MaxSize { get; }

    /// <summary>Bytes accumulated in the currently open chunk.</summary>
    public int CurrentLength { get; private set; }

    private readonly uint _mask;
    private uint _hash;

    public CdcChunker(int minSize, int avgSize, int maxSize)
    {
        if (minSize < 4096)
            throw new ArgumentOutOfRangeException(nameof(minSize), "CDC minimum chunk size must be >= 4 KiB.");
        if (avgSize <= minSize)
            throw new ArgumentOutOfRangeException(nameof(avgSize), "CDC target chunk size must exceed the minimum.");
        if (maxSize <= avgSize)
            throw new ArgumentOutOfRangeException(nameof(maxSize), "CDC maximum chunk size must exceed the target.");
        if ((avgSize & (avgSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(avgSize), "CDC target chunk size must be a power of two.");
        MinSize = minSize;
        MaxSize = maxSize;
        _mask = (uint)(avgSize - 1);
    }

    /// <summary>
    /// Feeds bytes through the rolling hash. Every time a chunk boundary is
    /// reached, the completed chunk's length is appended to
    /// <paramref name="completedChunkLengths"/> (lengths are relative to the
    /// stream: each is the length of one chunk, in order). The caller owns the
    /// bytes; the chunker only reports boundaries.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, List<int> completedChunkLengths)
    {
        ArgumentNullException.ThrowIfNull(completedChunkLengths);
        foreach (byte b in data)
        {
            _hash = (_hash << 1) + GearTable[b];
            CurrentLength++;
            if (CurrentLength >= MaxSize)
            {
                // Hard cap: bounds memory no matter what the content looks like.
                completedChunkLengths.Add(CurrentLength);
                CurrentLength = 0;
                _hash = 0;
            }
            else if (CurrentLength >= MinSize && (_hash & _mask) == 0)
            {
                completedChunkLengths.Add(CurrentLength);
                CurrentLength = 0;
                _hash = 0;
            }
        }
    }

    /// <summary>
    /// Ends the stream: the trailing open chunk (if any bytes were fed since
    /// the last boundary) is reported. A chunk shorter than <see cref="MinSize"/>
    /// is fine here — the minimum only suppresses cuts mid-stream.
    /// </summary>
    public void Finish(List<int> completedChunkLengths)
    {
        ArgumentNullException.ThrowIfNull(completedChunkLengths);
        if (CurrentLength > 0)
        {
            completedChunkLengths.Add(CurrentLength);
            CurrentLength = 0;
            _hash = 0;
        }
    }

    public void Reset()
    {
        CurrentLength = 0;
        _hash = 0;
    }
}
