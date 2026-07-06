using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PeerCommunication.Transport.I2P;

/// <summary>
/// ITransportStream over a SAM STREAM connection. Wraps the NetworkStream
/// from the SAM bridge TCP socket after STREAM CONNECT/ACCEPT completes.
/// </summary>
public sealed class I2pTransportStream : ITransportStream
{
    private readonly NetworkStream _stream;
    private readonly I2pEndPoint _remoteEndPoint;
    private bool _disposed;

    public I2pTransportStream(NetworkStream stream, I2pDestination remoteDestination)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _remoteEndPoint = new I2pEndPoint(
            remoteDestination ?? throw new ArgumentNullException(nameof(remoteDestination)));
    }

    public bool IsConnected => !_disposed && _stream.CanRead;
    public EndPoint? RemoteEndPoint => _remoteEndPoint;
    public TransportType TransportType => TransportType.I2p;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        return await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _stream.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
