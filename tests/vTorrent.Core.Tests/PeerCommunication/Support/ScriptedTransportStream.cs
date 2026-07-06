using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.Tests.PeerCommunication.Support;

/// <summary>
/// In-memory <see cref="ITransportStream"/> test double.
/// Serves bytes from a fixed "read script" to <see cref="ReadAsync"/> calls, recording every
/// <see cref="WriteAsync"/> for later assertions. Once the script is exhausted, further reads
/// block until cancelled (they never return 0), so tests can prove a code path did not depend
/// on reading from the wire.
/// </summary>
public sealed class ScriptedTransportStream : ITransportStream
{
    private static readonly byte[] HandshakeHeader = Encoding.ASCII.GetBytes("BitTorrent protocol");

    private readonly byte[] _readScript;
    private int _readPosition;
    private readonly List<byte[]> _writes = new();

    public ScriptedTransportStream(byte[] readScript)
    {
        _readScript = readScript ?? Array.Empty<byte>();
    }

    public bool Disposed { get; private set; }

    public IReadOnlyList<byte[]> Writes => _writes;

    public bool IsConnected => !Disposed;

    public EndPoint? RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 6881);

    public TransportType TransportType => TransportType.Tcp;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_readPosition >= _readScript.Length)
        {
            // Script exhausted: block until cancelled rather than returning 0, so tests can
            // prove a code path never performed a wire read.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0; // unreachable; Task.Delay(Infinite, ct) always throws on cancellation
        }

        int available = _readScript.Length - _readPosition;
        int toCopy = Math.Min(available, buffer.Length);
        _readScript.AsSpan(_readPosition, toCopy).CopyTo(buffer.Span);
        _readPosition += toCopy;
        return toCopy;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _writes.Add(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// True when the most recent write is a 68-byte BitTorrent handshake
    /// (pstrlen=19 followed by ASCII "BitTorrent protocol").
    /// </summary>
    public bool LastWriteWasHandshake()
    {
        if (_writes.Count == 0)
            return false;

        byte[] last = _writes[^1];
        if (last.Length != 68)
            return false;

        if (last[0] != 0x13)
            return false;

        return last.AsSpan(1, HandshakeHeader.Length).SequenceEqual(HandshakeHeader);
    }

    public void Dispose()
    {
        Disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
