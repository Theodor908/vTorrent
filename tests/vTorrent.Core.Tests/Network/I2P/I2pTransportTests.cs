using FluentAssertions;
using Moq;
using System.Net;
using Xunit;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Core.PeerCommunication.Transport.I2P;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pTimeoutPolicyTests
{
    [Fact]
    public void NewPolicy_ReturnsFloorTimeouts()
    {
        var policy = new I2pTimeoutPolicy();
        policy.HandshakeTimeoutMs.Should().Be(40_000); // 10s * 4
        policy.RequestTimeoutMs.Should().Be(120_000);  // 30s * 4
    }

    [Fact]
    public void RecordRtt_UpdatesSrtt()
    {
        var policy = new I2pTimeoutPolicy();
        policy.RecordRttSample(500);
        policy.SmoothedRttMs.Should().Be(500);
    }

    [Fact]
    public void RecordMultipleRtt_SmoothsValues()
    {
        var policy = new I2pTimeoutPolicy();
        policy.RecordRttSample(1000);
        policy.RecordRttSample(500);
        // SRTT = 0.875 * 1000 + 0.125 * 500 = 937.5
        policy.SmoothedRttMs.Should().BeApproximately(937.5, 0.1);
    }

    [Fact]
    public void AdaptiveTimeout_StaysAboveFloor()
    {
        var policy = new I2pTimeoutPolicy();
        policy.RecordRttSample(100); // Very fast I2P connection
        // Adaptive = 100 * 2 = 200ms, but floor = 40000ms
        policy.HandshakeTimeoutMs.Should().Be(40_000);
    }

    [Fact]
    public void AdaptiveTimeout_ExceedsFloor_WhenRttHigh()
    {
        var policy = new I2pTimeoutPolicy();
        policy.RecordRttSample(30_000); // Very slow connection
        // Adaptive = 30000 * 2 = 60000ms > floor of 40000ms
        policy.HandshakeTimeoutMs.Should().Be(60_000);
    }
}

public class CompositeTransportConnectorTests
{
    [Fact]
    public async Task ConnectAsync_I2pEndPoint_RoutesToI2pConnector()
    {
        var clearnet = new Mock<ITransportConnector>();
        var i2p = new Mock<ITransportConnector>();
        var mockStream = new Mock<ITransportStream>();

        var hash = new byte[32];
        var dest = I2pDestination.FromHash(hash);
        var ep = new I2pEndPoint(dest);

        i2p.Setup(c => c.ConnectAsync(ep, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockStream.Object);

        var composite = new CompositeTransportConnector(clearnet.Object, i2p.Object);
        var result = await composite.ConnectAsync(ep);

        result.Should().Be(mockStream.Object);
        i2p.Verify(c => c.ConnectAsync(ep, It.IsAny<CancellationToken>()), Times.Once);
        clearnet.Verify(c => c.ConnectAsync(It.IsAny<EndPoint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConnectAsync_IPEndPoint_RoutesToClearnetConnector()
    {
        var clearnet = new Mock<ITransportConnector>();
        var i2p = new Mock<ITransportConnector>();
        var mockStream = new Mock<ITransportStream>();

        var ep = new IPEndPoint(IPAddress.Loopback, 6881);

        clearnet.Setup(c => c.ConnectAsync(ep, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockStream.Object);

        var composite = new CompositeTransportConnector(clearnet.Object, i2p.Object);
        var result = await composite.ConnectAsync(ep);

        result.Should().Be(mockStream.Object);
        clearnet.Verify(c => c.ConnectAsync(ep, It.IsAny<CancellationToken>()), Times.Once);
        i2p.Verify(c => c.ConnectAsync(It.IsAny<EndPoint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConnectAsync_UnknownEndPointType_Throws()
    {
        var clearnet = new Mock<ITransportConnector>();
        var i2p = new Mock<ITransportConnector>();

        var composite = new CompositeTransportConnector(clearnet.Object, i2p.Object);

        // DnsEndPoint is neither IPEndPoint nor I2pEndPoint
        var act = () => composite.ConnectAsync(new DnsEndPoint("example.com", 80));
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
