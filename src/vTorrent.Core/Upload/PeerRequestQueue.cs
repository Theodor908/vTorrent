using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Upload;

/// <summary>
/// Per-peer bounded request queue. Decouples request arrival from serving.
/// Max depth prevents a single peer from flooding the upload path.
/// </summary>
public sealed class PeerRequestQueue
{
    private readonly ConcurrentQueue<BlockRequest> _queue = new();
    private readonly int _maxDepth;
    private int _count;

    public PeerRequestQueue(int maxDepth = 250)
    {
        _maxDepth = maxDepth;
    }

    public int Count => Volatile.Read(ref _count);
    public bool IsEmpty => _queue.IsEmpty;

    public bool Enqueue(BlockRequest request)
    {
        // Increment first to atomically reserve a slot — avoids TOCTOU race
        var newCount = Interlocked.Increment(ref _count);
        if (newCount > _maxDepth)
        {
            Interlocked.Decrement(ref _count);
            return false;
        }

        _queue.Enqueue(request);
        return true;
    }

    public bool TryDequeue(out BlockRequest request)
    {
        if (_queue.TryDequeue(out request))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }
        return false;
    }

    public void Cancel(int pieceIndex, int begin)
    {
        var snapshot = new List<BlockRequest>();
        while (_queue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            if (item.PieceIndex != pieceIndex || item.Begin != begin)
                snapshot.Add(item);
        }
        foreach (var item in snapshot)
        {
            _queue.Enqueue(item);
            Interlocked.Increment(ref _count);
        }
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }
}
