using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Core.Network;
using vTorrent.Core.Network.Proxy;
using vTorrent.Core.PeerCommunication.Transport.Tcp;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PeerCommunication.Transport;

/// <summary>
/// uTP-first transport connector with TCP fallback.
/// Tries uTP with a short timeout; on failure, falls back to TCP.
/// </summary>
public sealed class TransportConnector : ITransportConnector
{
    private readonly UtpSocketManager? _utpManager;
    private readonly PeerSettings _settings;
    private readonly IOptionsMonitor<ConnectionSettings>? _connectionMonitor;
    private readonly ILogger<TransportConnector>? _logger;
    private HolepunchManager? _holepunchManager;
    private readonly IProxyConnector? _proxyConnector;
    private readonly bool _proxyPeerConnections;
    private readonly VpnKillSwitch? _killSwitch;
    private readonly vTorrent.Core.Network.IpFilter.IpFilter? _ipFilter;

    private const int DefaultUtpConnectTimeoutMs = 5_000;

    // uTP connect timeout, honoured from PeerSettings so it can be tuned for
    // environments where UDP is heavily filtered. Falls back to the default
    // when the configured value is non-positive. Exposed internally for tests.
    internal int UtpConnectTimeoutMs =>
        _settings.UtpConnectTimeoutMs > 0 ? _settings.UtpConnectTimeoutMs : DefaultUtpConnectTimeoutMs;

    public TransportConnector(
        UtpSocketManager? utpManager,
        PeerSettings settings,
        ILogger<TransportConnector>? logger = null)
    {
        _utpManager = utpManager;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger;
    }

    public TransportConnector(
        UtpSocketManager? utpManager,
        PeerSettings settings,
        HolepunchManager? holepunchManager,
        ILogger<TransportConnector>? logger = null)
        : this(utpManager, settings, logger)
    {
        _holepunchManager = holepunchManager;
    }

    public TransportConnector(
        UtpSocketManager? utpManager,
        PeerSettings settings,
        HolepunchManager? holepunchManager,
        IProxyConnector? proxyConnector,
        bool proxyPeerConnections,
        VpnKillSwitch? killSwitch,
        IOptionsMonitor<ConnectionSettings>? connectionMonitor = null,
        vTorrent.Core.Network.IpFilter.IpFilter? ipFilter = null,
        ILogger<TransportConnector>? logger = null)
        : this(utpManager, settings, holepunchManager, logger)
    {
        _proxyConnector = proxyConnector;
        _proxyPeerConnections = proxyPeerConnections;
        _killSwitch = killSwitch;
        _connectionMonitor = connectionMonitor;
        _ipFilter = ipFilter;
    }

    /// <summary>
    /// Late-bind a HolepunchManager (used when it must be created after the connector).
    /// </summary>
    public void SetHolepunchManager(HolepunchManager? manager) => _holepunchManager = manager;

    /// <summary>
    /// Exposes the underlying UtpSocketManager so callers can create a HolepunchManager.
    /// </summary>
    public UtpSocketManager? UtpManager => _utpManager;

    public async Task<ITransportStream> ConnectAsync(EndPoint endpoint, CancellationToken ct = default)
    {
        var ipEndpoint = endpoint as IPEndPoint
            ?? throw new ArgumentException("TransportConnector only supports IPEndPoint", nameof(endpoint));

        // IP filter check
        if (_ipFilter != null && _ipFilter.Access(ipEndpoint.Address) == Network.IpFilter.AccessFlags.Blocked)
            throw new InvalidOperationException($"Connection to {ipEndpoint.Address} blocked by IP filter");

        // VPN kill-switch check
        if (_killSwitch?.ShouldBlock() == true)
            throw new VpnDisconnectedException();

        // Privileged port check
        var connSettings = _connectionMonitor?.CurrentValue;
        if (connSettings?.NoConnectPrivilegedPorts == true && ipEndpoint.Port > 0 && ipEndpoint.Port < 1024)
        {
            _logger?.LogDebug("[TRANSPORT] Rejecting connection to privileged port {Port}", ipEndpoint.Port);
            throw new InvalidOperationException($"Connection to privileged port {ipEndpoint.Port} blocked by NoConnectPrivilegedPorts setting");
        }

        // When proxy is active for peer connections, bypass uTP and go direct through proxy
        if (_proxyConnector != null && _proxyPeerConnections)
        {
            return await _proxyConnector.ConnectThroughProxyAsync(
                ipEndpoint.Address.ToString(), ipEndpoint.Port, ct).ConfigureAwait(false);
        }

        bool canUtp = _utpManager != null && (connSettings?.EnableOutgoingUtp ?? true);
        bool canTcp = connSettings?.EnableOutgoingTcp ?? true;

        if (!canUtp && !canTcp)
            throw new InvalidOperationException("Both uTP and TCP outgoing connections are disabled");

        // 1. Try uTP first (if enabled)
        if (canUtp)
        {
            try
            {
                using var utpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                utpCts.CancelAfter(UtpConnectTimeoutMs);

                var utpSocket = await _utpManager.ConnectAsync(ipEndpoint, utpCts.Token)
                    .ConfigureAwait(false);
                _logger?.LogDebug("Connected to {Endpoint} via uTP", ipEndpoint);
                return new UtpTransportStream(utpSocket);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (!canTcp) throw;
                _logger?.LogDebug("uTP timeout for {Endpoint}, falling back to TCP", ipEndpoint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!canTcp) throw;
                _logger?.LogDebug(ex, "uTP failed for {Endpoint}, falling back to TCP", ipEndpoint);
            }
        }

        // 2. Fall back to TCP (if enabled)
        if (canTcp)
        {
            var tcpConnector = new TcpTransportConnector(_settings);
            var stream = await tcpConnector.ConnectAsync(ipEndpoint, ct).ConfigureAwait(false);
            _logger?.LogDebug("Connected to {Endpoint} via TCP{Fallback}", ipEndpoint, canUtp ? " (fallback)" : "");
            return stream;
        }

        throw new InvalidOperationException($"No enabled transport could connect to {ipEndpoint}");
    }

    /// <summary>
    /// Connect with PeerInfo context, enabling holepunch fallback when direct uTP and TCP both fail.
    /// </summary>
    public async Task<ITransportStream> ConnectAsync(EndPoint endpoint, PeerInfo? peerInfo, CancellationToken ct = default)
    {
        var ipEndpoint = endpoint as IPEndPoint
            ?? throw new ArgumentException("TransportConnector only supports IPEndPoint", nameof(endpoint));

        // IP filter check
        if (_ipFilter != null && _ipFilter.Access(ipEndpoint.Address) == Network.IpFilter.AccessFlags.Blocked)
            throw new InvalidOperationException($"Connection to {ipEndpoint.Address} blocked by IP filter");

        // VPN kill-switch check
        if (_killSwitch?.ShouldBlock() == true)
            throw new VpnDisconnectedException();

        // Privileged port check
        var connSettings = _connectionMonitor?.CurrentValue;
        if (connSettings?.NoConnectPrivilegedPorts == true && ipEndpoint.Port > 0 && ipEndpoint.Port < 1024)
        {
            _logger?.LogDebug("[TRANSPORT] Rejecting connection to privileged port {Port}", ipEndpoint.Port);
            throw new InvalidOperationException($"Connection to privileged port {ipEndpoint.Port} blocked by NoConnectPrivilegedPorts setting");
        }

        // When proxy is active for peer connections, bypass uTP and go direct through proxy
        if (_proxyConnector != null && _proxyPeerConnections)
        {
            return await _proxyConnector.ConnectThroughProxyAsync(
                ipEndpoint.Address.ToString(), ipEndpoint.Port, ct).ConfigureAwait(false);
        }

        bool canUtp = _utpManager != null && (connSettings?.EnableOutgoingUtp ?? true);
        bool canTcp = connSettings?.EnableOutgoingTcp ?? true;

        if (!canUtp && !canTcp)
            throw new InvalidOperationException("Both uTP and TCP outgoing connections are disabled");

        // 1. Try uTP first (if enabled)
        if (canUtp)
        {
            try
            {
                using var utpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                utpCts.CancelAfter(UtpConnectTimeoutMs);

                var utpSocket = await _utpManager.ConnectAsync(ipEndpoint, utpCts.Token)
                    .ConfigureAwait(false);
                _logger?.LogDebug("Connected to {Endpoint} via uTP", ipEndpoint);
                return new UtpTransportStream(utpSocket);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (!canTcp) throw;
                _logger?.LogDebug("uTP timeout for {Endpoint}, falling back to TCP", ipEndpoint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!canTcp) throw;
                _logger?.LogDebug(ex, "uTP failed for {Endpoint}, falling back to TCP", ipEndpoint);
            }
        }

        // 2. Try TCP fallback (if enabled)
        Exception? tcpException = null;
        if (canTcp)
        {
            try
            {
                var tcpConnector = new TcpTransportConnector(_settings);
                var stream = await tcpConnector.ConnectAsync(ipEndpoint, ct).ConfigureAwait(false);
                _logger?.LogDebug("Connected to {Endpoint} via TCP{Fallback}", ipEndpoint, canUtp ? " (fallback)" : "");
                return stream;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tcpException = ex;
                _logger?.LogDebug(ex, "TCP{Fallback} failed for {Endpoint}", canUtp ? " fallback" : "", ipEndpoint);
            }
        }

        // 3. Try holepunch if peer supports it and manager is available
        if (peerInfo?.SupportsHolepunch == true && _holepunchManager != null)
        {
            _logger?.LogDebug("Attempting holepunch to {Endpoint}", ipEndpoint);
            var holepunchStream = await _holepunchManager.InitiateAsync(ipEndpoint, ct).ConfigureAwait(false);
            if (holepunchStream != null)
            {
                _logger?.LogDebug("Connected to {Endpoint} via holepunch", ipEndpoint);
                return holepunchStream;
            }
        }

        // 4. All transports failed
        throw new Exception($"All connection attempts failed for {ipEndpoint}", tcpException);
    }
}
