using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.PeerCommunication.Transport.Utp;

/// <summary>
/// Snapshot of uTP congestion control parameters.
/// Created per-connection from current PeerSettings.
/// Existing connections keep creation-time values.
/// </summary>
public readonly struct UtpTuning
{
    public int TargetDelayUs { get; init; }
    public int GainFactor { get; init; }
    public int MinTimeoutMs { get; init; }
    public int SynResends { get; init; }
    public int FinResends { get; init; }
    public int NumResends { get; init; }
    public double LossFactor { get; init; }
    public int CwndReduceTimerMs { get; init; }
    public int ConnectTimeoutMs { get; init; }

    /// <summary>
    /// Create from PeerSettings with unit conversions.
    /// </summary>
    public static UtpTuning FromPeerSettings(PeerSettings ps) => new()
    {
        TargetDelayUs = ps.UtpTargetDelay * 1000,       // ms → µs
        GainFactor = ps.UtpGainFactor,
        MinTimeoutMs = ps.UtpMinTimeout,
        SynResends = ps.UtpSynResends,
        FinResends = ps.UtpFinResends,
        NumResends = ps.UtpNumResends,
        LossFactor = ps.UtpLossMultiplier / 100.0,      // % → fraction
        CwndReduceTimerMs = ps.UtpCwndReduceTimer,
        ConnectTimeoutMs = ps.UtpConnectTimeoutMs,       // existing property
    };
}
