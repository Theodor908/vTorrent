using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Core.PeerCommunication.Transport;

namespace vTorrent.Tests.Helpers;

/// <summary>
/// In-memory full-duplex stream for testing MSE negotiation without network.
/// Uses anonymous pipes for cross-platform async I/O.
/// </summary>
internal sealed class DuplexMemoryStream : ITransportStream
{
    private readonly Stream _readStream;
    private readonly Stream _writeStream;

    public bool IsConnected => true;
    public EndPoint? RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 0);
    public TransportType TransportType => TransportType.Tcp;

    public DuplexMemoryStream(Stream readFrom, Stream writeTo)
    {
        _readStream = readFrom;
        _writeStream = writeTo;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => await _readStream.ReadAsync(buffer, ct).ConfigureAwait(false);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => await _writeStream.WriteAsync(buffer, ct).ConfigureAwait(false);

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    public void Dispose() { _readStream.Dispose(); _writeStream.Dispose(); }

    /// <summary>
    /// Creates a connected pair of DuplexMemoryStreams for loopback testing.
    /// </summary>
    public static (DuplexMemoryStream initiator, DuplexMemoryStream responder) CreatePair()
    {
        var aToB = new AnonymousPipeServerStream(PipeDirection.Out);
        var aToBClient = new AnonymousPipeClientStream(PipeDirection.In,
            aToB.GetClientHandleAsString());
        var bToA = new AnonymousPipeServerStream(PipeDirection.Out);
        var bToAClient = new AnonymousPipeClientStream(PipeDirection.In,
            bToA.GetClientHandleAsString());

        var initiator = new DuplexMemoryStream(readFrom: bToAClient, writeTo: aToB);
        var responder = new DuplexMemoryStream(readFrom: aToBClient, writeTo: bToA);

        return (initiator, responder);
    }
}
