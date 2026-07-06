using System;
using System.Net;
using FluentAssertions;
using vTorrent.Abstractions.Models;
using vTorrent.Core.DHT;
using vTorrent.Core.DHT.I2P;
using vTorrent.Core.Network.I2P;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Network.I2P;

public class I2pDhtTransportTests
{
    [Fact]
    public void CompactNodeInfoSize_Is54()
    {
        var transport = CreateTransport();
        transport.CompactNodeInfoSize.Should().Be(54);
    }

    [Fact]
    public void EncodeDecodeCompactNodeInfo_RoundTrips()
    {
        var transport = CreateTransport();

        var nodeIdBytes = new byte[20];
        Random.Shared.NextBytes(nodeIdBytes);
        var destHash = new byte[32];
        Random.Shared.NextBytes(destHash);
        ushort port = 6881;

        var dest = I2pDestination.FromHash(destHash);
        var endpoint = new I2pEndPoint(dest);
        var nodeId = new NodeId(nodeIdBytes);
        var entry = new NodeEntry(nodeId, endpoint, port);

        var encoded = transport.EncodeCompactNodeInfo(entry);
        encoded.Length.Should().Be(54);

        var (decodedId, decodedEp, decodedPort) = transport.DecodeCompactNodeInfo(encoded, 0);
        decodedId.Should().BeEquivalentTo(nodeIdBytes);
        decodedPort.Should().Be(port);
        decodedEp.Should().BeOfType<I2pEndPoint>();

        var i2pEp = (I2pEndPoint)decodedEp;
        i2pEp.Destination.Hash.ToArray().Should().BeEquivalentTo(destHash);
    }

    [Fact]
    public void NodeId_IsGenerated()
    {
        var transport = CreateTransport();
        transport.NodeId.Should().NotBeNull();
        transport.NodeId.Length.Should().Be(20);
    }

    private I2pDhtTransport CreateTransport()
    {
        var destHash = new byte[32];
        Random.Shared.NextBytes(destHash);
        var dest = I2pDestination.FromHash(destHash);
        return new I2pDhtTransport(null!, dest, 6881, null);
    }
}
