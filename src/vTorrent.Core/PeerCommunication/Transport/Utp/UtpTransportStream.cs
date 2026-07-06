using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.PeerCommunication.Transport.Utp;

/// <summary>
/// Adapts the packet-oriented UtpSocket into ITransportStream (stream semantics).
/// PeerConnection reads/writes through this — identical API to TcpTransportStream.
/// </summary>
public sealed class UtpTransportStream : ITransportStream
{
    private readonly UtpSocket _socket;
    private bool _disposed;

    public TransportType TransportType => TransportType.Utp;

    public bool IsConnected => !_disposed &&
        _socket.State == UtpConnectionState.Connected;

    public EndPoint? RemoteEndPoint => _socket.RemoteEndPoint;

    public UtpTransportStream(UtpSocket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _socket.ReadAsync(buffer, ct);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _socket.SendAsync(buffer, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
