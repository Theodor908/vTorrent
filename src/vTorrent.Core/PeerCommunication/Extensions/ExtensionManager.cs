using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Interfaces;
using vTorrent.Abstractions.Settings;
using vTorrent.Bencode.Objects;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Manages BEP 10 extensions for a peer connection.
/// Handles extension handshakes and message routing.
/// </summary>
public class ExtensionManager
{
    private readonly ILogger<ExtensionManager> _logger;
    private readonly IOptionsMonitor<PrivacySettings> _privacyMonitor;
    private readonly Dictionary<string, IExtension> _extensionsByName = new();
    private readonly Dictionary<byte, IExtension> _extensionsByLocalId = new();
    private readonly Dictionary<byte, IExtension> _extensionsByRemoteId = new();

    private readonly string _clientVersion;
    private readonly int _listenPort;
    private readonly IExternalIpVoter? _externalIpVoter;

    private bool _handshakeSent;
    private bool _handshakeReceived;
    private Timer _tickTimer;
    private readonly Func<PeerMessage, CancellationToken, Task> _sendMessage;

    /// <summary>
    /// Called when the extension handshake is complete and we know peer's capabilities.
    /// </summary>
    public event EventHandler<ExtensionHandshake> HandshakeReceived;

    /// <summary>
    /// Whether the peer supports the extension protocol.
    /// </summary>
    public bool PeerSupportsExtensions { get; private set; }

    /// <summary>
    /// The received extension handshake from the peer.
    /// </summary>
    public ExtensionHandshake PeerHandshake { get; private set; }

    public ExtensionManager(
        ILogger<ExtensionManager> logger,
        string clientVersion,
        int listenPort,
        Func<PeerMessage, CancellationToken, Task> sendMessage,
        IExternalIpVoter? externalIpVoter = null,
        IOptionsMonitor<PrivacySettings>? privacyMonitor = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clientVersion = clientVersion ?? "vTorrent/1.0";
        _listenPort = listenPort;
        _sendMessage = sendMessage ?? throw new ArgumentNullException(nameof(sendMessage));
        _externalIpVoter = externalIpVoter;
        _privacyMonitor = privacyMonitor;
    }

    /// <summary>Returns the effective client version, respecting anonymous mode.</summary>
    internal string GetEffectiveClientVersion()
    {
        if (_privacyMonitor?.CurrentValue?.AnonymousMode == true)
            return "";
        return _clientVersion;
    }

    /// <summary>
    /// Registers an extension with this manager.
    /// </summary>
    public void RegisterExtension(IExtension extension)
    {
        if (extension == null)
            throw new ArgumentNullException(nameof(extension));

        if (_extensionsByName.ContainsKey(extension.Name))
        {
            _logger.LogWarning("Extension {Name} already registered, replacing", extension.Name);
            var existing = _extensionsByName[extension.Name];
            _extensionsByLocalId.Remove(existing.LocalExtensionId);
        }

        _extensionsByName[extension.Name] = extension;
        _extensionsByLocalId[extension.LocalExtensionId] = extension;

        _logger.LogDebug("Registered extension {Name} with local ID {Id}", extension.Name, extension.LocalExtensionId);
    }

    /// <summary>
    /// Gets a registered extension by name.
    /// </summary>
    public IExtension GetExtension(string name)
    {
        return _extensionsByName.TryGetValue(name, out var ext) ? ext : null;
    }

    /// <summary>
    /// Called when the peer supports extension protocol (from handshake reserved bits).
    /// </summary>
    public void SetPeerSupportsExtensions(bool supports)
    {
        PeerSupportsExtensions = supports;
        _logger.LogDebug("Peer supports extensions: {Supports}", supports);
    }

    /// <summary>
    /// Sends our extension handshake to the peer.
    /// </summary>
    public async Task SendExtensionHandshakeAsync(CancellationToken cancellationToken = default)
    {
        if (!PeerSupportsExtensions)
        {
            _logger.LogDebug("Peer doesn't support extensions, skipping handshake");
            return;
        }

        if (_handshakeSent)
        {
            _logger.LogDebug("Extension handshake already sent");
            return;
        }

        var handshake = new ExtensionHandshake
        {
            ClientVersion = GetEffectiveClientVersion(),
            ListenPort = _listenPort,
            RequestQueueSize = 250
        };

        // Add all enabled extensions
        foreach (var ext in _extensionsByName.Values.Where(e => e.IsEnabled))
        {
            handshake.SupportedExtensions[ext.Name] = ext.LocalExtensionId;
        }

        var handshakeData = handshake.Encode();
        var message = PeerMessage.CreateExtendedHandshake(handshakeData);

        await _sendMessage(message, cancellationToken);
        _handshakeSent = true;

        _logger.LogDebug("Sent extension handshake with extensions: {Extensions}",
            string.Join(", ", handshake.SupportedExtensions.Keys));
    }

    /// <summary>
    /// Handles an incoming Extended message.
    /// </summary>
    public async Task HandleExtendedMessageAsync(PeerMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Type != MessageType.Extended)
            return;

        var (extensionId, data) = message.ParseExtended();

        if (extensionId == ExtensionHandshake.HandshakeExtensionId)
        {
            // This is the extension handshake
            await HandleExtensionHandshakeAsync(data, cancellationToken);
        }
        else
        {
            // Route to the appropriate extension
            await RouteExtensionMessageAsync(extensionId, data, cancellationToken);
        }
    }

    private async Task HandleExtensionHandshakeAsync(byte[] data, CancellationToken cancellationToken)
    {
        try
        {
            var handshake = ExtensionHandshake.Parse(data);
            PeerHandshake = handshake;
            _handshakeReceived = true;

            // BEP 24/10: Feed peer's yourip into external IP voter
            if (_externalIpVoter != null)
            {
                var yourIp = handshake.GetYourIpAddress();
                if (yourIp != null)
                    _externalIpVoter.AddVote(yourIp, "peer_extension");
            }

            _logger.LogDebug("Received extension handshake: {Handshake}", handshake);

            // Update remote extension IDs for our extensions
            _extensionsByRemoteId.Clear();

            foreach (var ext in _extensionsByName.Values)
            {
                var remoteId = handshake.GetExtensionId(ext.Name);
                ext.RemoteExtensionId = remoteId;

                if (remoteId.HasValue)
                {
                    _extensionsByRemoteId[remoteId.Value] = ext;
                    _logger.LogDebug("Peer supports {Extension} with remote ID {Id}", ext.Name, remoteId.Value);
                }
            }

            // Notify extensions
            var dict = handshake.RawDictionary ?? new BDictionary();
            foreach (var ext in _extensionsByName.Values)
            {
                await ext.OnExtensionHandshakeReceivedAsync(dict);
            }

            // Start periodic tick for extensions
            StartTickTimer();

            HandshakeReceived?.Invoke(this, handshake);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error parsing extension handshake");
        }
    }

    private async Task RouteExtensionMessageAsync(byte extensionId, byte[] data, CancellationToken cancellationToken)
    {
        // Find extension by our local ID (the peer sends to our advertised ID)
        if (!_extensionsByLocalId.TryGetValue(extensionId, out var extension))
        {
            _logger.LogDebug("Received extension message for unknown extension ID {Id}", extensionId);
            return;
        }

        _logger.LogDebug("Routing extension message to {Name} (ID {Id}, {Bytes} bytes)",
            extension.Name, extensionId, data.Length);

        try
        {
            await extension.OnMessageReceivedAsync(data, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error handling extension message for {Name}", extension.Name);
        }
    }

    /// <summary>
    /// Starts the periodic tick timer for extensions.
    /// </summary>
    private void StartTickTimer()
    {
        _tickTimer?.Dispose();
        _tickTimer = new Timer(async _ => await TickAsync(), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Periodic tick to allow extensions to send messages.
    /// </summary>
    private async Task TickAsync()
    {
        if (!_handshakeReceived)
            return;

        foreach (var ext in _extensionsByName.Values.Where(e => e.IsEnabled && e.RemoteExtensionId.HasValue))
        {
            try
            {
                var data = await ext.GenerateMessageAsync();
                if (data != null && data.Length > 0)
                {
                    var message = PeerMessage.CreateExtended(ext.RemoteExtensionId.Value, data);
                    await _sendMessage(message, CancellationToken.None);

                    _logger.LogDebug("Sent {Name} message ({Bytes} bytes)", ext.Name, data.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in extension tick for {Name}", ext.Name);
            }
        }
    }

    /// <summary>
    /// Notifies all extensions that the connection is established.
    /// </summary>
    public async Task NotifyConnectedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var ext in _extensionsByName.Values)
        {
            try
            {
                await ext.OnConnectedAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error notifying {Name} of connection", ext.Name);
            }
        }
    }

    /// <summary>
    /// Notifies all extensions that the connection is being closed.
    /// </summary>
    public async Task NotifyDisconnectingAsync(CancellationToken cancellationToken = default)
    {
        _tickTimer?.Dispose();
        _tickTimer = null;

        foreach (var ext in _extensionsByName.Values)
        {
            try
            {
                await ext.OnDisconnectingAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error notifying {Name} of disconnection", ext.Name);
            }
        }
    }

    /// <summary>
    /// Checks if the peer supports a specific extension.
    /// </summary>
    public bool PeerSupportsExtension(string extensionName)
    {
        return _extensionsByName.TryGetValue(extensionName, out var ext) && ext.RemoteExtensionId.HasValue;
    }

    public void Dispose()
    {
        _tickTimer?.Dispose();
    }
}
