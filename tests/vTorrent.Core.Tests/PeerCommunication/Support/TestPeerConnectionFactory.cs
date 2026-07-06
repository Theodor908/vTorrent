using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Tests.PeerCommunication.Support;

/// <summary>
/// Builds minimally-configured <see cref="PeerConnection"/> instances against an in-memory
/// transport for tests. Shared across the handshake / inbound-connection test suites.
/// </summary>
public static class TestPeerConnectionFactory
{
    public static PeerConnection CreateIncoming(ITransportStream transport)
    {
        return new PeerConnection(
            PeerInfo.Incoming(new IPEndPoint(IPAddress.Loopback, 6881)),
            new PeerSettings(),
            transport,
            NullLogger<PeerConnection>.Instance,
            loggerFactory: NullLoggerFactory.Instance);
    }
}
