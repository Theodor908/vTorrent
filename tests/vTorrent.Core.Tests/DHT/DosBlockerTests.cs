using System.Net;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.DHT;
using Xunit;

namespace vTorrent.Core.Tests.DHT;

public class DosBlockerTests
{
    private static DhtSettings DefaultSettings() => new()
    {
        EnableDosBlocker = true,
        BlockRateLimitPacketsPerSec = 5,
        BlockTimeoutSeconds = 300,
        MaxBlockedIps = 20
    };

    private static IOptionsMonitor<DhtSettings> Monitor(DhtSettings s)
    {
        var m = new SettingsMonitor<DhtSettings>();
        m.Update(s);
        return m;
    }

    private static readonly IPAddress TestIp = IPAddress.Parse("1.2.3.4");

    [Fact]
    public void UnderThreshold_AllAllowed()
    {
        var blocker = new DosBlocker(Monitor(DefaultSettings()));
        for (int i = 0; i < 49; i++)
            Assert.True(blocker.RecordPacket(TestIp));
    }

    [Fact]
    public void AtThreshold_BansOnFiftiethPacket()
    {
        var blocker = new DosBlocker(Monitor(DefaultSettings()));
        for (int i = 0; i < 49; i++)
            Assert.True(blocker.RecordPacket(TestIp));
        Assert.False(blocker.RecordPacket(TestIp));
    }

    [Fact]
    public void BurstTolerance_SixPacketsInQuickSuccession_Allowed()
    {
        var blocker = new DosBlocker(Monitor(DefaultSettings()));
        for (int i = 0; i < 6; i++)
            Assert.True(blocker.RecordPacket(TestIp));
    }

    [Fact]
    public void BlockedIp_DropsPackets()
    {
        var blocker = new DosBlocker(Monitor(DefaultSettings()));
        for (int i = 0; i < 50; i++)
            blocker.RecordPacket(TestIp);
        Assert.False(blocker.RecordPacket(TestIp));
        Assert.True(blocker.IsBlocked(TestIp));
    }

    [Fact]
    public void DifferentIps_IndependentTracking()
    {
        var blocker = new DosBlocker(Monitor(DefaultSettings()));
        var ip2 = IPAddress.Parse("5.6.7.8");
        for (int i = 0; i < 50; i++)
            blocker.RecordPacket(TestIp);
        Assert.True(blocker.RecordPacket(ip2));
    }

    [Fact]
    public void Disabled_AllowsEverything()
    {
        var settings = DefaultSettings();
        settings.EnableDosBlocker = false;
        var blocker = new DosBlocker(Monitor(settings));
        for (int i = 0; i < 100; i++)
            Assert.True(blocker.RecordPacket(TestIp));
    }

    [Fact]
    public void ZeroLimit_AllowsEverything()
    {
        var settings = DefaultSettings();
        settings.BlockRateLimitPacketsPerSec = 0;
        var blocker = new DosBlocker(Monitor(settings));
        for (int i = 0; i < 100; i++)
            Assert.True(blocker.RecordPacket(TestIp));
    }

    [Fact]
    public void TrackedIpCount_Correct()
    {
        var blocker = new DosBlocker(Monitor(DefaultSettings()));
        blocker.RecordPacket(IPAddress.Parse("1.1.1.1"));
        blocker.RecordPacket(IPAddress.Parse("2.2.2.2"));
        blocker.RecordPacket(IPAddress.Parse("3.3.3.3"));
        Assert.Equal(3, blocker.TrackedIpCount);
    }
}
