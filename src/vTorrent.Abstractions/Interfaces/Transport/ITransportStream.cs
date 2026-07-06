using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using vTorrent.Abstractions.Enums;

namespace vTorrent.Abstractions.Interfaces.Transport;

public interface ITransportStream : IAsyncDisposable, IDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
    bool IsConnected { get; }
    EndPoint? RemoteEndPoint { get; }
    TransportType TransportType { get; }
}
