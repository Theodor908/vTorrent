using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PeerCommunication.Transport;

/// <summary>
/// Routes ConnectAsync to either clearnet or I2P transport connector
/// based on the runtime type of the EndPoint. Only used in mixed mode.
/// </summary>
public sealed class CompositeTransportConnector : ITransportConnector
{
    private readonly ITransportConnector _clearnet;
    private readonly ITransportConnector _i2p;

    public CompositeTransportConnector(ITransportConnector clearnet, ITransportConnector i2p)
    {
        _clearnet = clearnet ?? throw new ArgumentNullException(nameof(clearnet));
        _i2p = i2p ?? throw new ArgumentNullException(nameof(i2p));
    }

    public Task<ITransportStream> ConnectAsync(EndPoint endpoint, CancellationToken ct = default)
    {
        return endpoint switch
        {
            I2pEndPoint => _i2p.ConnectAsync(endpoint, ct),
            IPEndPoint => _clearnet.ConnectAsync(endpoint, ct),
            _ => throw new ArgumentException($"Unsupported endpoint type: {endpoint.GetType().Name}", nameof(endpoint))
        };
    }
}
