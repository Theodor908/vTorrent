using System;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.DHT;
using Xunit;

namespace vTorrent.Core.Tests.DHT;

public class DhtStorageSampleTests
{
    private static IOptionsMonitor<DhtSettings> Monitor(DhtSettings s)
    {
        var m = new SettingsMonitor<DhtSettings>();
        m.Update(s);
        return m;
    }

    private DhtDefaultStorage CreateStorage(int maxSamples = 20, int intervalSeconds = 600)
    {
        var settings = new DhtSettings
        {
            MaxInfoHashes = 1000,
            MaxTotalPeers = 10000,
            MaxPeersPerInfoHash = 500,
            MaxPeersReply = 50,
            MaxSampleCount = maxSamples,
            SampleInfohashesIntervalSeconds = intervalSeconds
        };
        return new DhtDefaultStorage(Monitor(settings));
    }

    private byte[] MakeInfoHash(byte value)
    {
        var hash = new byte[20];
        hash[0] = value;
        return hash;
    }

    [Fact]
    public void GetInfohashesSample_SingleEntry_ReturnsThatEntry()
    {
        var storage = CreateStorage();
        var hash = MakeInfoHash(0x42);
        storage.AnnouncePeer(hash, new IPEndPoint(IPAddress.Loopback, 6881));

        var result = storage.GetInfohashesSample();
        result.Samples.Length.Should().Be(20);
        result.TotalCount.Should().Be(1);
        result.Samples.AsSpan(0, 20).ToArray().Should().BeEquivalentTo(hash);
    }

    [Fact]
    public void GetInfohashesSample_MultipleEntries_SamplesMultipleOf20()
    {
        var storage = CreateStorage();
        for (byte i = 0; i < 10; i++)
            storage.AnnouncePeer(MakeInfoHash(i), new IPEndPoint(IPAddress.Loopback, 6881 + i));

        var result = storage.GetInfohashesSample();
        (result.Samples.Length % 20).Should().Be(0);
        result.TotalCount.Should().Be(10);
    }

    [Fact]
    public void GetInfohashesSample_MoreThanMaxSamples_CapsAtMax()
    {
        var storage = CreateStorage(maxSamples: 5);
        for (byte i = 0; i < 30; i++)
            storage.AnnouncePeer(MakeInfoHash(i), new IPEndPoint(IPAddress.Loopback, 6881 + i));

        var result = storage.GetInfohashesSample();
        result.Samples.Length.Should().Be(5 * 20);
        result.TotalCount.Should().Be(30);
    }

    [Fact]
    public void GetInfohashesSample_IntervalReturned()
    {
        var storage = CreateStorage(intervalSeconds: 300);
        var result = storage.GetInfohashesSample();
        result.IntervalSeconds.Should().Be(300);
    }

    [Fact]
    public void GetInfohashesSample_SamplesAreCached()
    {
        var storage = CreateStorage();
        storage.AnnouncePeer(MakeInfoHash(1), new IPEndPoint(IPAddress.Loopback, 6881));

        var result1 = storage.GetInfohashesSample();
        var result2 = storage.GetInfohashesSample();

        // Same cached samples (same reference)
        result1.Samples.Should().BeSameAs(result2.Samples);
    }
}
