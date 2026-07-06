using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Core.Network;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Network;

public class UdpDhtTransportTests
{
    [Fact]
    public void SetPacketHandler_RegistersAsDhtHandler()
    {
        var socketManager = new Mock<IUdpSocketManagerDht>();
        var transport = new UdpDhtTransport(socketManager.Object);

        transport.SetPacketHandler((data, ep) => { });

        socketManager.Verify(m => m.SetDhtHandler(It.IsAny<IUdpPacketHandler>()), Times.Once);
    }

    [Fact]
    public void ProcessPacket_InvokesHandler()
    {
        var socketManager = new Mock<IUdpSocketManagerDht>();
        var transport = new UdpDhtTransport(socketManager.Object);
        EndPoint receivedFrom = null;
        byte[] receivedData = null;

        transport.SetPacketHandler((data, ep) =>
        {
            receivedData = data.ToArray();
            receivedFrom = ep;
        });

        var sender = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);
        var packet = new byte[] { 0x64, 0x65 };
        transport.ProcessPacket(packet, sender);

        receivedData.Should().BeEquivalentTo(packet);
        receivedFrom.Should().Be(sender);
    }

    [Fact]
    public void CompactNodeInfoSize_Is26()
    {
        var socketManager = new Mock<IUdpSocketManagerDht>();
        var transport = new UdpDhtTransport(socketManager.Object);

        transport.CompactNodeInfoSize.Should().Be(26);
    }

    [Fact]
    public void Dispose_DeregistersHandler()
    {
        var socketManager = new Mock<IUdpSocketManagerDht>();
        var transport = new UdpDhtTransport(socketManager.Object);
        transport.SetPacketHandler((data, ep) => { });

        transport.Dispose();

        socketManager.Verify(m => m.SetDhtHandler(null), Times.Once);
    }
}
