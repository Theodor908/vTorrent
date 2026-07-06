using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Core.DHT;

namespace vTorrent.Core.Network;

/// <summary>
/// Standalone DHT transport with its own UdpClient. Used when no shared
/// UdpSocketManager is available (testing, standalone DHT mode).
/// </summary>
public sealed class StandaloneDhtTransport : IDhtTransport
{
    private readonly int _port;
    private UdpClient? _udpClient;
    private Action<ReadOnlyMemory<byte>, EndPoint>? _handler;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public int CompactNodeInfoSize => 26;

    public StandaloneDhtTransport(int port)
    {
        _port = port;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        }
        catch (SocketException) when (_port != 0)
        {
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint target, CancellationToken ct = default)
    {
        if (_udpClient == null) throw new InvalidOperationException("Not started");
        if (target is not IPEndPoint ipTarget)
            throw new ArgumentException("StandaloneDhtTransport requires IPEndPoint", nameof(target));

        _udpClient.Send(data.Span, ipTarget);
        return ValueTask.CompletedTask;
    }

    public void SetPacketHandler(Action<ReadOnlyMemory<byte>, EndPoint> handler)
    {
        _handler = handler;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct).ConfigureAwait(false);
                _handler?.Invoke(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
        }
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
        _cts?.Cancel();
        _udpClient?.Dispose();
        _udpClient = null;
        _handler = null;
        _cts?.Dispose();
    }
}
