using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Network.I2P;

namespace vTorrent.Core.PeerCommunication.Transport.I2P;

/// <summary>
/// ITransportListener that accepts inbound I2P connections via SAM STREAM ACCEPT.
/// </summary>
public sealed class I2pTransportListener : ITransportListener
{
    private readonly I2pSamSession _session;
    private readonly Channel<ITransportStream> _acceptQueue =
        Channel.CreateBounded<ITransportStream>(64);
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public I2pTransportListener(I2pSamSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task StartAsync(EndPoint bindEndpoint, CancellationToken ct = default)
    {
        // bindEndpoint is ignored for I2P — the SAM session handles listening
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task<ITransportStream> AcceptAsync(CancellationToken ct = default)
    {
        return await _acceptQueue.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Each STREAM ACCEPT needs its own SAM connection
                var acceptClient = new I2pSamClient(_session.SamHostname, _session.SamPort);
                await acceptClient.HandshakeAsync(ct).ConfigureAwait(false);

                var peerDestBase64 = await acceptClient.StreamAcceptAsync(
                    _session.SessionId!, ct).ConfigureAwait(false);

                var peerDest = I2pDestination.FromBase64(peerDestBase64);
                var networkStream = acceptClient.RawStream
                    ?? throw new I2pSamException("No network stream after STREAM ACCEPT");

                var transport = new I2pTransportStream(networkStream, peerDest);
                await _acceptQueue.Writer.WriteAsync(transport, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                // Log and retry after brief delay
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_acceptTask != null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch { /* expected */ }
        }
        _cts?.Dispose();
    }
}
