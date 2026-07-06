using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.Tests.PeerCommunication.Support;

/// <summary>
/// ITransportConnector test double. Hands out the pre-queued streams in order (one per dial) and
/// counts how many times ConnectAsync was called, so tests can assert the establishment reordered
/// through the connector — and how many dials the plaintext-fallback path performed.
/// </summary>
public sealed class RecordingTransportConnector : ITransportConnector
{
    private readonly Queue<ITransportStream> _streams;

    public RecordingTransportConnector(params ITransportStream[] streams)
        => _streams = new Queue<ITransportStream>(streams);

    public int CallCount { get; private set; }
    public List<EndPoint> DialedEndpoints { get; } = new();

    public Task<ITransportStream> ConnectAsync(EndPoint endpoint, CancellationToken ct = default)
    {
        CallCount++;
        DialedEndpoints.Add(endpoint);
        if (_streams.Count == 0)
            throw new InvalidOperationException("RecordingTransportConnector: no more queued streams");
        return Task.FromResult(_streams.Dequeue());
    }
}
