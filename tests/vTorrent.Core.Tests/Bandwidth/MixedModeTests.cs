using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.Orchestration.Bandwidth;
using Xunit;

namespace vTorrent.Core.Tests.Bandwidth;

public class MixedModeTests
{
    [Fact]
    public void PreferTcp_UtpGetsLeftovers()
    {
        var (tcpLimit, utpLimit) = MixedModeSplitter.Calculate(
            mode: MixedModeAlgorithm.PreferTcp,
            totalBandwidth: 100_000, tcpActualUsage: 60_000, utpActualUsage: 20_000,
            tcpPeerCount: 3, utpPeerCount: 1);
        Assert.Equal(0, tcpLimit);       // unlimited (0 = no cap)
        Assert.Equal(40_000, utpLimit);  // 100K - 60K TCP usage
    }

    [Fact]
    public void PeerProportional_SplitsByPeerCount()
    {
        var (tcpLimit, utpLimit) = MixedModeSplitter.Calculate(
            mode: MixedModeAlgorithm.PeerProportional,
            totalBandwidth: 100_000, tcpActualUsage: 0, utpActualUsage: 0,
            tcpPeerCount: 30, utpPeerCount: 10);
        Assert.Equal(75_000, tcpLimit);  // 30/40 * 100K
        Assert.Equal(25_000, utpLimit);  // 10/40 * 100K
    }

    [Fact]
    public void PreferUtp_TcpGetsLeftovers()
    {
        var (tcpLimit, utpLimit) = MixedModeSplitter.Calculate(
            mode: MixedModeAlgorithm.PreferUtp,
            totalBandwidth: 100_000, tcpActualUsage: 30_000, utpActualUsage: 50_000,
            tcpPeerCount: 2, utpPeerCount: 3);
        Assert.Equal(50_000, tcpLimit);  // 100K - 50K uTP usage
        Assert.Equal(0, utpLimit);       // unlimited
    }

    [Fact]
    public void ZeroPeersOfOneType_FullBandwidthToOther()
    {
        var (tcpLimit, utpLimit) = MixedModeSplitter.Calculate(
            mode: MixedModeAlgorithm.PeerProportional,
            totalBandwidth: 100_000, tcpActualUsage: 0, utpActualUsage: 0,
            tcpPeerCount: 5, utpPeerCount: 0);
        Assert.Equal(100_000, tcpLimit);
        Assert.Equal(0, utpLimit);
    }

    [Fact]
    public void NegativeQuota_FlooredToZero()
    {
        var (tcpLimit, utpLimit) = MixedModeSplitter.Calculate(
            mode: MixedModeAlgorithm.PreferTcp,
            totalBandwidth: 50_000, tcpActualUsage: 80_000, utpActualUsage: 10_000,
            tcpPeerCount: 5, utpPeerCount: 2);
        Assert.Equal(0, tcpLimit);       // unlimited
        Assert.Equal(0, utpLimit);       // max(0, 50K - 80K) = 0
    }

    [Fact]
    public void ZeroPeersTotal_ReturnsZero()
    {
        var (tcpLimit, utpLimit) = MixedModeSplitter.Calculate(
            mode: MixedModeAlgorithm.PeerProportional,
            totalBandwidth: 100_000, tcpActualUsage: 0, utpActualUsage: 0,
            tcpPeerCount: 0, utpPeerCount: 0);
        Assert.Equal(0, tcpLimit);
        Assert.Equal(0, utpLimit);
    }
}
