using System;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Bencode.Objects;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Interface for BitTorrent extension protocol plugins (BEP 10).
/// Each extension handles a specific type of extension message.
/// </summary>
public interface IExtension
{
    /// <summary>
    /// The name of this extension (e.g., "ut_pex", "ut_metadata").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Our local extension ID that we advertise to peers.
    /// </summary>
    byte LocalExtensionId { get; }

    /// <summary>
    /// The remote peer's extension ID for this extension.
    /// Null if the peer doesn't support this extension.
    /// </summary>
    byte? RemoteExtensionId { get; set; }

    /// <summary>
    /// Whether this extension is enabled and should be advertised.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Called when an extension handshake is received from the peer.
    /// Use this to extract the peer's extension ID for this extension.
    /// </summary>
    /// <param name="handshake">The parsed extension handshake dictionary.</param>
    Task OnExtensionHandshakeReceivedAsync(BDictionary handshake);

    /// <summary>
    /// Adds this extension's entries to the outgoing extension handshake.
    /// </summary>
    /// <param name="handshake">The handshake dictionary to add entries to.</param>
    void AddToHandshake(BDictionary handshake);

    /// <summary>
    /// Called periodically to allow the extension to send messages.
    /// Returns the message payload to send, or null if no message is needed.
    /// </summary>
    Task<byte[]> GenerateMessageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when an extension message for this extension is received.
    /// </summary>
    /// <param name="payload">The message payload (without extension message header).</param>
    Task OnMessageReceivedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when the peer connection is established.
    /// </summary>
    Task OnConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when the peer connection is about to be closed.
    /// </summary>
    Task OnDisconnectingAsync(CancellationToken cancellationToken = default);
}
