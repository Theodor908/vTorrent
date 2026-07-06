using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network.Proxy;

namespace vTorrent.Core.Network;

/// <summary>
/// Minimal interface for UdpDhtTransport to depend on for DHT handler registration and sending.
/// </summary>
public interface IUdpSocketManagerDht
{
    void SetDhtHandler(IUdpPacketHandler handler);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint target);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint target, UdpSendFlags flags);
}

/// <summary>
/// Shared UDP socket manager. Single socket, single receive loop.
/// Demuxes incoming packets via four-stage cascade (mirroring libtorrent session_impl.cpp):
///   1. uTP — version nibble == 1, type 0-4, length >= 20
///   2. DHT — first byte 'd', last byte 'e' (bencode dictionary bookends)
///   3. Tracker — first 4 bytes as uint32 in {0,1,2,3} (BEP 15 action), length >= 8
///   4. Drop — unrecognized protocol silently ignored
/// Uses raw Socket (not UdpClient) for pooled-buffer receives.
/// </summary>
public sealed class UdpSocketManager : IDisposable, IUdpSocketManagerDht
{
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private IUdpPacketHandler? _utpHandler;
    private IUdpPacketHandler? _dhtHandler;
    private IUdpPacketHandler? _trackerHandler;
    private bool _disposed;

    private Socks5UdpAssociation? _proxyAssociation;
    private ProxySettings? _proxySettings;
    private readonly ILogger<UdpSocketManager>? _logger;
    private IUdpSendSink? _sendSink;
    private int _loggedProxyDrop;
    private readonly SemaphoreSlim _startStopGate = new(1, 1);

    public UdpSocketManager(ILogger<UdpSocketManager>? logger = null) => _logger = logger;

    public int LocalPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    // Test seam: configure proxy routing + send sink without binding a real socket.
    internal void ConfigureProxyRoutingForTest(
        ProxySettings proxySettings, IUdpSendSink sink, Socks5UdpAssociation? association = null)
    {
        _proxySettings = proxySettings;
        _sendSink = sink;
        _proxyAssociation = association;
    }

    public void SetUtpHandler(IUdpPacketHandler handler) => _utpHandler = handler;
    public void SetDhtHandler(IUdpPacketHandler handler) => _dhtHandler = handler;
    public void SetTrackerHandler(IUdpPacketHandler handler) => _trackerHandler = handler;

    public async Task StartAsync(IPEndPoint bindEndpoint, CancellationToken ct, ProxySettings? proxySettings = null)
    {
        await _startStopGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Idempotent: tear down any previous socket/association before re-binding.
            // The VPN kill-switch rebind path may call StartAsync without a preceding StopAsync.
            await StopInternalAsync().ConfigureAwait(false);

            // New association lifetime — allow one fail-closed drop warning again.
            Interlocked.Exchange(ref _loggedProxyDrop, 0);

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                _socket.Bind(bindEndpoint);
            }
            catch (SocketException) when (bindEndpoint.Port != 0)
            {
                _socket.Bind(new IPEndPoint(bindEndpoint.Address, 0));
            }

            // uTP carries bulk piece traffic over this shared UDP socket. A single 256 KB
            // BitTorrent piece is ~180 datagrams, so a 64 KB kernel receive buffer overflows
            // the instant a peer bursts a piece — dropping packets and forcing slow uTP
            // retransmission recovery. Size the buffers for bulk transfer (libtorrent likewise
            // uses MB-scale socket buffers). Receive is the bottleneck under inbound bursts.
            _socket.ReceiveBufferSize = 4 * 1024 * 1024;
            _socket.SendBufferSize = 1 * 1024 * 1024;
            _sendSink = new SocketUdpSendSink(_socket);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            _proxySettings = proxySettings;

            // Initialize SOCKS5 UDP association if configured
            if (_proxySettings != null
                && (_proxySettings.Type == ProxyType.Socks5 || _proxySettings.Type == ProxyType.Socks5Password))
            {
                try
                {
                    _proxyAssociation = new Socks5UdpAssociation(_proxySettings, null);
                    await _proxyAssociation.StartAsync(ct).ConfigureAwait(false);

                    // Receive loop — unwrapped packets feed into the demuxer
                    _proxyAssociation.StartReceiveLoop((data, sender) =>
                    {
                        RouteReceivedPacket(data, sender);
                    }, ct);
                }
                catch (Exception ex)
                {
                    // Fail closed: proxied UDP (DHT/tracker) is DROPPED, never sent direct.
                    _proxyAssociation = null;
                    _logger?.LogWarning(ex,
                        "SOCKS5 UDP association failed to start. Proxied DHT/tracker traffic " +
                        "will be DROPPED (not sent direct). Check proxy credentials and restart.");
                }
            }

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    /// <summary>
    /// Stops the receive loop and tears down the socket + proxy association,
    /// allowing StartAsync to be called again.
    /// </summary>
    public async Task StopAsync()
    {
        await _startStopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    // Un-gated teardown. Callers MUST hold _startStopGate.
    private async Task StopInternalAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            if (_receiveTask != null)
            {
                try { await _receiveTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
        }
        _receiveTask = null;

        if (_proxyAssociation != null)
        {
            try { await _proxyAssociation.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Error disposing SOCKS5 UDP association during stop"); }
            _proxyAssociation = null;
        }

        _socket?.Dispose();
        _socket = null;
        _sendSink = null;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint target)
        => SendAsync(data, target, UdpSendFlags.None);

    /// <summary>
    /// Send a UDP packet. If the flags + settings require proxying, the packet is
    /// sent through the SOCKS5 UDP association when active; otherwise it is DROPPED
    /// (fail-closed) — it is never sent direct. Mirrors libtorrent udp_socket::send.
    /// </summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint target, UdpSendFlags flags)
    {
        if (ShouldUseProxy(flags, _proxySettings))
        {
            if (_proxyAssociation != null && _proxyAssociation.IsActive)
                return _proxyAssociation.SendAsync(data, target, default);

            // Fail closed: proxy required but the association is unavailable.
            WarnProxyDropOnce();
            return ValueTask.CompletedTask;
        }

        if (_sendSink == null) throw new InvalidOperationException("Not started");
        _sendSink.Send(data.Span, target);
        return ValueTask.CompletedTask;
    }

    private void WarnProxyDropOnce()
    {
        if (Interlocked.Exchange(ref _loggedProxyDrop, 1) == 0)
        {
            _logger?.LogWarning(
                "Dropping proxied UDP datagram: SOCKS5 UDP association is not active. " +
                "Traffic is NOT sent direct (fail-closed). Further drops are suppressed.");
        }
    }

    /// <summary>
    /// Determines whether a UDP packet with the given flags should be routed through the SOCKS5 proxy.
    /// Mirrors libtorrent's per-protocol proxy decision logic.
    /// </summary>
    public static bool ShouldUseProxy(UdpSendFlags flags, ProxySettings? settings)
    {
        if (settings == null || settings.Type == ProxyType.None)
            return false;

        // Only SOCKS5 supports UDP association
        if (settings.Type != ProxyType.Socks5 && settings.Type != ProxyType.Socks5Password)
            return false;

        if (flags.HasFlag(UdpSendFlags.PeerConnection))
            return settings.ProxyPeerConnections;

        if (flags.HasFlag(UdpSendFlags.TrackerConnection))
            return settings.ProxyTrackerConnections;

        // Untagged = DHT
        return settings.ProxyDht;
    }

    /// <summary>
    /// Discriminates uTP from DHT by first-byte inspection.
    /// uTP v1: version=1 in low nibble, type 0-4 in high nibble.
    /// DHT: bencoded dict starts with 'd' (0x64).
    /// </summary>
    public static bool IsUtpPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) return false;
        byte version = (byte)(data[0] & 0x0F);
        byte type = (byte)(data[0] >> 4);
        return version == 1 && type <= 4;
    }

    /// <summary>
    /// Validates that a packet looks like a DHT bencoded dictionary.
    /// DHT messages always start with 'd' (0x64) and end with 'e' (0x65).
    /// Mirrors libtorrent session_impl.cpp:2637-2643.
    /// </summary>
    public static bool IsDhtPacket(ReadOnlySpan<byte> data)
    {
        return data.Length >= 2
            && data[0] == (byte)'d'
            && data[data.Length - 1] == (byte)'e';
    }

    /// <summary>
    /// Validates that a packet looks like a BEP 15 UDP tracker response.
    /// Tracker responses start with a 4-byte big-endian action (0-3)
    /// followed by a 4-byte transaction ID. Minimum 8 bytes.
    /// </summary>
    public static bool IsTrackerPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;
        uint action = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4));
        return action <= 3; // 0=connect, 1=announce, 2=scrape, 3=error
    }

    private void RouteReceivedPacket(ReadOnlyMemory<byte> data, IPEndPoint sender)
    {
        if (IsUtpPacket(data.Span))
            _utpHandler?.ProcessPacket(data, sender);
        else if (IsDhtPacket(data.Span))
            _dhtHandler?.ProcessPacket(data, sender);
        else if (IsTrackerPacket(data.Span))
            _trackerHandler?.ProcessPacket(data, sender);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        var senderEp = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _socket!.ReceiveFromAsync(
                        buffer.AsMemory(), SocketFlags.None, senderEp, ct)
                        .ConfigureAwait(false);

                    var data = buffer.AsMemory(0, result.ReceivedBytes);
                    var sender = (IPEndPoint)result.RemoteEndPoint;

                    RouteReceivedPacket(data, sender);
                    // else: unrecognized protocol — silently drop (libtorrent parity)
                }
                catch (SocketException) { }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _socket?.Dispose();
        _socket = null;
        if (_proxyAssociation != null)
        {
            try
            {
                // Bounded — never block shutdown indefinitely on async disposal.
                if (!_proxyAssociation.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)))
                    _logger?.LogWarning("Timed out disposing SOCKS5 UDP association");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing SOCKS5 UDP association");
            }
            _proxyAssociation = null;
        }
        _startStopGate.Dispose();
    }
}
