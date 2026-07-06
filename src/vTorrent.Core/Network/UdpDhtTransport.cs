using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Core.DHT;

namespace vTorrent.Core.Network;

/// <summary>
/// Clearnet DHT transport. Wraps UdpSocketManager, registers as DHT packet handler.
/// </summary>
public sealed class UdpDhtTransport : IDhtTransport, IUdpPacketHandler
{
    private readonly IUdpSocketManagerDht _socketManager;
    private Action<ReadOnlyMemory<byte>, EndPoint>? _handler;

    public int CompactNodeInfoSize => 26;

    public UdpDhtTransport(IUdpSocketManagerDht socketManager)
    {
        _socketManager = socketManager ?? throw new ArgumentNullException(nameof(socketManager));
    }

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint target, CancellationToken ct = default)
    {
        if (target is not IPEndPoint ipTarget)
            throw new ArgumentException("UdpDhtTransport requires IPEndPoint", nameof(target));
        return _socketManager.SendAsync(data, ipTarget, UdpSendFlags.None);
    }

    public void SetPacketHandler(Action<ReadOnlyMemory<byte>, EndPoint> handler)
    {
        _handler = handler;
        _socketManager.SetDhtHandler(handler != null ? this : null);
    }

    // IUdpPacketHandler — called by UdpSocketManager when DHT packet arrives
    public void ProcessPacket(ReadOnlyMemory<byte> data, IPEndPoint sender)
    {
        _handler?.Invoke(data, sender);
    }

    public byte[] EncodeCompactNodeInfo(object nodeEntry)
    {
        var entry = (NodeEntry)nodeEntry;
        return entry.ToCompact();
    }

    public (byte[] nodeId, EndPoint endpoint, int port) DecodeCompactNodeInfo(ReadOnlySpan<byte> data, int offset)
    {
        var slice = data.Slice(offset, 26);
        var nodeId = slice.Slice(0, 20).ToArray();
        var ip = new IPAddress(slice.Slice(20, 4));
        int port = (slice[24] << 8) | slice[25];
        return (nodeId, new IPEndPoint(ip, port), port);
    }

    public void Dispose()
    {
        _socketManager.SetDhtHandler(null);
        _handler = null;
    }
}
