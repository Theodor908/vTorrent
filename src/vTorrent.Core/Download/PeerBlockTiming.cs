using System;

namespace vTorrent.Core.Download;

/// <summary>
/// Tracks per-peer block delivery times using EWMA for adaptive timeout calculation.
/// Based on libtorrent's approach: timeout = mean + 4 * deviation, clamped to [2s, 60s].
/// Similar to TCP's RTO calculation (RFC 6298).
/// </summary>
internal class PeerBlockTiming
{
    private double _avgBlockTime = 15.0;       // Default 15s
    private double _blockTimeDeviation = 5.0;  // Default 5s
    private readonly object _lock = new();

    public void RecordBlockDelivery(TimeSpan elapsed)
    {
        double sample = elapsed.TotalSeconds;
        lock (_lock)
        {
            // EWMA with α=0.125 (same as TCP RTT smoothing)
            _blockTimeDeviation = 0.875 * _blockTimeDeviation + 0.125 * Math.Abs(sample - _avgBlockTime);
            _avgBlockTime = 0.875 * _avgBlockTime + 0.125 * sample;
        }
    }

    public TimeSpan GetAdaptiveTimeout(bool endgameMode)
    {
        double timeout;
        lock (_lock)
        {
            timeout = _avgBlockTime + 4 * _blockTimeDeviation;
        }

        // In endgame mode, use half the adaptive timeout (more aggressive)
        if (endgameMode)
            timeout *= 0.5;

        return TimeSpan.FromSeconds(Math.Clamp(timeout, 2.0, 60.0));
    }
}
