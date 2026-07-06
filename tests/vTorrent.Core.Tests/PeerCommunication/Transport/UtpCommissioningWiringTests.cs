using System;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication.Transport;

public class UtpCommissioningWiringTests
{
    [Fact]
    public void SessionUtpManager_IsRegisteredOnUdpSocket_AndSharedWithConnector()
    {
        var udp = new UdpSocketManager(logger: null);
        var utp = new UtpSocketManager((data, ep) => udp.SendAsync(data, ep, UdpSendFlags.PeerConnection));

        // The commissioning contract: register on the UDP demux and share with the connector.
        udp.SetUtpHandler(utp);
        var connector = new TransportConnector(utpManager: utp, new PeerSettings(), logger: null);

        connector.UtpManager.Should().BeSameAs(utp);
    }
}
