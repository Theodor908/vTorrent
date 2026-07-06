using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.Tests.PeerCommunication.Support;

/// <summary>
/// ITransportStream test double representing one dialed connection. Records writes; its ReadAsync
/// throws (default IOException) to make an MSE handshake fail deterministically. When the code path
/// under test never reads (e.g. MSE skipped), ReadAsync is simply never invoked.
/// </summary>
public sealed class FakeDialStream : ITransportStream
{
    private readonly Exception _readError;

    public FakeDialStream(Exception? readError = null)
        => _readError = readError ?? new IOException("peer closed");

    public List<byte[]> Writes { get; } = new();
    public bool Disposed { get; private set; }
    public bool IsConnected => !Disposed;
    public EndPoint? RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 6881);
    public TransportType TransportType => TransportType.Utp;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => throw _readError;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Writes.Add(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public void Dispose() => Disposed = true;
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
