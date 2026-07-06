using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;
using vTorrent.Core.DHT;
using vTorrent.Core.DHT.I2P;

namespace vTorrent.Core.Network.I2P;

/// <summary>
/// I2P DHT transport. Wraps I2pDatagramClient for SAM datagram send/receive.
/// </summary>
public sealed class I2pDhtTransport : IDhtTransport
{
    private readonly I2pDatagramClient _datagramClient;
    private readonly I2pDestination _localDestination;
    private readonly byte[] _nodeId;
    private readonly ushort _port;
    private readonly ILogger? _logger;
    private Action<ReadOnlyMemory<byte>, EndPoint>? _handler;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public int CompactNodeInfoSize => 54;
    public byte[] NodeId => _nodeId;

    public I2pDhtTransport(I2pDatagramClient datagramClient, I2pDestination localDestination,
        ushort port, ILogger? logger)
    {
        _datagramClient = datagramClient;
        _localDestination = localDestination ?? throw new ArgumentNullException(nameof(localDestination));
        _port = port;
        _logger = logger;
        _nodeId = I2pSecureNodeId.Generate(localDestination, port);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint target, CancellationToken ct = default)
    {
        if (target is not I2pEndPoint i2pTarget)
            throw new ArgumentException("I2pDhtTransport requires I2pEndPoint", nameof(target));

        var destBase64 = i2pTarget.Destination.Base64Destination
            ?? i2pTarget.Destination.ToBase32();

        return new ValueTask(_datagramClient.SendDatagramAsync(destBase64, data.ToArray(), ct));
    }

    public void SetPacketHandler(Action<ReadOnlyMemory<byte>, EndPoint> handler)
    {
        _handler = handler;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (sender, payload) = await _datagramClient.ReceiveDatagramAsync(ct)
                    .ConfigureAwait(false);

                var dest = I2pDestination.FromBase64(sender);
                var endpoint = new I2pEndPoint(dest);

                _handler?.Invoke(payload, endpoint);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "I2P DHT receive error");
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public byte[] EncodeCompactNodeInfo(object nodeEntry)
    {
        var entry = (NodeEntry)nodeEntry;
        if (entry.NetworkEndPoint is not I2pEndPoint i2pEp)
            throw new ArgumentException("Expected I2P node entry");

        var result = new byte[54];
        entry.Id.Bytes.CopyTo(result.AsSpan(0, 20));
        i2pEp.Destination.Hash.ToArray().CopyTo(result.AsSpan(20, 32));
        result[52] = (byte)(entry.Port >> 8);
        result[53] = (byte)(entry.Port & 0xFF);
        return result;
    }

    public (byte[] nodeId, EndPoint endpoint, int port) DecodeCompactNodeInfo(ReadOnlySpan<byte> data, int offset)
    {
        var slice = data.Slice(offset, 54);
        var nodeId = slice.Slice(0, 20).ToArray();
        var destHash = slice.Slice(20, 32).ToArray();
        ushort port = (ushort)((slice[52] << 8) | slice[53]);

        var dest = I2pDestination.FromHash(destHash);
        var endpoint = new I2pEndPoint(dest);
        return (nodeId, endpoint, port);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _handler = null;
        _cts?.Dispose();
    }
}
