using vTorrent.Bencode.Torrents;
using vTorrent.Core.Merkle;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace vTorrent.Core.PieceIO;

public class PieceVerifier
{
    private readonly TorrentInfo _torrentInfo;
    private readonly IReadOnlyList<MerkleTree>? _merkleTrees;
    private readonly (int fileIndex, int localBlock)[]? _globalBlockMap;

    public PieceVerifier(TorrentInfo torrentInfo, IReadOnlyList<MerkleTree>? merkleTrees = null)
    {
        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
        _merkleTrees = merkleTrees;

        if (torrentInfo.Version != TorrentVersion.V1 && torrentInfo.Files.Count > 1)
        {
            _globalBlockMap = BuildGlobalBlockMap(torrentInfo);
        }
    }

    /// <summary>
    /// Verify a complete piece. Returns bool for backward compatibility.
    /// </summary>
    public bool VerifyPiece(int pieceIndex, byte[] data)
        => VerifyPieceResult(pieceIndex, data) == PieceVerifyResult.Valid;

    /// <summary>
    /// Verify a complete piece with detailed result.
    /// For hybrid torrents, detects V1/V2 inconsistency.
    /// </summary>
    public PieceVerifyResult VerifyPieceResult(int pieceIndex, byte[] data)
    {
        if (data is null || data.Length == 0)
            return PieceVerifyResult.CorruptV1;

        return _torrentInfo.Version switch
        {
            TorrentVersion.V1 => VerifyV1(pieceIndex, data)
                ? PieceVerifyResult.Valid : PieceVerifyResult.CorruptV1,

            TorrentVersion.V2 => VerifyV2Piece(pieceIndex, data)
                ? PieceVerifyResult.Valid : PieceVerifyResult.CorruptV2,

            TorrentVersion.Hybrid => VerifyHybridDetailed(pieceIndex, data),

            _ => PieceVerifyResult.CorruptV1
        };
    }

    private PieceVerifyResult VerifyHybridDetailed(int pieceIndex, byte[] data)
    {
        var v1Pass = VerifyV1(pieceIndex, data);
        var v2Pass = VerifyV2Piece(pieceIndex, data);

        if (v1Pass && v2Pass) return PieceVerifyResult.Valid;
        if (!v1Pass && !v2Pass) return PieceVerifyResult.CorruptV1;

        // One passed, other failed — metadata integrity issue
        return PieceVerifyResult.Inconsistent;
    }

    /// <summary>
    /// Verify a single 16KiB block against its merkle leaf hash (v2 only).
    /// </summary>
    public bool VerifyBlock(int fileIndex, int blockIndex, byte[] data)
    {
        if (_merkleTrees is null || fileIndex < 0 || fileIndex >= _merkleTrees.Count)
            return false;

        var hash = new SHA256Hash(SHA256.HashData(data));
        return _merkleTrees[fileIndex].ValidateBlock(blockIndex, hash);
    }

    // --- V1 path (unchanged logic) ---

    private bool VerifyV1(int pieceIndex, byte[] data)
    {
        if (_torrentInfo.Pieces is null) return false;
        if (pieceIndex < 0 || pieceIndex >= _torrentInfo.Pieces.Count) return false;

        var expectedHash = _torrentInfo.Pieces.GetPieceHash(pieceIndex);
        if (expectedHash.Length != 20) return false;

        var actualHash = SHA1.HashData(data);
        return expectedHash.SequenceEqual(actualHash);
    }

    // --- V2 path ---

    private bool VerifyV2Piece(int pieceIndex, byte[] data)
    {
        if (_merkleTrees is null) return false;

        var blockSize = MerkleHelpers.BlockSize;
        var blocksInPiece = (int)((_torrentInfo.PieceLength + blockSize - 1) / blockSize);

        var globalBlockStart = pieceIndex * blocksInPiece;

        for (int b = 0; b < blocksInPiece; b++)
        {
            var offset = b * blockSize;
            if (offset >= data.Length) break;

            var length = Math.Min(blockSize, data.Length - offset);
            var blockData = data.AsSpan(offset, length);
            var hash = new SHA256Hash(SHA256.HashData(blockData));

            var (fileIndex, localBlock) = MapGlobalBlockToFile(globalBlockStart + b);
            if (fileIndex < 0 || fileIndex >= _merkleTrees.Count) return false;

            if (!_merkleTrees[fileIndex].ValidateBlock(localBlock, hash))
                return false;
        }

        return true;
    }

    private (int fileIndex, int localBlock) MapGlobalBlockToFile(int globalBlock)
    {
        if (_globalBlockMap is not null)
        {
            if (globalBlock < 0 || globalBlock >= _globalBlockMap.Length)
                return (-1, -1);
            return _globalBlockMap[globalBlock];
        }

        // Single-file fast path (unchanged)
        if (_torrentInfo.Files.Count == 1)
            return (0, globalBlock);

        // Fallback linear scan
        var blockSize = MerkleHelpers.BlockSize;
        int accumulated = 0;
        for (int f = 0; f < _torrentInfo.Files.Count; f++)
        {
            var fileBlocks = (int)((_torrentInfo.Files[f].Length + blockSize - 1) / blockSize);
            if (fileBlocks == 0) fileBlocks = 1;
            if (globalBlock < accumulated + fileBlocks)
                return (f, globalBlock - accumulated);
            accumulated += fileBlocks;
        }

        return (-1, -1);
    }

    private static (int fileIndex, int localBlock)[] BuildGlobalBlockMap(TorrentInfo info)
    {
        var blockSize = MerkleHelpers.BlockSize;
        int totalBlocks = 0;
        for (int f = 0; f < info.Files.Count; f++)
        {
            var fileBlocks = (int)((info.Files[f].Length + blockSize - 1) / blockSize);
            if (fileBlocks == 0) fileBlocks = 1;
            totalBlocks += fileBlocks;
        }

        var map = new (int, int)[totalBlocks];
        int accumulated = 0;
        for (int f = 0; f < info.Files.Count; f++)
        {
            var fileBlocks = (int)((info.Files[f].Length + blockSize - 1) / blockSize);
            if (fileBlocks == 0) fileBlocks = 1;
            for (int b = 0; b < fileBlocks; b++)
                map[accumulated + b] = (f, b);
            accumulated += fileBlocks;
        }

        return map;
    }

    internal (int fileIndex, int localBlock) MapGlobalBlockToFilePublic(int globalBlock)
        => MapGlobalBlockToFile(globalBlock);

    // --- Legacy accessor (kept for existing callers) ---

    public ReadOnlySpan<byte> GetPieceHash(int pieceIndex)
    {
        if (_torrentInfo.Pieces is null) return ReadOnlySpan<byte>.Empty;
        if (pieceIndex < 0 || pieceIndex >= _torrentInfo.Pieces.Count)
            return ReadOnlySpan<byte>.Empty;
        return _torrentInfo.Pieces.GetPieceHash(pieceIndex);
    }
}
