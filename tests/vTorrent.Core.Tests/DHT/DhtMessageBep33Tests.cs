using System.Net;
using FluentAssertions;
using vTorrent.Core.DHT;
using Xunit;

namespace vTorrent.Core.Tests.DHT;

public class DhtMessageBep33Tests
{
    private static readonly NodeId TestNodeId = NodeId.GenerateRandom();
    private static readonly byte[] TestInfoHash = new byte[20];

    [Fact]
    public void ReadOnly_EncodeQuery_AddsRoToTopLevel()
    {
        var msg = DhtMessage.CreatePingQuery(new byte[] { 0, 1 }, TestNodeId, readOnly: true);
        msg.ReadOnly = true;
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));
        parsed.ReadOnly.Should().BeTrue();
    }

    [Fact]
    public void ReadOnly_NormalQuery_ReadOnlyIsFalse()
    {
        var msg = DhtMessage.CreatePingQuery(new byte[] { 0, 1 }, TestNodeId);
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));
        parsed.ReadOnly.Should().BeFalse();
    }

    [Fact]
    public void GetPeersQuery_WithScrape_RoundTrips()
    {
        var msg = DhtMessage.CreateGetPeersQuery(new byte[] { 0, 1 }, TestNodeId, TestInfoHash, scrape: true, noSeed: true);
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));
        parsed.Scrape.Should().BeTrue();
        parsed.NoSeed.Should().BeTrue();
    }

    [Fact]
    public void AnnouncePeer_WithSeed_RoundTrips()
    {
        var msg = DhtMessage.CreateAnnouncePeerQuery(
            new byte[] { 0, 1 }, TestNodeId, TestInfoHash, 6881, new byte[] { 1, 2, 3, 4 },
            isSeed: true);
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));
        parsed.IsSeed.Should().BeTrue();
    }

    [Fact]
    public void GetPeersResponse_WithBloomFilters_RoundTrips()
    {
        var bfsd = new byte[256]; bfsd[0] = 0xFF;
        var bfpe = new byte[256]; bfpe[255] = 0xAA;
        var msg = DhtMessage.CreateGetPeersResponseWithPeers(
            new byte[] { 0, 1 }, TestNodeId, new byte[] { 1, 2, 3, 4 },
            new System.Collections.Generic.List<byte[]>(), bfsd: bfsd, bfpe: bfpe);
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));
        parsed.BFsd.Should().BeEquivalentTo(bfsd);
        parsed.BFpe.Should().BeEquivalentTo(bfpe);
    }

    [Fact]
    public void GetPeersResponse_NoBloomFilters_NullByDefault()
    {
        var msg = DhtMessage.CreateGetPeersResponseWithPeers(
            new byte[] { 0, 1 }, TestNodeId, new byte[] { 1, 2, 3, 4 },
            new System.Collections.Generic.List<byte[]>());
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));
        parsed.BFsd.Should().BeNull();
        parsed.BFpe.Should().BeNull();
    }
}
