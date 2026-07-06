using System;
using System.Net;
using FluentAssertions;
using vTorrent.Core.DHT;
using Xunit;

namespace vTorrent.Core.Tests.DHT;

public class DhtMessageBep51Tests
{
    private static readonly NodeId TestNodeId = NodeId.GenerateRandom();
    private static readonly byte[] TxId = new byte[] { 0, 1 };

    [Fact]
    public void SampleInfohashesQuery_RoundTrips()
    {
        var target = NodeId.GenerateRandom();
        var msg = DhtMessage.CreateSampleInfohashesQuery(TxId, TestNodeId, target);
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));

        parsed.MessageType.Should().Be(DhtMessageType.Query);
        parsed.QueryType.Should().Be(DhtQueryType.SampleInfohashes);
        parsed.Target.Should().Be(target);
    }

    [Fact]
    public void SampleInfohashesResponse_RoundTrips()
    {
        var samples = new byte[40]; // 2 infohashes
        samples[0] = 0xAA;
        samples[20] = 0xBB;
        var nodes = new byte[26]; // 1 compact node

        var msg = DhtMessage.CreateSampleInfohashesResponse(
            TxId, TestNodeId, nodes, samples, num: 42, interval: 600);
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));

        parsed.MessageType.Should().Be(DhtMessageType.Response);
        parsed.Samples.Should().BeEquivalentTo(samples);
        parsed.SampleNum.Should().Be(42);
        parsed.SampleInterval.Should().Be(600);
        parsed.Nodes.Should().BeEquivalentTo(nodes);
    }

    [Fact]
    public void SampleInfohashesResponse_EmptySamples_StillIncluded()
    {
        var msg = DhtMessage.CreateSampleInfohashesResponse(
            TxId, TestNodeId, Array.Empty<byte>(), Array.Empty<byte>(), num: 0, interval: 600);
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));

        parsed.Samples.Should().NotBeNull();
        parsed.Samples.Should().BeEmpty();
    }

    [Fact]
    public void SampleInfohashesQuery_ReadOnly_RoundTrips()
    {
        var msg = DhtMessage.CreateSampleInfohashesQuery(TxId, TestNodeId, NodeId.GenerateRandom(), readOnly: true);
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));

        parsed.ReadOnly.Should().BeTrue();
        parsed.QueryType.Should().Be(DhtQueryType.SampleInfohashes);
    }

    [Fact]
    public void SampleInfohashesResponse_ZeroNum_StillEncoded()
    {
        // BEP 51: num=0 must be present in response, not omitted
        var msg = DhtMessage.CreateSampleInfohashesResponse(
            TxId, TestNodeId, Array.Empty<byte>(), Array.Empty<byte>(), num: 0, interval: 0);
        var encoded = msg.Encode();
        var parsed = DhtMessage.Parse(encoded, new IPEndPoint(IPAddress.Loopback, 1234));

        parsed.SampleNum.Should().Be(0);
        parsed.SampleInterval.Should().Be(0);
        parsed.Samples.Should().NotBeNull();
    }
}
