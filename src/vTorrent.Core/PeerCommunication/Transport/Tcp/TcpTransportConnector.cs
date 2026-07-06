using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.PeerCommunication.Transport.Tcp;

/// <summary>
/// TCP-only transport connector. Will be replaced by TransportConnector
/// (uTP-first with TCP fallback) once the uTP stack is complete.
/// </summary>
public sealed class TcpTransportConnector : ITransportConnector
{
    private readonly PeerSettings _settings;
    private readonly IPAddress? _bindAddress;

    public TcpTransportConnector(PeerSettings settings, IPAddress? bindAddress = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _bindAddress = bindAddress;
    }

    public async Task<ITransportStream> ConnectAsync(EndPoint endpoint, CancellationToken ct = default)
    {
        var ipEndpoint = endpoint as IPEndPoint
            ?? throw new ArgumentException("TcpTransportConnector only supports IPEndPoint", nameof(endpoint));

        var client = new TcpClient();
        try
        {
            // Bind to specific interface if configured (for VPN enforcement)
            if (_bindAddress != null)
                client.Client.Bind(new IPEndPoint(_bindAddress, 0));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.ConnectTimeout));

            await client.ConnectAsync(ipEndpoint.Address, ipEndpoint.Port, timeoutCts.Token).ConfigureAwait(false);
            return new TcpTransportStream(client, _settings);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
