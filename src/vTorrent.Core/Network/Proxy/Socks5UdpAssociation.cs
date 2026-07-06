using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.Proxy;

/// <summary>
/// SOCKS5 UDP ASSOCIATE — relays UDP packets through a SOCKS5 proxy.
/// Maintains a TCP control channel and wraps/unwraps UDP packets with SOCKS5 headers.
/// </summary>
public class Socks5UdpAssociation : IAsyncDisposable
{
    private readonly ProxySettings _settings;
    private readonly ILogger? _logger;
    private TcpClient? _controlChannel;
    private Socket? _udpSocket;
    private IPEndPoint? _relayEndpoint;
    private CancellationTokenSource? _cts;

    public bool IsActive => _controlChannel?.Connected == true && _relayEndpoint != null;

    public Socks5UdpAssociation(ProxySettings settings, ILogger? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var connector = new Socks5ProxyConnector(_settings,
            auth: _settings.Type == ProxyType.Socks5Password);

        var (control, relay) = await connector.CreateUdpAssociationAsync(ct).ConfigureAwait(false);
        _controlChannel = control;
        _relayEndpoint = relay;

        // If relay address is 0.0.0.0, use the proxy hostname instead
        if (_relayEndpoint.Address.Equals(IPAddress.Any))
        {
            var proxyAddrs = await Dns.GetHostAddressesAsync(_settings.Hostname, ct).ConfigureAwait(false);
            _relayEndpoint = new IPEndPoint(proxyAddrs[0], _relayEndpoint.Port);
        }

        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint target, CancellationToken ct)
    {
        if (_udpSocket == null || _relayEndpoint == null)
            throw new InvalidOperationException("UDP association not started");

        var wrapped = WrapPacket(data.Span, target);
        _udpSocket.SendTo(wrapped, SocketFlags.None, _relayEndpoint);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Push-based receive — runs a loop, unwraps packets, calls callback.
    /// </summary>
    public void StartReceiveLoop(Action<ReadOnlyMemory<byte>, IPEndPoint> onPacketReceived, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            var buffer = new byte[65536];
            var senderEp = new IPEndPoint(IPAddress.Any, 0);

            while (!ct.IsCancellationRequested && _udpSocket != null)
            {
                try
                {
                    var result = await _udpSocket.ReceiveFromAsync(
                        buffer.AsMemory(), SocketFlags.None, senderEp, ct).ConfigureAwait(false);

                    var (payload, originalSender) = UnwrapPacket(
                        buffer.AsSpan(0, result.ReceivedBytes));

                    if (payload.Length > 0)
                        onPacketReceived(payload, originalSender);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[SOCKS5_UDP] Receive error");
                }
            }
        }, ct);
    }

    /// <summary>
    /// Wraps a UDP packet with SOCKS5 UDP header.
    /// Format: RSV(2, 0x0000) + FRAG(1, 0x00) + ATYP(1) + ADDR(4/16) + PORT(2) + DATA
    /// </summary>
    internal static byte[] WrapPacket(ReadOnlySpan<byte> data, IPEndPoint target)
    {
        var addrBytes = target.Address.GetAddressBytes();
        byte atyp = (byte)(addrBytes.Length == 4 ? 0x01 : 0x04);
        var wrapped = new byte[6 + addrBytes.Length + data.Length];

        wrapped[0] = 0x00; // RSV
        wrapped[1] = 0x00; // RSV
        wrapped[2] = 0x00; // FRAG
        wrapped[3] = atyp;
        addrBytes.CopyTo(wrapped, 4);
        wrapped[4 + addrBytes.Length] = (byte)(target.Port >> 8);
        wrapped[5 + addrBytes.Length] = (byte)(target.Port & 0xFF);
        data.CopyTo(wrapped.AsSpan(6 + addrBytes.Length));

        return wrapped;
    }

    /// <summary>
    /// Unwraps a SOCKS5 UDP packet, extracting the original sender and payload.
    /// Returns the byte offset where payload starts and the sender endpoint.
    /// </summary>
    internal static UnwrapResult UnwrapPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
            return new UnwrapResult(Array.Empty<byte>(), new IPEndPoint(IPAddress.Any, 0));

        byte atyp = data[3];
        int addrLen;
        switch (atyp)
        {
            case 0x01: addrLen = 4; break;
            case 0x04: addrLen = 16; break;
            case 0x03: addrLen = 1 + data[4]; break;
            default: return new UnwrapResult(Array.Empty<byte>(), new IPEndPoint(IPAddress.Any, 0));
        }

        int portOffset = 4 + addrLen;
        if (data.Length < portOffset + 2)
            return new UnwrapResult(Array.Empty<byte>(), new IPEndPoint(IPAddress.Any, 0));

        int port = (data[portOffset] << 8) | data[portOffset + 1];
        int payloadOffset = portOffset + 2;

        IPAddress addr;
        if (atyp == 0x01)
            addr = new IPAddress(data.Slice(4, 4));
        else if (atyp == 0x04)
            addr = new IPAddress(data.Slice(4, 16));
        else
            addr = IPAddress.Any;

        return new UnwrapResult(data.Slice(payloadOffset).ToArray(), new IPEndPoint(addr, port));
    }

    internal readonly record struct UnwrapResult(byte[] Payload, IPEndPoint Sender)
    {
        public void Deconstruct(out byte[] payload, out IPEndPoint sender)
        {
            payload = Payload;
            sender = Sender;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _udpSocket?.Dispose();
        if (_controlChannel != null)
        {
            _controlChannel.Close();
            _controlChannel.Dispose();
        }
    }
}
