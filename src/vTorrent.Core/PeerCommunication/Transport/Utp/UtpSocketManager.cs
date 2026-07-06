using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using vTorrent.Core.Network;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.PeerCommunication.Transport.Utp;

/// <summary>
/// Multiplexes multiple UtpSocket connections over a single UDP send path.
/// Routes incoming packets by connection_id to the correct UtpSocket.
/// Implements IUdpPacketHandler so UdpSocketManager can route uTP packets here.
/// </summary>
public sealed class UtpSocketManager : IDisposable, IUdpPacketHandler
{
    private readonly ConcurrentDictionary<ushort, UtpSocket> _sockets = new();
    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, ValueTask> _sendDatagram;
    private readonly Channel<UtpSocket> _acceptQueue =
        Channel.CreateBounded<UtpSocket>(64);
    private readonly Timer _tickTimer;
    private bool _disposed;

    /// <summary>Number of live registered uTP sockets (test observation of teardown cleanup).</summary>
    internal int RegisteredSocketCount => _sockets.Count;

    public UtpSocketManager(Func<ReadOnlyMemory<byte>, IPEndPoint, ValueTask> sendDatagram)
    {
        _sendDatagram = sendDatagram ?? throw new ArgumentNullException(nameof(sendDatagram));
        _tickTimer = new Timer(_ => Tick(), null, 50, 50);
    }

    public async Task<UtpSocket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = UtpSocket.CreateOutgoing(endpoint, _sendDatagram);
        Register(socket);
        await socket.ConnectAsync(ct).ConfigureAwait(false);
        return socket;
    }

    public async Task<UtpSocket> AcceptAsync(CancellationToken ct)
    {
        return await _acceptQueue.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// IUdpPacketHandler implementation — called by UdpSocketManager for incoming uTP packets.
    /// </summary>
    public void ProcessPacket(ReadOnlyMemory<byte> data, IPEndPoint sender)
        => ProcessIncomingPacket(data, sender);

    public void ProcessIncomingPacket(ReadOnlyMemory<byte> data, IPEndPoint sender)
    {
        if (!UtpPacketHeader.TryParse(data.Span, out var header))
            return;

        if (header.Type == UtpPacketType.Syn)
        {
            var socket = UtpSocket.CreateIncoming(header, sender, _sendDatagram);
            Register(socket);
            _acceptQueue.Writer.TryWrite(socket);
            return;
        }

        if (_sockets.TryGetValue(header.ConnectionId, out var existing))
        {
            existing.ProcessIncomingPacket(data, sender);
            return;
        }

        SendReset(header.ConnectionId, sender);
    }

    public void Tick()
    {
        // Runs on a Timer thread — an escaping exception would crash the process, so guard
        // each socket independently and never let one failure abort the sweep.
        foreach (var (_, socket) in _sockets)
        {
            try
            {
                socket.Tick();
                if (socket.State is UtpConnectionState.Closed or UtpConnectionState.Reset)
                    Unregister(socket);
            }
            catch
            {
                // best-effort per-socket tick
            }
        }
    }

    private void Register(UtpSocket socket)
    {
        _sockets[socket.RecvConnectionId] = socket;
    }

    private void Unregister(UtpSocket socket)
    {
        _sockets.TryRemove(socket.RecvConnectionId, out _);
    }

    private void SendReset(ushort connectionId, IPEndPoint target)
    {
        var reset = new UtpPacketHeader(
            type: UtpPacketType.Reset,
            connectionId: connectionId,
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: 0,
            windowSize: 0,
            sequenceNumber: 0,
            ackNumber: 0);

        var buffer = new byte[UtpPacketHeader.Size];
        reset.WriteTo(buffer);
        _ = _sendDatagram(buffer, target);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tickTimer.Dispose();
        foreach (var (_, socket) in _sockets)
            socket.Dispose();
        _sockets.Clear();
    }
}
