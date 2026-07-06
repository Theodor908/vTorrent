using System.Threading;
using vTorrent.Core.Utilities;

namespace vTorrent.Core.Upload;

/// <summary>
/// Per-torrent memory ceiling for send buffers with 3-tier watermark backpressure.
/// Mirrors DiskWriteThrottler's watermark pattern but for the upload read-ahead path.
/// </summary>
internal sealed class TorrentSendBufferAccounting
{
    private long _totalBufferedBytes;
    private long _effectiveCeiling;
    private readonly bool _autoTune;
    private readonly ThroughputMeter _aggregateUploadMeter = new();
    private volatile PressureState _state = PressureState.Normal;

    // Async signaling for HardPause recovery
    private readonly SemaphoreSlim _recoverySignal = new(0, 1);

    public long TotalBufferedBytes => Interlocked.Read(ref _totalBufferedBytes);
    public long EffectiveCeiling => Interlocked.Read(ref _effectiveCeiling);
    public PressureState State => _state;

    public TorrentSendBufferAccounting(int manualCeiling)
    {
        _autoTune = manualCeiling == 0;
        _effectiveCeiling = _autoTune ? 4L * 1024 * 1024 : manualCeiling; // default floor 4 MiB
    }

    /// <summary>
    /// Try to reserve bytes for a block read-ahead. Returns false if ceiling would be exceeded.
    /// Uses CompareExchange loop to prevent TOCTOU race under concurrency.
    /// </summary>
    public bool TryReserve(int bytes)
    {
        var ceiling = Interlocked.Read(ref _effectiveCeiling);
        while (true)
        {
            var current = Interlocked.Read(ref _totalBufferedBytes);
            if (current + bytes > ceiling)
                return false;
            if (Interlocked.CompareExchange(ref _totalBufferedBytes, current + bytes, current) == current)
            {
                UpdatePressureState(current + bytes, ceiling);
                return true;
            }
            // Another thread changed the counter — retry
        }
    }

    /// <summary>
    /// Release bytes when a block is consumed or discarded. Checks for low-watermark recovery.
    /// </summary>
    public void Release(int bytes)
    {
        var previousState = _state;
        var current = Interlocked.Add(ref _totalBufferedBytes, -bytes);
        var ceiling = Interlocked.Read(ref _effectiveCeiling);
        UpdatePressureState(current, ceiling);

        // Low watermark recovery: resume at 50% — check previous state to avoid missed signal
        if (previousState == PressureState.HardPause && _state == PressureState.Normal)
        {
            try { _recoverySignal.Release(); }
            catch (SemaphoreFullException) { /* already signaled */ }
        }
    }

    /// <summary>
    /// Record uploaded bytes for auto-tune throughput calculation.
    /// </summary>
    public void RecordUpload(int bytes)
    {
        _aggregateUploadMeter.Record(bytes);
        if (_autoTune)
            AutoTune();
    }

    /// <summary>
    /// Wait for recovery from HardPause state.
    /// </summary>
    public Task WaitForRecoveryAsync(CancellationToken ct)
        => _recoverySignal.WaitAsync(ct);

    private void UpdatePressureState(long current, long ceiling)
    {
        if (current > ceiling * 3 / 4)
            _state = PressureState.HardPause;
        else if (current > ceiling / 2)
            _state = PressureState.SoftPressure;
        else
            _state = PressureState.Normal;
    }

    private void AutoTune()
    {
        var bytesPerSecond = _aggregateUploadMeter.BytesPerSecond;
        if (bytesPerSecond <= 0) return;

        var target = (long)(bytesPerSecond * 2.5);
        target = Math.Clamp(target, 4L * 1024 * 1024, 64L * 1024 * 1024);
        Interlocked.Exchange(ref _effectiveCeiling, target);
    }

    /// <summary>Test helper: forces auto-tune calculation.</summary>
    internal void ForceAutoTuneForTesting()
    {
        _aggregateUploadMeter.ForceRollForTesting();
        AutoTune();
    }
}
