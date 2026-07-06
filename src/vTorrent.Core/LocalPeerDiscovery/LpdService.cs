using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.LocalPeerDiscovery;

/// <summary>
/// Implements BEP 14 Local Peer Discovery (LPD/LSD).
/// Discovers peers on the local network via UDP multicast.
/// Mirrors libtorrent's lsd.cpp implementation.
/// </summary>
public sealed class LpdService : IDisposable
{
    // BEP 14 multicast addresses
    private static readonly IPAddress MulticastAddressV4 = IPAddress.Parse("239.192.152.143");
    private static readonly IPAddress MulticastAddressV6 = IPAddress.Parse("ff15::efc0:988f");
    private const int LpdPort = 6771;
    private const int MaxRetries = 3;
    private const int DefaultAnnounceIntervalSeconds = 300; // 5 minutes

    private readonly ILogger<LpdService> _logger;
    private readonly IOptionsMonitor<ConnectionSettings>? _connectionMonitor;
    private readonly string _cookie;
    private readonly ConcurrentDictionary<string, LpdTorrentEntry> _torrents = new();

    private UdpClient? _udpClientV4;
    private UdpClient? _udpClientV6;
    private CancellationTokenSource? _cts;
    private Task? _receiveTaskV4;
    private Task? _receiveTaskV6;
    private Task? _announceTask;
    private bool _disposed;
    private bool _isRunning;

    /// <summary>
    /// Fired when a peer is discovered via LPD.
    /// Parameters: (infoHash bytes, list of discovered peers)
    /// </summary>
    public event Action<byte[], List<PeerInfo>>? PeersDiscovered;

    public bool IsRunning => _isRunning;

    public LpdService(ILogger<LpdService> logger, IOptionsMonitor<ConnectionSettings>? connectionMonitor = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionMonitor = connectionMonitor;
        // Generate unique cookie to filter our own announcements (libtorrent pattern)
        _cookie = (Random.Shared.Next(0, int.MaxValue) ^ (RuntimeHelpers.GetHashCode(this) & 0x7FFFFFFF))
            .ToString("x");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // IPv4 multicast setup
            _udpClientV4 = new UdpClient();
            _udpClientV4.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClientV4.Client.Bind(new IPEndPoint(IPAddress.Any, LpdPort));
            _udpClientV4.JoinMulticastGroup(MulticastAddressV4);
            _udpClientV4.MulticastLoopback = true;
            _udpClientV4.Ttl = 32;

            _receiveTaskV4 = ReceiveLoopAsync(_udpClientV4, "IPv4", _cts.Token);
            _logger.LogDebug("[LPD] IPv4 multicast started on {Address}:{Port}", MulticastAddressV4, LpdPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LPD] Failed to start IPv4 multicast");
            _udpClientV4?.Dispose();
            _udpClientV4 = null;
        }

        try
        {
            // IPv6 multicast setup (optional — not all networks support it)
            _udpClientV6 = new UdpClient(AddressFamily.InterNetworkV6);
            _udpClientV6.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClientV6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, LpdPort));
            _udpClientV6.JoinMulticastGroup(MulticastAddressV6);
            _udpClientV6.MulticastLoopback = true;

            _receiveTaskV6 = ReceiveLoopAsync(_udpClientV6, "IPv6", _cts.Token);
            _logger.LogDebug("[LPD] IPv6 multicast started on [{Address}]:{Port}", MulticastAddressV6, LpdPort);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LPD] IPv6 multicast not available (this is normal on many networks)");
            _udpClientV6?.Dispose();
            _udpClientV6 = null;
        }

        if (_udpClientV4 == null && _udpClientV6 == null)
        {
            _logger.LogWarning("[LPD] No multicast sockets available, LPD disabled");
            return;
        }

        _announceTask = AnnounceLoopAsync(_cts.Token);
        _isRunning = true;
        _logger.LogDebug("[LPD] Local Peer Discovery started (cookie={Cookie})", _cookie);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Register a torrent for LPD announcements.
    /// </summary>
    public void RegisterTorrent(byte[] infoHash, int listenPort, bool isPrivate = false)
    {
        if (isPrivate)
        {
            _logger.LogDebug("[LPD] Skipping private torrent registration");
            return;
        }

        var hex = Convert.ToHexString(infoHash).ToLowerInvariant();
        _torrents[hex] = new LpdTorrentEntry(infoHash, listenPort);
        _logger.LogDebug("[LPD] Registered torrent {InfoHash}", hex);
    }

    /// <summary>
    /// Unregister a torrent from LPD announcements.
    /// </summary>
    public void UnregisterTorrent(byte[] infoHash)
    {
        var hex = Convert.ToHexString(infoHash).ToLowerInvariant();
        _torrents.TryRemove(hex, out _);
        _logger.LogDebug("[LPD] Unregistered torrent {InfoHash}", hex);
    }

    /// <summary>
    /// Round-robin announce loop. Distributes announces evenly across torrents.
    /// Interval = 300/N seconds per torrent (libtorrent pattern).
    /// </summary>
    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        // Small startup delay to let sockets settle
        await Task.Delay(2000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = _torrents.Values.ToList();
                if (entries.Count == 0)
                {
                    await Task.Delay(5000, ct);
                    continue;
                }

                int delayMs = Math.Max((_connectionMonitor?.CurrentValue.LsdAnnounceInterval ?? DefaultAnnounceIntervalSeconds) * 1000 / entries.Count, 1000);

                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested) break;

                    await AnnounceWithRetriesAsync(entry, ct);
                    await Task.Delay(delayMs, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LPD] Error in announce loop");
                await Task.Delay(5000, ct);
            }
        }
    }

    /// <summary>
    /// Announce a torrent with up to 3 retries (libtorrent pattern: 2s, 4s, 6s backoff).
    /// </summary>
    private async Task AnnounceWithRetriesAsync(LpdTorrentEntry entry, CancellationToken ct)
    {
        var infoHashHex = Convert.ToHexString(entry.InfoHash).ToLowerInvariant();

        for (int retry = 0; retry <= MaxRetries; retry++)
        {
            if (ct.IsCancellationRequested) return;
            if (retry > 0)
            {
                await Task.Delay(2000 * retry, ct);
            }

            try
            {
                // Send on IPv4
                if (_udpClientV4 != null)
                {
                    var msg = RenderLsdPacket(infoHashHex, entry.ListenPort, isV6: false);
                    var endpoint = new IPEndPoint(MulticastAddressV4, LpdPort);
                    await _udpClientV4.SendAsync(msg, endpoint, ct);
                }

                // Send on IPv6
                if (_udpClientV6 != null)
                {
                    var msg = RenderLsdPacket(infoHashHex, entry.ListenPort, isV6: true);
                    var endpoint = new IPEndPoint(MulticastAddressV6, LpdPort);
                    await _udpClientV6.SendAsync(msg, endpoint, ct);
                }

                _logger.LogDebug("[LPD] Announced {InfoHash} (retry={Retry})", infoHashHex, retry);
                return; // Success, no more retries
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "[LPD] Send failed for {InfoHash} (retry={Retry})", infoHashHex, retry);
            }
        }
    }

    /// <summary>
    /// Renders a BT-SEARCH packet per BEP 14.
    /// </summary>
    private byte[] RenderLsdPacket(string infoHashHex, int port, bool isV6)
    {
        var host = isV6
            ? $"[{MulticastAddressV6}]:{LpdPort}"
            : $"{MulticastAddressV4}:{LpdPort}";

        var message = $"BT-SEARCH * HTTP/1.1\r\nHost: {host}\r\nPort: {port}\r\nInfohash: {infoHashHex}\r\ncookie: {_cookie}\r\n\r\n\r\n";
        return Encoding.ASCII.GetBytes(message);
    }

    /// <summary>
    /// Receive loop for incoming BT-SEARCH multicast messages.
    /// </summary>
    private async Task ReceiveLoopAsync(UdpClient client, string protocol, CancellationToken ct)
    {
        _logger.LogDebug("[LPD] Starting {Protocol} receive loop", protocol);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(ct);
                ProcessIncomingMessage(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows ICMP port unreachable — benign, same as DHT
                continue;
            }
            catch (SocketException ex)
            {
                if (!ct.IsCancellationRequested)
                    _logger.LogDebug(ex, "[LPD] {Protocol} socket error", protocol);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _logger.LogError(ex, "[LPD] {Protocol} receive error", protocol);
            }
        }

        _logger.LogDebug("[LPD] {Protocol} receive loop stopped", protocol);
    }

    /// <summary>
    /// Parse an incoming BT-SEARCH message and emit peer if we have the torrent.
    /// </summary>
    private void ProcessIncomingMessage(byte[] data, IPEndPoint sender)
    {
        try
        {
            var text = Encoding.ASCII.GetString(data);

            // Must start with BT-SEARCH
            if (!text.StartsWith("BT-SEARCH", StringComparison.OrdinalIgnoreCase))
                return;

            // Parse headers
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines.Skip(1)) // Skip method line
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;

                var key = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                headers[key] = value;
            }

            // Check cookie — filter our own announcements
            if (headers.TryGetValue("cookie", out var cookie) &&
                string.Equals(cookie, _cookie, StringComparison.OrdinalIgnoreCase))
            {
                return; // Our own announcement
            }

            // Extract port
            if (!headers.TryGetValue("Port", out var portStr) || !int.TryParse(portStr, out var port))
                return;

            if (port <= 0 || port > 65535)
                return;

            // Extract infohash — BEP 14 allows multiple but we handle one per message
            if (!headers.TryGetValue("Infohash", out var infoHashHex))
                return;

            // Validate: must be 40-char hex
            infoHashHex = infoHashHex.Trim().ToLowerInvariant();
            if (infoHashHex.Length != 40)
                return;

            // Check if we have this torrent
            if (!_torrents.ContainsKey(infoHashHex))
                return;

            // Build peer endpoint from sender's IP + announced port
            var peerEndpoint = new IPEndPoint(sender.Address, port);
            var peer = new PeerInfo(sender.Address, port, source: "LPD");
            var infoHashBytes = Convert.FromHexString(infoHashHex);

            _logger.LogDebug("[LPD] Discovered peer {Peer} for {InfoHash}",
                peerEndpoint, infoHashHex);

            PeersDiscovered?.Invoke(infoHashBytes, new List<PeerInfo> { peer });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LPD] Error parsing message from {Sender}", sender);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _isRunning = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        try { _udpClientV4?.DropMulticastGroup(MulticastAddressV4); } catch { }
        try { _udpClientV6?.DropMulticastGroup(MulticastAddressV6); } catch { }

        _udpClientV4?.Dispose();
        _udpClientV6?.Dispose();
        _cts?.Dispose();
    }

    private sealed class LpdTorrentEntry
    {
        public byte[] InfoHash { get; }
        public int ListenPort { get; }

        public LpdTorrentEntry(byte[] infoHash, int listenPort)
        {
            InfoHash = infoHash;
            ListenPort = listenPort;
        }
    }
}
