using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Network.I2P;

namespace vTorrent.Core.PeerCommunication.Transport.I2P;

/// <summary>
/// ITransportConnector that connects to I2P peers via SAM STREAM CONNECT.
/// Each connection opens a new TCP socket to the SAM bridge.
/// </summary>
public sealed class I2pTransportConnector : ITransportConnector
{
    private readonly I2pSamSession _session;

    public I2pTransportConnector(I2pSamSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task<ITransportStream> ConnectAsync(EndPoint endpoint, CancellationToken ct = default)
    {
        var i2pEndpoint = endpoint as I2pEndPoint
            ?? throw new ArgumentException("I2pTransportConnector only supports I2pEndPoint", nameof(endpoint));

        if (!_session.IsConnected)
            throw new InvalidOperationException("I2P SAM session is not connected");

        var destination = i2pEndpoint.Destination;

        // Need the full base64 destination for STREAM CONNECT
        // If we only have the hash, we need to resolve it via naming lookup
        string base64Dest;
        if (destination.Base64Destination != null)
        {
            base64Dest = destination.Base64Destination;
        }
        else
        {
            // Resolve via b32 address
            var b32 = destination.ToBase32();
            var lookupClient = new I2pSamClient(_session.SamHostname, _session.SamPort);
            await lookupClient.HandshakeAsync(ct).ConfigureAwait(false);
            base64Dest = await lookupClient.NamingLookupAsync(b32, ct).ConfigureAwait(false);
            await lookupClient.DisposeAsync().ConfigureAwait(false);
        }

        // Open new SAM connection for this stream
        var streamClient = new I2pSamClient(_session.SamHostname, _session.SamPort);
        await streamClient.HandshakeAsync(ct).ConfigureAwait(false);
        await streamClient.StreamConnectAsync(_session.SessionId!, base64Dest, ct).ConfigureAwait(false);

        // The raw TCP stream to SAM bridge now carries peer data
        var networkStream = streamClient.RawStream
            ?? throw new I2pSamException("No network stream after STREAM CONNECT");

        return new I2pTransportStream(networkStream, destination);
    }
}
