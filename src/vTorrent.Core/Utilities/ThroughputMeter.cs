using System.Threading;

namespace vTorrent.Core.Utilities;

/// <summary>
/// Measures throughput using a 10-bucket sliding window (one bucket per second)
/// smoothed with an EMA (alpha = 0.3). Extracted from DiskWriteThrottler for reuse
/// by send buffer flow control.
/// </summary>
internal sealed class ThroughputMeter
{
    private readonly long[] _samples = new long[10]; // 10 one-second buckets
    private int _currentIndex;
    private long _currentBucketBytes;
    private long _lastBucketTick;

    /// <summary>Exponentially smoothed bytes-per-second estimate.</summary>
    public long BytesPerSecond { get; private set; }

    public ThroughputMeter()
    {
        _lastBucketTick = Environment.TickCount64;
    }

    /// <summary>
    /// Records bytes and advances the bucket if one or more seconds have elapsed.
    /// </summary>
    public void Record(int bytes)
    {
        var now = Environment.TickCount64;
        var elapsed = now - Volatile.Read(ref _lastBucketTick);

        if (elapsed >= 1000)
        {
            var finishedBytes = Interlocked.Exchange(ref _currentBucketBytes, 0);
            Volatile.Write(ref _lastBucketTick, now);

            _currentIndex = (_currentIndex + 1) % _samples.Length;
            _samples[_currentIndex] = finishedBytes;

            BytesPerSecond = (long)(0.3 * finishedBytes + 0.7 * BytesPerSecond);
        }

        Interlocked.Add(ref _currentBucketBytes, bytes);
    }

    /// <summary>Test helper: forces a bucket roll without waiting 1 second.</summary>
    internal void ForceRollForTesting()
    {
        var finishedBytes = Interlocked.Exchange(ref _currentBucketBytes, 0);
        Volatile.Write(ref _lastBucketTick, Environment.TickCount64);
        _currentIndex = (_currentIndex + 1) % _samples.Length;
        _samples[_currentIndex] = finishedBytes;
        BytesPerSecond = (long)(0.3 * finishedBytes + 0.7 * BytesPerSecond);
    }
}
