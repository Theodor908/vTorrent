using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.DHT;
using Xunit;

namespace vTorrent.Core.Tests.DHT;

/// <summary>
/// Contract tests that verify any IDhtStorage implementation.
/// Currently tests DhtDefaultStorage.
/// </summary>
public class IDhtStorageContractTests
{
    private static readonly byte[] TestInfoHash = new byte[20];

    private static IOptionsMonitor<DhtSettings> Monitor(DhtSettings s)
    {
        var m = new SettingsMonitor<DhtSettings>();
        m.Update(s);
        return m;
    }

    private IDhtStorage CreateStorage()
    {
        var settings = new DhtSettings
        {
            MaxInfoHashes = 100,
            MaxTotalPeers = 10000,
            MaxPeersPerInfoHash = 500,
            MaxPeersReply = 50,
            MaxSampleCount = 20,
            SampleInfohashesIntervalSeconds = 600
        };
        return new DhtDefaultStorage(Monitor(settings));
    }

    [Fact]
    public void GetPeers_EmptyStorage_ReturnsEmptyList()
    {
        var storage = CreateStorage();
        storage.GetPeers(TestInfoHash).Should().BeEmpty();
    }

    [Fact]
    public void AnnouncePeer_ThenGetPeers_ReturnsPeer()
    {
        var storage = CreateStorage();
        var ep = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);
        storage.AnnouncePeer(TestInfoHash, ep).Should().BeTrue();
        storage.GetPeers(TestInfoHash).Should().ContainSingle()
            .Which.Should().Be(ep);
    }

    [Fact]
    public void HasPeers_AfterAnnounce_ReturnsTrue()
    {
        var storage = CreateStorage();
        storage.HasPeers(TestInfoHash).Should().BeFalse();
        storage.AnnouncePeer(TestInfoHash, new IPEndPoint(IPAddress.Loopback, 6881));
        storage.HasPeers(TestInfoHash).Should().BeTrue();
    }

    [Fact]
    public void InfoHashCount_TracksDistinctInfoHashes()
    {
        var storage = CreateStorage();
        storage.InfoHashCount.Should().Be(0);

        storage.AnnouncePeer(TestInfoHash, new IPEndPoint(IPAddress.Loopback, 6881));
        storage.InfoHashCount.Should().Be(1);

        var otherHash = new byte[20];
        otherHash[0] = 1;
        storage.AnnouncePeer(otherHash, new IPEndPoint(IPAddress.Loopback, 6882));
        storage.InfoHashCount.Should().Be(2);
    }

    [Fact]
    public void Clear_RemovesAllData()
    {
        var storage = CreateStorage();
        storage.AnnouncePeer(TestInfoHash, new IPEndPoint(IPAddress.Loopback, 6881));
        storage.Clear();
        storage.InfoHashCount.Should().Be(0);
        storage.TotalPeerCount.Should().Be(0);
    }

    [Fact]
    public void GetInfohashesSample_EmptyStorage_ReturnsEmptySamples()
    {
        var storage = CreateStorage();
        var result = storage.GetInfohashesSample();
        result.Samples.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.IntervalSeconds.Should().Be(600);
    }
}
