using System;

using System.Collections.Concurrent;

using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using vTorrent.Core.PeerCommunication.Events;

using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Download;

namespace vTorrent.Core.Engine;

public class PeerMessageRouter : IDisposable

{

    private readonly IPeerManager _peerManager;

    private readonly ILogger<PeerMessageRouter> _logger;

    private readonly ConcurrentDictionary<MessageType, ConcurrentBag<Func<IPeerConnection, PeerMessage, Task>>> _handlers = new();

    private bool _disposed;

    public PeerMessageRouter(IPeerManager peerManager, ILogger<PeerMessageRouter> logger)

    {

        _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _peerManager.MessageReceived += OnMessageReceived;

        _logger.LogDebug("PeerMessageRouter initialized");

    }

    public void RegisterHandler(MessageType type, Func<IPeerConnection, PeerMessage, Task> handler)

    {

        if (handler == null)

            throw new ArgumentNullException(nameof(handler));

        var handlers = _handlers.GetOrAdd(type, _ => new ConcurrentBag<Func<IPeerConnection, PeerMessage, Task>>());

        handlers.Add(handler);

        _logger.LogTrace("Registered handler for {MessageType}", type);

    }

    private void OnMessageReceived(object? sender, PeerMessageEventArgs e)

    {

        _ = OnMessageReceivedAsync(e);

    }

    private async Task OnMessageReceivedAsync(PeerMessageEventArgs e)

    {

        if (_disposed)

            return;

        if (!_handlers.TryGetValue(e.Message.Type, out var handlers))

        {

            _logger.LogTrace("No handler registered for message type {Type} from {Peer}",

                e.Message.Type, e.Peer.PeerInfo.EndPoint);

            return;

        }

        foreach (var handler in handlers)

        {

            try

            {

                await handler(e.Peer, e.Message).ConfigureAwait(false);

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "Error handling {MessageType} from {Peer}",

                    e.Message.Type, e.Peer.PeerInfo.EndPoint);

            }

        }

    }

    /// <summary>

    /// Subscribe to an additional message source (e.g., WebSeedManager).

    /// Routes messages through the same handler dispatch as PeerManager messages,

    /// ensuring web seeds use the identical code path as regular peers.

    /// </summary>

    public void SubscribeTo(WebSeedManager webSeedManager)

    {

        webSeedManager.MessageReceived += OnMessageReceived;

    }

    public void UnsubscribeFrom(WebSeedManager webSeedManager)

    {

        webSeedManager.MessageReceived -= OnMessageReceived;

    }

    public void Dispose()

    {

        if (_disposed)

            return;

        _disposed = true;

        _peerManager.MessageReceived -= OnMessageReceived;

        _handlers.Clear();

        _logger.LogDebug("PeerMessageRouter disposed");

    }

}