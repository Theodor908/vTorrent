using System;
using System.Collections.Generic;
using System.Numerics;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Utilities;

namespace vTorrent.Core.Download;

/// <summary>
/// Coordinates downloading merkle tree hashes from peers for BEP 52.
/// Determines which hashes to request, tracks pending/completed requests,
/// and ensures blocks aren't requested until their hashes are available.
/// Modeled after libtorrent's hash_picker.
/// </summary>
public sealed class HashPicker
{
    private const int MaxBatchSize = 512;

    private readonly IReadOnlyList<SHA256Hash> _fileRoots;
    private readonly int _blocksPerPiece;
    private readonly int _totalPieces;
    private readonly IReadOnlyList<int> _filePieceOffsets;

    private readonly HashSet<int> _receivedBlocks = new();
    private readonly HashSet<(SHA256Hash Root, int Index)> _pending = new();

    public HashPicker(
        IReadOnlyList<SHA256Hash> fileRoots,
        IReadOnlyList<int> filePieceOffsets,
        int blocksPerPiece,
        int totalPieces)
    {
        _fileRoots = fileRoots ?? throw new ArgumentNullException(nameof(fileRoots));
        _filePieceOffsets = filePieceOffsets ?? throw new ArgumentNullException(nameof(filePieceOffsets));
        _blocksPerPiece = blocksPerPiece;
        _totalPieces = totalPieces;

        if (_fileRoots.Count != _filePieceOffsets.Count)
            throw new ArgumentException("fileRoots and filePieceOffsets must have same length");
    }

    /// <summary>
    /// Pick a hash request for blocks that a peer can provide.
    /// Returns null if no hashes are needed for pieces the peer has.
    /// </summary>
    public HashRequestMessage? PickHashRequest(Bitfield peerBitfield)
    {
        for (int piece = 0; piece < _totalPieces; piece++)
        {
            if (!peerBitfield.HasPiece(piece)) continue;
            if (HasBlockHashes(piece)) continue;

            var globalBlockStart = piece * _blocksPerPiece;
            var fileRoot = FindFileRoot(piece);
            var key = (fileRoot, globalBlockStart);

            if (_pending.Contains(key)) continue;

            var batchSize = Math.Min(MaxBatchSize, _blocksPerPiece);
            batchSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, batchSize));

            var proofLayers = ComputeProofLayers(batchSize);

            _pending.Add(key);

            return new HashRequestMessage
            {
                PiecesRoot = fileRoot,
                BaseLayer = 0,
                Index = globalBlockStart,
                Length = batchSize,
                ProofLayers = proofLayers,
            };
        }

        return null;
    }

    /// <summary>
    /// Check if we have all block-level hashes needed to verify a piece.
    /// </summary>
    public bool HasBlockHashes(int pieceIndex)
    {
        var start = pieceIndex * _blocksPerPiece;
        for (int b = 0; b < _blocksPerPiece; b++)
        {
            if (!_receivedBlocks.Contains(start + b))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Mark a range of block hashes as received and validated.
    /// </summary>
    public void MarkHashesReceived(SHA256Hash root, int startIndex, int count)
    {
        for (int i = 0; i < count; i++)
            _receivedBlocks.Add(startIndex + i);

        _pending.Remove((root, startIndex));
    }

    /// <summary>
    /// Handle a hash reject — remove from pending so it can be re-requested.
    /// </summary>
    public void OnHashRejected(HashRequestMessage msg)
    {
        _pending.Remove((msg.PiecesRoot, msg.Index));
    }

    private SHA256Hash FindFileRoot(int pieceIndex)
    {
        // Binary search: find the last offset that is <= pieceIndex
        int lo = 0, hi = _filePieceOffsets.Count - 1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (_filePieceOffsets[mid] <= pieceIndex)
                lo = mid;
            else
                hi = mid - 1;
        }
        return _fileRoots[lo];
    }

    private static int ComputeProofLayers(int batchSize)
    {
        return Math.Max(1, BitOperations.Log2((uint)batchSize));
    }
}
