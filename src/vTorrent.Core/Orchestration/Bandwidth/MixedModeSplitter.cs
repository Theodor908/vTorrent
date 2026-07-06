using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Core.Orchestration.Bandwidth;

/// <summary>
/// Pure static utility for calculating TCP/uTP bandwidth split.
/// Returns (tcpLimit, utpLimit) where 0 means unlimited for that transport type.
/// </summary>
public static class MixedModeSplitter
{
    public static (long tcpLimit, long utpLimit) Calculate(
        MixedModeAlgorithm mode,
        long totalBandwidth,
        long tcpActualUsage,
        long utpActualUsage,
        int tcpPeerCount,
        int utpPeerCount)
    {
        int totalPeers = tcpPeerCount + utpPeerCount;
        if (totalPeers == 0) return (0, 0);

        return mode switch
        {
            MixedModeAlgorithm.PreferTcp => (
                0, // TCP unlimited
                Math.Max(0, totalBandwidth - tcpActualUsage)),
            MixedModeAlgorithm.PeerProportional => (
                totalPeers > 0 ? totalBandwidth * tcpPeerCount / totalPeers : totalBandwidth,
                totalPeers > 0 ? totalBandwidth * utpPeerCount / totalPeers : 0),
            MixedModeAlgorithm.PreferUtp => (
                Math.Max(0, totalBandwidth - utpActualUsage),
                0), // uTP unlimited
            _ => (0, 0)
        };
    }
}
