using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Core.PeerCommunication.Transport.Tcp;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.PeerCommunication.Transport;

/// <summary>
/// Unified incoming connection listener. Accepts both TCP and uTP connections
/// and presents them through a single AcceptAsync interface.
/// </summary>
public sealed class TransportListener : ITransportListener
{
    private readonly UtpSocketManager? _utpManager;
    private readonly PeerSettings _settings;
    private readonly ILogger<TransportListener>? _logger;
    private readonly IOptionsMonitor<ConnectionSettings>? _connectionMonitor;
    private readonly Channel<ITransportStream> _acceptQueue =
        Channel.CreateBounded<ITransportStream>(64);

    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private Task? _tcpAcceptTask;
    private Task? _utpAcceptTask;

    public TransportListener(
        UtpSocketManager? utpManager,
        PeerSettings settings,
        ILogger<TransportListener>? logger = null,
        IOptionsMonitor<ConnectionSettings>? connectionMonitor = null)
    {
        _utpManager = utpManager;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger;
        _connectionMonitor = connectionMonitor;
    }

    /// <summary>The actual TCP port bound (resolves OS-assigned port when bound to 0). 0 if not started.</summary>
    public int BoundPort =>
        _tcpListener?.LocalEndpoint is IPEndPoint ep ? ep.Port : 0;

    public Task StartAsync(EndPoint bindEndpoint, CancellationToken ct = default)
    {
        var ipBindEndpoint = bindEndpoint as IPEndPoint
            ?? throw new ArgumentException("TransportListener only supports IPEndPoint", nameof(bindEndpoint));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (_connectionMonitor?.CurrentValue.EnableIncomingTcp != false)
        {
            _tcpListener = new TcpListener(ipBindEndpoint);
            try
            {
                _tcpListener.Start(backlog: 10);
            }
            catch (SocketException ex) when (_connectionMonitor?.CurrentValue.ListenSystemPortFallback == true)
            {
                _logger?.LogWarning(ex, "Failed to bind to port {Port}, falling back to OS-assigned port", ipBindEndpoint.Port);
                _tcpListener = new TcpListener(new IPEndPoint(ipBindEndpoint.Address, 0));
                _tcpListener.Start(backlog: 10);
                _logger?.LogInformation("Listening on fallback port {Port}", ((IPEndPoint)_tcpListener.LocalEndpoint).Port);
            }

            _tcpAcceptTask = Task.Run(() => AcceptTcpLoopAsync(_cts.Token), _cts.Token);
        }

        if (_utpManager != null && _connectionMonitor?.CurrentValue.EnableIncomingUtp != false)
        {
            _utpAcceptTask = Task.Run(() => AcceptUtpLoopAsync(_cts.Token), _cts.Token);
        }

        _logger?.LogInformation("Transport listener started on {Endpoint} (TCP + uTP)",
            ipBindEndpoint);

        return Task.CompletedTask;
    }

    public async Task<ITransportStream> AcceptAsync(CancellationToken ct = default)
    {
        return await _acceptQueue.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    private async Task AcceptTcpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var stream = new TcpTransportStream(client, _settings);
                await _acceptQueue.Writer.WriteAsync(stream, ct).ConfigureAwait(false);
                _logger?.LogDebug("Accepted incoming TCP connection from {Endpoint}",
                    client.Client.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                _logger?.LogDebug(ex, "TCP accept error");
            }
        }
    }

    private async Task AcceptUtpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var utpSocket = await _utpManager.AcceptAsync(ct).ConfigureAwait(false);
                var stream = new UtpTransportStream(utpSocket);
                await _acceptQueue.Writer.WriteAsync(stream, ct).ConfigureAwait(false);
                _logger?.LogDebug("Accepted incoming uTP connection from {Endpoint}",
                    utpSocket.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "uTP accept error");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _tcpListener?.Stop();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
