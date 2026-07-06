using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Core.Tests.Utp;

public class UtpTuningTests
{
    [Fact]
    public void FromPeerSettings_ConvertsTargetDelayMsToUs()
    {
        var ps = new PeerSettings { UtpTargetDelay = 100 };
        var tuning = UtpTuning.FromPeerSettings(ps);
        Assert.Equal(100_000, tuning.TargetDelayUs); // 100ms * 1000
    }

    [Fact]
    public void FromPeerSettings_ConvertsLossMultiplierPercentToFraction()
    {
        var ps = new PeerSettings { UtpLossMultiplier = 50 };
        var tuning = UtpTuning.FromPeerSettings(ps);
        Assert.Equal(0.5, tuning.LossFactor); // 50% / 100
    }

    [Fact]
    public void FromPeerSettings_PassthroughParamsUnchanged()
    {
        var ps = new PeerSettings
        {
            UtpGainFactor = 3000, UtpMinTimeout = 500,
            UtpSynResends = 2, UtpFinResends = 2, UtpNumResends = 3,
            UtpCwndReduceTimer = 100, UtpConnectTimeoutMs = 5000
        };
        var tuning = UtpTuning.FromPeerSettings(ps);
        Assert.Equal(3000, tuning.GainFactor);
        Assert.Equal(500, tuning.MinTimeoutMs);
        Assert.Equal(2, tuning.SynResends);
        Assert.Equal(2, tuning.FinResends);
        Assert.Equal(3, tuning.NumResends);
        Assert.Equal(100, tuning.CwndReduceTimerMs);
        Assert.Equal(5000, tuning.ConnectTimeoutMs);
    }

    [Fact]
    public void DefaultPeerSettings_MatchesOriginalConstants()
    {
        // Verify defaults match the original hardcoded values
        var tuning = UtpTuning.FromPeerSettings(new PeerSettings());
        Assert.Equal(100_000, tuning.TargetDelayUs);  // was const 100_000
        Assert.Equal(0.5, tuning.LossFactor);          // was const 0.5
        Assert.Equal(500, tuning.MinTimeoutMs);         // was const 500
        Assert.Equal(3000, tuning.GainFactor);          // was const 3000 (MaxCwndIncreasePerRtt)
    }
}
