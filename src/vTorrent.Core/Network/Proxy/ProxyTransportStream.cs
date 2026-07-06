using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.Network.Proxy;

/// <summary>
/// Wraps a TcpClient connected through a proxy. Implements ITransportStream.
/// </summary>
public class ProxyTransportStream : ITransportStream
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly EndPoint? _remoteEndPoint;

    public ProxyTransportStream(TcpClient client, EndPoint? logicalRemoteEndpoint = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _stream = client.GetStream();
        _remoteEndPoint = logicalRemoteEndpoint;
    }

    public bool IsConnected => _client.Connected;
    public EndPoint? RemoteEndPoint => _remoteEndPoint ?? _client.Client.RemoteEndPoint;
    public TransportType TransportType => TransportType.Tcp;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);

    /// <summary>Returns the underlying NetworkStream for HttpClient ConnectCallback.</summary>
    public NetworkStream AsNetworkStream() => _stream;

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
