using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.TrackerCommunication.Udp;

/// <summary>
/// Optimized UDP tracker client with global connection ID caching.
/// Based on libtorrent's udp_tracker_connection implementation.
/// </summary>
public class UdpTrackerClient : ITrackerClient
{
    private const long ProtocolId = 0x41727101980; // Magic constant
    private const int ActionConnect = 0;
    private const int ActionAnnounce = 1;
    private const int ActionScrape = 2;
    private const int ActionError = 3;

    // Default UDP tracker port per BEP 15
    private const int DefaultUdpTrackerPort = 6969;

    // Connection ID expiry (120 seconds per BEP 15 — maximizes cache hits during startup bursts)
    private static readonly TimeSpan ConnectionIdExpiry = TimeSpan.FromSeconds(120);

    // UDP socket buffer sizes for better performance
    private const int UdpSendBufferSize = 64 * 1024;   // 64KB
    private const int UdpReceiveBufferSize = 64 * 1024; // 64KB

    private readonly IOptionsMonitor<TrackerSettings> _trackerMonitor;
    private readonly ILogger<UdpTrackerClient> _logger;
    private readonly DnsCache _dnsCache;
    private readonly string _host;
    private readonly int _port;
    private IPEndPoint _resolvedEndpoint;
    private readonly Network.UdpSocketManager? _socketManager;
    private readonly UdpTrackerPacketHandler? _packetHandler;
    private bool UseSharedSocket => _socketManager != null && _packetHandler != null;

    private int _failureCount;
    private DateTime? _lastAnnounce;

    public string TrackerUrl { get; }
    public TrackerType Type => TrackerType.Udp;
    public bool IsAvailable => _failureCount < 5;
    public DateTime? LastAnnounce => _lastAnnounce;
    public int FailureCount => _failureCount;

    public UdpTrackerClient(string trackerUrl, IOptionsMonitor<TrackerSettings> trackerMonitor,
        ILogger<UdpTrackerClient> logger, DnsCache dnsCache,
        Network.UdpSocketManager? socketManager = null,
        UdpTrackerPacketHandler? packetHandler = null)
    {
        if (string.IsNullOrWhiteSpace(trackerUrl))
            throw new ArgumentException("Tracker URL cannot be empty", nameof(trackerUrl));

        if (!trackerUrl.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Tracker URL must start with udp://");

        TrackerUrl = trackerUrl;
        _trackerMonitor = trackerMonitor ?? throw new ArgumentNullException(nameof(trackerMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dnsCache = dnsCache ?? throw new ArgumentNullException(nameof(dnsCache));
        _socketManager = socketManager;
        _packetHandler = packetHandler;

        var uri = new Uri(trackerUrl);
        _host = uri.Host;
        _port = uri.Port > 0 ? uri.Port : DefaultUdpTrackerPort;

        _resolvedEndpoint = IPAddress.TryParse(_host, out var ip) ? new IPEndPoint(ip, _port) : null;

        _logger.LogDebug("UdpTrackerClient created for {TrackerUrl} (shared socket: {UseShared})",
            TrackerUrl, UseSharedSocket);
    }

    public async Task<TrackerResponse> AnnounceAsync(TrackerRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        _logger.LogDebug("Announcing to UDP tracker {TrackerUrl} [Event: {Event}]", TrackerUrl, request.Event);

        if (_trackerMonitor.CurrentValue.SsrfMitigation)
        {
            if (SsrfGuard.ShouldBlock(_host))
            {
                _failureCount++;
                _logger.LogWarning("SSRF mitigation: blocking UDP tracker request to private address {Host}", _host);
                return TrackerResponse.CreateFailure("SSRF mitigation: tracker resolves to private address", TrackerUrl);
            }
        }

        UdpClient? udpClient = null;
        try
        {
            if (!UseSharedSocket)
                udpClient = CreateOptimizedUdpClient();

            var endpoint = _resolvedEndpoint;
            if (endpoint == null)
            {
                var addresses = await _dnsCache.ResolveAsync(_host, cancellationToken).ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    endpoint = new IPEndPoint(addresses[0], _port);
                    _resolvedEndpoint = endpoint;
                }
            }
            if (endpoint == null)
            {
                _failureCount++;
                return TrackerResponse.CreateFailure("Failed to resolve tracker hostname", TrackerUrl);
            }

            var connectionId = await GetConnectionIdAsync(udpClient, endpoint, cancellationToken);
            if (connectionId == 0)
            {
                _failureCount++;
                return TrackerResponse.CreateFailure("Failed to connect to UDP tracker", TrackerUrl);
            }

            var announceResponse = await SendAnnounceAsync(udpClient, endpoint, connectionId, request, cancellationToken);

            if (announceResponse.IsSuccess)
            {
                _failureCount = 0;
                _lastAnnounce = DateTime.UtcNow;
                _logger.LogDebug("UDP tracker {TrackerUrl} returned {Peers} peers, {Seeders} seeders",
                    TrackerUrl, announceResponse.Peers.Count, announceResponse.Complete);
            }
            else
            {
                _failureCount++;
                UdpConnectionCache.Invalidate(_host, _port);
            }

            return announceResponse;
        }
        catch (SocketException ex)
        {
            _failureCount++;
            UdpConnectionCache.Invalidate(_host, _port);
            _logger.LogWarning(ex, "Socket error announcing to UDP tracker {TrackerUrl}", TrackerUrl);
            return TrackerResponse.CreateFailure($"Socket error: {ex.Message}", TrackerUrl);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Announce to {TrackerUrl} was cancelled", TrackerUrl);
            throw;
        }
        catch (TimeoutException)
        {
            _failureCount++;
            _logger.LogWarning("Timeout announcing to UDP tracker {TrackerUrl}", TrackerUrl);
            return TrackerResponse.CreateFailure("Request timed out", TrackerUrl);
        }
        catch (Exception ex)
        {
            _failureCount++;
            UdpConnectionCache.Invalidate(_host, _port);
            _logger.LogError(ex, "Error announcing to UDP tracker {TrackerUrl}", TrackerUrl);
            return TrackerResponse.CreateFailure($"Error: {ex.Message}", TrackerUrl);
        }
        finally
        {
            udpClient?.Dispose();
        }
    }

    /// <summary>
    /// Creates an optimized UDP client with proper buffer sizes.
    /// </summary>
    private UdpClient CreateOptimizedUdpClient()
    {
        var udpClient = new UdpClient();

        try
        {
            // Set socket buffer sizes for better performance
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, UdpSendBufferSize);
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, UdpReceiveBufferSize);

            // Set timeouts
            var timeoutMs = _trackerMonitor.CurrentValue.UdpTimeoutSeconds * 1000;
            udpClient.Client.ReceiveTimeout = timeoutMs;
            udpClient.Client.SendTimeout = timeoutMs;
        }
        catch (SocketException ex)
        {
            _logger.LogDebug("Failed to set UDP socket options: {Error}", ex.Message);
        }

        return udpClient;
    }

    /// <summary>
    /// Sends a request via the shared socket and waits for a response matched by transaction ID.
    /// </summary>
    private async Task<byte[]> SendAndReceiveSharedAsync(byte[] request, IPEndPoint endpoint,
        int transactionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _packetHandler!.RegisterPending(transactionId, tcs, endpoint);
        try
        {
            await _socketManager!.SendAsync(request, endpoint, UdpSendFlags.TrackerConnection).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _packetHandler.UnregisterPending(transactionId);
        }
    }

    /// <summary>
    /// Gets a connection ID, either from cache or via connect handshake.
    /// </summary>
    private async Task<long> GetConnectionIdAsync(UdpClient? udpClient, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        if (UdpConnectionCache.TryGetConnectionId(_host, _port, out var cachedConnectionId))
        {
            _logger.LogDebug("Using cached connection ID for {Host}:{Port}", _host, _port);
            return cachedConnectionId;
        }

        _logger.LogDebug("Connecting to UDP tracker {Host}:{Port}...", _host, _port);

        var trackerSettings = _trackerMonitor.CurrentValue;
        for (int attempt = 0; attempt < trackerSettings.MaxRetries; attempt++)
        {
            try
            {
                var txnId = GenerateTransactionId();

                var connectRequest = new byte[16];
                BinaryPrimitives.WriteInt64BigEndian(connectRequest.AsSpan(0, 8), ProtocolId);
                BinaryPrimitives.WriteInt32BigEndian(connectRequest.AsSpan(8, 4), ActionConnect);
                BinaryPrimitives.WriteInt32BigEndian(connectRequest.AsSpan(12, 4), txnId);

                var timeout = TimeSpan.FromSeconds(trackerSettings.UdpTimeoutSeconds * (1 << attempt));

                byte[] response;
                if (UseSharedSocket)
                {
                    response = await SendAndReceiveSharedAsync(connectRequest, endpoint, txnId, timeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await udpClient!.SendAsync(connectRequest, connectRequest.Length, endpoint);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(timeout);
                    var result = await udpClient.ReceiveAsync().WaitAsync(cts.Token);
                    response = result.Buffer;
                }

                if (response.Length < 16)
                {
                    _logger.LogWarning("Invalid connect response length: {Length}", response.Length);
                    continue;
                }

                int action = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(0, 4));
                int responseTxnId = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(4, 4));

                if (responseTxnId != txnId)
                {
                    _logger.LogWarning("Transaction ID mismatch: expected {Expected}, got {Got}", txnId, responseTxnId);
                    continue;
                }

                if (action == ActionError)
                {
                    string errorMessage = System.Text.Encoding.UTF8.GetString(response, 8, response.Length - 8);
                    _logger.LogWarning("UDP tracker error: {Error}", errorMessage);
                    return 0;
                }

                if (action != ActionConnect)
                {
                    _logger.LogWarning("Unexpected action in connect response: {Action}", action);
                    continue;
                }

                var connectionId = BinaryPrimitives.ReadInt64BigEndian(response.AsSpan(8, 8));
                UdpConnectionCache.SetConnectionId(_host, _port, connectionId, ConnectionIdExpiry);
                _logger.LogDebug("Connected to UDP tracker, connection_id: {ConnectionId} (cached)", connectionId);
                return connectionId;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Connect attempt {Attempt} timed out", attempt + 1);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug("Connect attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);
            }
        }

        return 0;
    }

    private async Task<TrackerResponse> SendAnnounceAsync(
        UdpClient? udpClient,
        IPEndPoint endpoint,
        long connectionId,
        TrackerRequest request,
        CancellationToken cancellationToken)
    {
        var txnId = GenerateTransactionId();

        // Build announce request (98 bytes) — unchanged
        var announceRequest = new byte[98];
        int offset = 0;

        BinaryPrimitives.WriteInt64BigEndian(announceRequest.AsSpan(offset, 8), connectionId);
        offset += 8;
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(offset, 4), ActionAnnounce);
        offset += 4;
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(offset, 4), txnId);
        offset += 4;
        Buffer.BlockCopy(request.InfoHash, 0, announceRequest, offset, 20);
        offset += 20;
        Buffer.BlockCopy(request.PeerId, 0, announceRequest, offset, 20);
        offset += 20;
        BinaryPrimitives.WriteInt64BigEndian(announceRequest.AsSpan(offset, 8), request.Downloaded);
        offset += 8;
        BinaryPrimitives.WriteInt64BigEndian(announceRequest.AsSpan(offset, 8), request.Left);
        offset += 8;
        BinaryPrimitives.WriteInt64BigEndian(announceRequest.AsSpan(offset, 8), request.Uploaded);
        offset += 8;
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(offset, 4), request.Event.ToUdpValue());
        offset += 4;

        int announceIpValue = 0;
        if (!string.IsNullOrEmpty(request.Ip) && IPAddress.TryParse(request.Ip, out var parsedIp)
            && parsedIp.AddressFamily == AddressFamily.InterNetwork)
        {
            var ipBytes = parsedIp.GetAddressBytes();
            announceIpValue = BinaryPrimitives.ReadInt32BigEndian(ipBytes);
        }
        else if (request.IsPrivateTorrent && !string.IsNullOrEmpty(request.Ipv4Address)
            && IPAddress.TryParse(request.Ipv4Address, out var privateIp)
            && privateIp.AddressFamily == AddressFamily.InterNetwork)
        {
            var ipBytes = privateIp.GetAddressBytes();
            announceIpValue = BinaryPrimitives.ReadInt32BigEndian(ipBytes);
        }
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(offset, 4), announceIpValue);
        offset += 4;
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(offset, 4), request.PeerKey);
        offset += 4;
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(offset, 4), request.NumWant);
        offset += 4;
        BinaryPrimitives.WriteUInt16BigEndian(announceRequest.AsSpan(offset, 2), (ushort)request.Port);

        // Send + receive with retries
        var announceSettings = _trackerMonitor.CurrentValue;
        for (int attempt = 0; attempt < announceSettings.MaxRetries; attempt++)
        {
            try
            {
                var timeout = TimeSpan.FromSeconds(announceSettings.UdpTimeoutSeconds * (1 << attempt));

                byte[] response;
                if (UseSharedSocket)
                {
                    if (attempt > 0)
                        txnId = GenerateTransactionId();
                    BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(12, 4), txnId);
                    response = await SendAndReceiveSharedAsync(announceRequest, endpoint, txnId, timeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await udpClient!.SendAsync(announceRequest, announceRequest.Length, endpoint);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(timeout);
                    var result = await udpClient.ReceiveAsync().WaitAsync(cts.Token);
                    response = result.Buffer;
                }

                if (response.Length < 20)
                {
                    _logger.LogWarning("Invalid announce response length: {Length}", response.Length);
                    continue;
                }

                int action = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(0, 4));
                int responseTxnId = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(4, 4));

                if (responseTxnId != txnId)
                {
                    _logger.LogWarning("Transaction ID mismatch in announce response");
                    continue;
                }

                if (action == ActionError)
                {
                    string errorMessage = System.Text.Encoding.UTF8.GetString(response, 8, response.Length - 8);
                    UdpConnectionCache.Invalidate(_host, _port);
                    return TrackerResponse.CreateFailure(errorMessage, TrackerUrl);
                }

                if (action != ActionAnnounce)
                {
                    _logger.LogWarning("Unexpected action in announce response: {Action}", action);
                    continue;
                }

                int interval = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(8, 4));
                int leechers = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(12, 4));
                int seeders = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(16, 4));

                var peers = new List<TrackerPeer>();
                int peerDataStart = 20;
                int peerDataLength = response.Length - peerDataStart;

                if (peerDataLength > 0 && peerDataLength % 6 == 0)
                {
                    for (int i = 0; i < peerDataLength; i += 6)
                    {
                        try
                        {
                            var peer = TrackerPeer.FromCompact(response, peerDataStart + i);
                            if (peer.Port > 0)
                                peers.Add(peer);
                        }
                        catch { }
                    }
                }

                return new TrackerResponse
                {
                    TrackerUrl = TrackerUrl,
                    Interval = interval,
                    Complete = seeders,
                    Incomplete = leechers,
                    Peers = peers
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Announce attempt {Attempt} timed out", attempt + 1);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug("Announce attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);
            }
        }

        UdpConnectionCache.Invalidate(_host, _port);
        return TrackerResponse.CreateFailure("Max retries exceeded", TrackerUrl);
    }

    public async Task<ScrapeResponse> ScrapeAsync(byte[] infoHash, CancellationToken cancellationToken = default)
    {
        if (infoHash == null || infoHash.Length != 20)
            throw new ArgumentException("InfoHash must be exactly 20 bytes");

        _logger.LogDebug("Scraping UDP tracker {TrackerUrl}...", TrackerUrl);

        UdpClient? udpClient = null;
        try
        {
            if (!UseSharedSocket)
                udpClient = CreateOptimizedUdpClient();

            var endpoint = _resolvedEndpoint;
            if (endpoint == null)
            {
                var addresses = await _dnsCache.ResolveAsync(_host, cancellationToken).ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    endpoint = new IPEndPoint(addresses[0], _port);
                    _resolvedEndpoint = endpoint;
                }
            }
            if (endpoint == null)
                return ScrapeResponse.CreateFailure("Failed to resolve tracker hostname");

            var connectionId = await GetConnectionIdAsync(udpClient, endpoint, cancellationToken);
            if (connectionId == 0)
                return ScrapeResponse.CreateFailure("Failed to connect to UDP tracker");

            var txnId = GenerateTransactionId();
            var scrapeRequest = new byte[36];
            int offset = 0;

            BinaryPrimitives.WriteInt64BigEndian(scrapeRequest.AsSpan(offset, 8), connectionId);
            offset += 8;
            BinaryPrimitives.WriteInt32BigEndian(scrapeRequest.AsSpan(offset, 4), ActionScrape);
            offset += 4;
            BinaryPrimitives.WriteInt32BigEndian(scrapeRequest.AsSpan(offset, 4), txnId);
            offset += 4;
            Buffer.BlockCopy(infoHash, 0, scrapeRequest, offset, 20);

            var timeout = TimeSpan.FromSeconds(_trackerMonitor.CurrentValue.UdpTimeoutSeconds);

            byte[] response;
            if (UseSharedSocket)
            {
                response = await SendAndReceiveSharedAsync(scrapeRequest, endpoint, txnId, timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await udpClient!.SendAsync(scrapeRequest, scrapeRequest.Length, endpoint);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                var result = await udpClient.ReceiveAsync().WaitAsync(cts.Token);
                response = result.Buffer;
            }

            if (response.Length < 20)
                return ScrapeResponse.CreateFailure("Invalid scrape response length");

            int action = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(0, 4));
            int responseTxnId = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(4, 4));

            if (responseTxnId != txnId)
                return ScrapeResponse.CreateFailure("Transaction ID mismatch");

            if (action == ActionError)
            {
                string errorMessage = System.Text.Encoding.UTF8.GetString(response, 8, response.Length - 8);
                return ScrapeResponse.CreateFailure(errorMessage);
            }

            if (action != ActionScrape)
                return ScrapeResponse.CreateFailure($"Unexpected action: {action}");

            int seeders = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(8, 4));
            int completed = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(12, 4));
            int leechers = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(16, 4));

            _logger.LogDebug("UDP scrape from {TrackerUrl}: {Seeders} seeders, {Leechers} leechers",
                TrackerUrl, seeders, leechers);

            return new ScrapeResponse
            {
                IsSuccess = true,
                Complete = seeders,
                Incomplete = leechers,
                Downloaded = completed
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ScrapeResponse.CreateFailure("Request timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scraping UDP tracker {TrackerUrl}", TrackerUrl);
            return ScrapeResponse.CreateFailure(ex.Message);
        }
        finally
        {
            udpClient?.Dispose();
        }
    }

    private int GenerateTransactionId()
    {
        return RandomNumberGenerator.GetInt32(int.MaxValue);
    }

    public void Dispose()
    {
        _logger.LogDebug("UdpTrackerClient disposed for {TrackerUrl}", TrackerUrl);
    }
}
