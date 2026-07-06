using System;
using System.Collections.Generic;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.PeerCommunication.Transport.Utp;

/// <summary>
/// LEDBAT (Low Extra Delay Background Transport) congestion control per BEP 29.
/// Targets 100ms of additional queuing delay, yielding bandwidth to TCP flows.
/// </summary>
public sealed class LedbatCongestionControl
{
    public readonly int TargetDelayUs;
    public readonly double LossFactor;
    public const int MinPacketSize = 150;  // keep as const - structural minimum
    // Initial congestion window (~6 uTP MTUs). uTP/LEDBAT starts in slow start and grows via
    // OnAck, but the initial window must exceed a single packet so a healthy connection can
    // pipeline instead of stop-and-waiting from the first byte (libtorrent floors cwnd at
    // 1 MTU; TCP RFC 6928 uses IW=10*MSS — this is a conservative middle ground).
    public const int InitialWindowBytes = 8192;
    private const int BaseDelayWindowSeconds = 120;  // keep as const - algorithm constant
    public readonly int MinTimeoutMs;
    private const int InitialTimeoutMs = 1000;  // keep as const
    public readonly int MaxCwndIncreasePerRtt;

    public int CongestionWindow { get; private set; }

    private long _rttUs;
    private long _rttVarUs;
    private bool _hasRttSample;
    private int _timeoutMs = InitialTimeoutMs;

    private readonly SortedList<long, long> _baseDelayHistory = new();
    private long _baseDelayUs = long.MaxValue;

    public LedbatCongestionControl(UtpTuning tuning)
    {
        TargetDelayUs = tuning.TargetDelayUs;
        LossFactor = tuning.LossFactor;
        MinTimeoutMs = tuning.MinTimeoutMs;
        MaxCwndIncreasePerRtt = tuning.GainFactor;
        CongestionWindow = InitialWindowBytes;
        _timeoutMs = InitialTimeoutMs;
    }

    public LedbatCongestionControl() : this(UtpTuning.FromPeerSettings(new PeerSettings())) { }

    public void UpdateBaseDelay(long delayUs)
    {
        long now = Environment.TickCount64;

        while (_baseDelayHistory.Count > 0 &&
               now - _baseDelayHistory.Keys[0] > BaseDelayWindowSeconds * 1000)
        {
            _baseDelayHistory.RemoveAt(0);
        }

        _baseDelayHistory[now] = delayUs;

        _baseDelayUs = long.MaxValue;
        foreach (var entry in _baseDelayHistory.Values)
        {
            if (entry < _baseDelayUs)
                _baseDelayUs = entry;
        }
    }

    public void OnAck(int ackedBytes, int inFlightBytes, long delayUs)
    {
        if (inFlightBytes <= 0 || ackedBytes <= 0) return;

        long ourDelay = delayUs - _baseDelayUs;
        if (ourDelay < 0) ourDelay = 0;

        double delayFactor = (double)(TargetDelayUs - ourDelay) / TargetDelayUs;
        double windowFactor = (double)ackedBytes / inFlightBytes;
        int scaledGain = (int)(MaxCwndIncreasePerRtt * delayFactor * windowFactor);

        CongestionWindow = Math.Max(CongestionWindow + scaledGain, MinPacketSize);
    }

    public void OnPacketLoss()
    {
        CongestionWindow = Math.Max((int)(CongestionWindow * LossFactor), MinPacketSize);
    }

    public void OnTimeout()
    {
        CongestionWindow = MinPacketSize;
        _timeoutMs = Math.Min(_timeoutMs * 2, 60_000);
    }

    public void UpdateRtt(long sampleUs)
    {
        if (!_hasRttSample)
        {
            _rttUs = sampleUs;
            _rttVarUs = sampleUs / 2;
            _hasRttSample = true;
        }
        else
        {
            long delta = Math.Abs(sampleUs - _rttUs);
            _rttVarUs += (delta - _rttVarUs) / 4;
            _rttUs += (sampleUs - _rttUs) / 8;
        }

        _timeoutMs = Math.Max((int)((_rttUs + _rttVarUs * 4) / 1000), MinTimeoutMs);
    }

    public int GetTimeoutMs() => _timeoutMs;

    public bool CanSend(int curWindowBytes, int packetSize, uint peerWindowSize)
    {
        int effectiveWindow = (int)Math.Min(CongestionWindow, peerWindowSize);
        return curWindowBytes + packetSize <= effectiveWindow;
    }
}
