using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Abstractions.Interfaces.Transport;

/// <summary>
/// Abstraction over DHT network transport. Implementations handle
/// send/receive and network-specific compact format encoding.
/// </summary>
public interface IDhtTransport : IDisposable
{
    /// <summary>
    /// Send a DHT message to the target endpoint.
    /// </summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint target, CancellationToken ct = default);

    /// <summary>
    /// Register a callback invoked when a DHT packet is received.
    /// The callback receives (packet data, sender endpoint).
    /// </summary>
    void SetPacketHandler(Action<ReadOnlyMemory<byte>, EndPoint> handler);

    /// <summary>
    /// Start the transport (e.g., register with shared socket, start receive loop).
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Size of compact node info for this network (26 for IPv4, 54 for I2P).
    /// </summary>
    int CompactNodeInfoSize { get; }

    /// <summary>
    /// Encode a node entry to compact format for this network.
    /// </summary>
    byte[] EncodeCompactNodeInfo(object nodeEntry);

    /// <summary>
    /// Decode a node entry from compact format for this network.
    /// Returns (NodeId bytes, EndPoint, port).
    /// </summary>
    (byte[] nodeId, EndPoint endpoint, int port) DecodeCompactNodeInfo(ReadOnlySpan<byte> data, int offset);
}
