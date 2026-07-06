using System;

namespace vTorrent.Core.PeerCommunication.Transport.I2P;

/// <summary>
/// Per-peer adaptive timeout calculator for I2P connections.
/// Uses TCP-style SRTT with a minimum floor of 4x clearnet defaults.
/// </summary>
public sealed class I2pTimeoutPolicy
{
    private const double Alpha = 0.125; // SRTT smoothing factor
    private const double SrttWeight = 1.0 - Alpha; // 0.875

    private double _srtt; // Smoothed RTT in milliseconds
    private bool _initialized;

    // Clearnet defaults
    private const int ClearnetHandshakeTimeoutMs = 10_000;
    private const int ClearnetRequestTimeoutMs = 30_000;
    private const int ClearnetKeepAliveMs = 120_000;
    private const int FloorMultiplier = 4;

    public double SmoothedRttMs => _srtt;

    public void RecordRttSample(double rttMs)
    {
        if (rttMs <= 0) return;

        if (!_initialized)
        {
            _srtt = rttMs;
            _initialized = true;
        }
        else
        {
            _srtt = SrttWeight * _srtt + Alpha * rttMs;
        }
    }

    public int HandshakeTimeoutMs => ComputeTimeout(ClearnetHandshakeTimeoutMs);
    public int RequestTimeoutMs => ComputeTimeout(ClearnetRequestTimeoutMs);
    public int KeepAliveIntervalMs => Math.Max(ClearnetKeepAliveMs * FloorMultiplier, (int)(_srtt * 4));

    private int ComputeTimeout(int clearnetDefault)
    {
        int floor = clearnetDefault * FloorMultiplier;
        if (!_initialized) return floor;
        int adaptive = (int)(_srtt * 2);
        return Math.Max(adaptive, floor);
    }
}
