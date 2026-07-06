using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using vTorrent.Core.Utilities;

namespace vTorrent.Core.Upload;

/// <summary>
/// Per-peer read-ahead queue. Holds pre-read 16 KiB blocks in ArrayPool-backed buffers.
/// Guided sizing scales the target block count proportional to the peer's upload rate.
/// </summary>
internal sealed class PeerSendBuffer : IDisposable
{
    private const int BlockSize = 16384;

    private readonly ConcurrentQueue<SendBufferEntry> _blocks = new();
    private long _bufferedBytes;
    private readonly int _lowWatermarkBytes;
    private readonly int _factor;
    private readonly ThroughputMeter _uploadMeter = new();

    // Read-ahead position tracking
    internal int NextPieceIndex;
    internal int NextBlockOffset;

    // Async drain signal — wakes read-ahead loop when blocks are consumed
    private readonly SemaphoreSlim _drainSignal = new(0, 1);

    public long BufferedBytes => Interlocked.Read(ref _bufferedBytes);
    public int BlockCount => _blocks.Count;
    public ThroughputMeter UploadMeter => _uploadMeter;
    public SemaphoreSlim DrainSignal => _drainSignal;

    private int _watermark = 10 * 1024; // Start at low watermark (10 KB)

    /// <summary>
    /// Dynamic send buffer watermark. Recomputed each rechoke cycle based on peer upload rate.
    /// </summary>
    public int Watermark => Volatile.Read(ref _watermark);

    /// <summary>
    /// True if buffered bytes exceed the dynamic watermark — dispatch should pause for this peer.
    /// </summary>
    public bool IsOverWatermark => BufferedBytes > Watermark;

    /// <summary>
    /// Recalculate watermark based on current upload rate.
    /// Called during rechoke cycle (every 15s).
    /// </summary>
    public void RecalculateWatermark()
    {
        const int low = 10 * 1024;      // 10 KB floor
        const int high = 500 * 1024;    // 500 KB ceiling
        var rate = _uploadMeter.BytesPerSecond;
        var factor = (int)(rate * 0.5);
        Volatile.Write(ref _watermark, Math.Clamp(factor, low, high));
    }

    public PeerSendBuffer(int lowWatermarkBytes, int factor)
    {
        _lowWatermarkBytes = Math.Max(lowWatermarkBytes, BlockSize);
        _factor = Math.Clamp(factor, 1, 300);
    }

    public void Enqueue(SendBufferEntry entry)
    {
        _blocks.Enqueue(entry);
        Interlocked.Add(ref _bufferedBytes, entry.Length);
    }

    /// <summary>
    /// Try to dequeue the head block if it matches the requested position.
    /// Returns false (miss) if the queue is empty or head doesn't match.
    /// </summary>
    public bool TryDequeue(int pieceIndex, int begin, int length, out SendBufferEntry entry)
    {
        if (_blocks.TryPeek(out var head) &&
            head.PieceIndex == pieceIndex &&
            head.Begin == begin)
        {
            if (_blocks.TryDequeue(out entry))
            {
                Interlocked.Add(ref _bufferedBytes, -entry.Length);
                return true;
            }
        }

        entry = default;
        return false;
    }

    /// <summary>
    /// Calculate the target number of blocks for this peer based on guided sizing.
    /// </summary>
    public int CalculateTargetBlocks(long uploadRateBytesPerSec, long maxWatermarkBytes)
    {
        var rateTarget = uploadRateBytesPerSec * _factor / 100;
        var targetBytes = Math.Clamp(rateTarget, _lowWatermarkBytes, maxWatermarkBytes);
        return Math.Max(1, (int)(targetBytes / BlockSize));
    }

    /// <summary>
    /// Drain blocks behind the given position, keep blocks at or ahead.
    /// Returns the number of blocks drained.
    /// </summary>
    public int Invalidate(int pieceIndex, int begin)
    {
        int drained = 0;
        var kept = new ConcurrentQueue<SendBufferEntry>();

        while (_blocks.TryDequeue(out var entry))
        {
            bool isBehind = entry.PieceIndex < pieceIndex ||
                           (entry.PieceIndex == pieceIndex && entry.Begin < begin);
            if (isBehind)
            {
                Interlocked.Add(ref _bufferedBytes, -entry.Length);
                ArrayPool<byte>.Shared.Return(entry.Data);
                drained++;
            }
            else
            {
                kept.Enqueue(entry);
            }
        }

        // Re-enqueue kept blocks
        while (kept.TryDequeue(out var k))
            _blocks.Enqueue(k);

        return drained;
    }

    public void Dispose()
    {
        while (_blocks.TryDequeue(out var entry))
        {
            Interlocked.Add(ref _bufferedBytes, -entry.Length);
            ArrayPool<byte>.Shared.Return(entry.Data);
        }
        _drainSignal.Dispose();
    }
}
