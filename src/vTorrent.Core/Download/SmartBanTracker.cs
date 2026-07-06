using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Download;

/// <summary>
/// Tracks per-block hashes and source peers for smart banning.
/// When a piece fails, records are kept. When a piece succeeds,
/// compares correct block hashes against stored hashes to identify
/// which peers sent corrupt data. Based on libtorrent's smart ban algorithm.
/// </summary>
public class SmartBanTracker
{
    private readonly ConcurrentDictionary<(int pieceIndex, int begin), List<BlockRecord>> _records = new();

    public bool HasRecords(int pieceIndex)
    {
        foreach (var key in _records.Keys)
        {
            if (key.pieceIndex == pieceIndex)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Records a block's hash and source peer. If the same peer sends
    /// different data for the same block, returns ShouldBanPeer=true.
    /// </summary>
    public SmartBanResult RecordBlock(int pieceIndex, int begin, byte[] blockData, IPeerConnection peer)
    {
        var key = (pieceIndex, begin);
        var hash = SHA1.HashData(blockData);

        var records = _records.GetOrAdd(key, _ => new List<BlockRecord>());
        lock (records)
        {
            foreach (var existing in records)
            {
                if (existing.Peer == peer && !existing.Hash.AsSpan().SequenceEqual(hash))
                {
                    return new SmartBanResult { ShouldBanPeer = true };
                }
            }

            records.Add(new BlockRecord { Peer = peer, Hash = hash });
        }

        return new SmartBanResult { ShouldBanPeer = false };
    }

    /// <summary>
    /// Called when a piece is successfully verified. Compares correct block
    /// hashes against stored records and returns peers that sent bad data.
    /// Clears records for this piece after comparison.
    /// </summary>
    public List<IPeerConnection> OnPieceVerified(int pieceIndex, IEnumerable<(int begin, byte[] data)> correctBlocks)
    {
        var badPeers = new List<IPeerConnection>();

        foreach (var (begin, data) in correctBlocks)
        {
            var key = (pieceIndex, begin);
            if (!_records.TryGetValue(key, out var records))
                continue;

            var correctHash = SHA1.HashData(data);

            lock (records)
            {
                foreach (var record in records)
                {
                    if (!record.Hash.AsSpan().SequenceEqual(correctHash))
                    {
                        badPeers.Add(record.Peer);
                    }
                }
            }
        }

        ClearRecords(pieceIndex);
        return badPeers;
    }

    /// <summary>
    /// Called when a piece fails hash verification. Keeps records for
    /// comparison when the piece eventually succeeds.
    /// </summary>
    public void OnPieceFailed(int pieceIndex)
    {
        // Records are intentionally kept - they'll be compared when the piece eventually succeeds
    }

    private void ClearRecords(int pieceIndex)
    {
        var keysToRemove = new List<(int, int)>();
        foreach (var key in _records.Keys)
        {
            if (key.pieceIndex == pieceIndex)
                keysToRemove.Add(key);
        }
        foreach (var key in keysToRemove)
            _records.TryRemove(key, out _);
    }

    private class BlockRecord
    {
        public required IPeerConnection Peer { get; init; }
        public required byte[] Hash { get; init; }
    }
}

public struct SmartBanResult
{
    public bool ShouldBanPeer { get; init; }
}
