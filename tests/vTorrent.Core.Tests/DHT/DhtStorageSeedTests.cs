using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.DHT;
using Xunit;

namespace vTorrent.Core.Tests.DHT;

public class DhtStorageSeedTests
{
    private static readonly byte[] TestInfoHash = new byte[20];

    private static IOptionsMonitor<DhtSettings> Monitor(DhtSettings s)
    {
        var m = new SettingsMonitor<DhtSettings>();
        m.Update(s);
        return m;
    }

    private DhtDefaultStorage CreateStorage()
    {
        var settings = new DhtSettings
        {
            MaxInfoHashes = 100,
            MaxTotalPeers = 10000,
            MaxPeersPerInfoHash = 500,
            MaxPeersReply = 50
        };
        return new DhtDefaultStorage(Monitor(settings));
    }

    [Fact]
    public void AnnouncePeer_WithSeedFlag_TracksSeedStatus()
    {
        var storage = CreateStorage();
        storage.AnnouncePeer(TestInfoHash, new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881), isSeed: true);
        storage.GetSeedBloomFilter(TestInfoHash).EstimateCount().Should().BeGreaterThan(0);
    }

    [Fact]
    public void AnnouncePeer_WithoutSeedFlag_NotInSeedFilter()
    {
        var storage = CreateStorage();
        storage.AnnouncePeer(TestInfoHash, new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881), isSeed: false);
        storage.GetSeedBloomFilter(TestInfoHash).EstimateCount().Should().Be(0);
        storage.GetPeerBloomFilter(TestInfoHash).EstimateCount().Should().BeGreaterThan(0);
    }

    [Fact]
    public void AnnouncePeer_ReAnnounce_UpdatesSeedStatus()
    {
        var storage = CreateStorage();
        var ep = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);
        storage.AnnouncePeer(TestInfoHash, ep, isSeed: false);
        storage.GetSeedBloomFilter(TestInfoHash).EstimateCount().Should().Be(0);
        storage.AnnouncePeer(TestInfoHash, ep, isSeed: true);
        storage.GetSeedBloomFilter(TestInfoHash).EstimateCount().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetSeedBloomFilter_UnknownInfoHash_ReturnsEmptyFilter()
    {
        var storage = CreateStorage();
        var filter = storage.GetSeedBloomFilter(new byte[20]);
        filter.EstimateCount().Should().Be(0);
        filter.Data.Length.Should().Be(256);
    }

    [Fact]
    public void GetPeerBloomFilter_IncludesAllPeers()
    {
        var storage = CreateStorage();
        for (int i = 1; i <= 10; i++)
            storage.AnnouncePeer(TestInfoHash, new IPEndPoint(IPAddress.Parse($"10.0.0.{i}"), 6881), isSeed: i <= 3);
        var peerFilter = storage.GetPeerBloomFilter(TestInfoHash);
        peerFilter.EstimateCount().Should().BeInRange(5, 15);
        var seedFilter = storage.GetSeedBloomFilter(TestInfoHash);
        seedFilter.EstimateCount().Should().BeLessThan(peerFilter.EstimateCount());
    }
}
