using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.PeerCommunication.Encryption;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.PeerCommunication.Transport;

/// <summary>
/// Session-level owner of a single <see cref="TransportListener"/>. Accepts raw incoming
/// streams, identifies the target torrent by info-hash (via MSE req2 lookup, or by peeking
/// the plaintext 68-byte BitTorrent handshake), and routes the connection to that torrent's
/// <see cref="PeerManager.AcceptIncomingPeerAsync"/>.
/// </summary>
public sealed class IncomingConnectionDispatcher : IAsyncDisposable
{
    private readonly TransportListener _listener;
    private readonly Func<string, PeerManager?> _resolvePeerManager;
    private readonly Func<byte[], byte[]?> _req2HashLookup;
    private readonly IOptionsMonitor<EncryptionSettings> _encryptionMonitor;
    private readonly Func<int> _connectedPeerCount;
    private readonly Func<int> _maxSessionConnections;
    private readonly ILogger<IncomingConnectionDispatcher> _logger;
    private readonly ILogger<MseNegotiator> _mseLogger;

    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    public IncomingConnectionDispatcher(
        TransportListener listener,
        Func<string, PeerManager?> resolvePeerManager,
        Func<byte[], byte[]?> req2HashLookup,
        IOptionsMonitor<EncryptionSettings> encryptionMonitor,
        ILoggerFactory loggerFactory,
        Func<int> connectedPeerCount,
        Func<int> maxSessionConnections)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _resolvePeerManager = resolvePeerManager ?? throw new ArgumentNullException(nameof(resolvePeerManager));
        _req2HashLookup = req2HashLookup ?? throw new ArgumentNullException(nameof(req2HashLookup));
        _encryptionMonitor = encryptionMonitor ?? throw new ArgumentNullException(nameof(encryptionMonitor));
        _connectedPeerCount = connectedPeerCount ?? throw new ArgumentNullException(nameof(connectedPeerCount));
        _maxSessionConnections = maxSessionConnections ?? throw new ArgumentNullException(nameof(maxSessionConnections));

        if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<IncomingConnectionDispatcher>();
        _mseLogger = loggerFactory.CreateLogger<MseNegotiator>();
    }

    public int BoundPort => _listener.BoundPort;

    public async Task StartAsync(EndPoint bindEndpoint, CancellationToken ct = default)
    {
        await _listener.StartAsync(bindEndpoint, ct).ConfigureAwait(false);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ITransportStream? stream = null;
            try
            {
                stream = await _listener.AcceptAsync(ct).ConfigureAwait(false);
                await HandleAcceptedStreamAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while handling an incoming connection");
                if (stream != null)
                {
                    try { await stream.DisposeAsync().ConfigureAwait(false); }
                    catch { /* best effort */ }
                }
            }
        }
    }

    private async Task HandleAcceptedStreamAsync(ITransportStream stream, CancellationToken ct)
    {
        // 1. Session-wide connection cap.
        if (_connectedPeerCount() >= _maxSessionConnections())
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // 2. Identify the target torrent.
        ITransportStream effectiveStream = stream;
        byte[]? infoHash = null;
        byte[]? preRead = null;
        bool isEncrypted = false;
        bool needsPlaintextPath;

        var policy = _encryptionMonitor.CurrentValue.InPolicy;

        if (policy != EncryptionPolicy.Disabled)
        {
            try
            {
                var mse = await MseTransportStream.CreateInboundAsync(
                    stream, _req2HashLookup, _encryptionMonitor, _mseLogger, ct).ConfigureAwait(false);
                effectiveStream = mse;
                isEncrypted = mse.IsEncrypted;
                infoHash = mse.IdentifiedInfoHash;
                preRead = null;
                needsPlaintextPath = false;
            }
            catch (MseNegotiationException)
            {
                if (policy != EncryptionPolicy.Forced)
                {
                    // Plaintext inbound still allowed; fall through to the plaintext path below.
                    needsPlaintextPath = true;
                }
                else
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        else
        {
            needsPlaintextPath = true;
        }

        if (needsPlaintextPath)
        {
            var buffer = new byte[Handshake.HandshakeLength];
            int read = 0;
            while (read < Handshake.HandshakeLength)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, Handshake.HandshakeLength - read), ct).ConfigureAwait(false);
                if (n == 0) { break; }
                read += n;
            }

            if (read < Handshake.HandshakeLength)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }

            var hs = Handshake.FromBytes(buffer);
            infoHash = hs.InfoHash;
            effectiveStream = stream;
            isEncrypted = false;
            preRead = buffer;
        }

        // 3. MSE succeeded but could not identify the info-hash.
        if (infoHash == null)
        {
            await effectiveStream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // 4. Resolve the owning torrent's PeerManager.
        var pm = _resolvePeerManager(Convert.ToHexString(infoHash));
        if (pm == null)
        {
            await effectiveStream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // 5. Route. From here, PeerManager owns disposal/error handling for the stream.
        var remote = (IPEndPoint)stream.RemoteEndPoint!;
        _ = pm.AcceptIncomingPeerAsync(effectiveStream, remote, isEncrypted, preRead, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();

            if (_acceptLoopTask != null)
            {
                try
                {
                    await _acceptLoopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected clean shutdown.
                }
            }
        }

        await _listener.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
