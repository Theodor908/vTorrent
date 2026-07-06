using System;
using System.Threading.Tasks;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Interfaces;

/// <summary>
/// Routes incoming peer messages to registered handlers.
/// Enables loose coupling between message reception and processing.
/// </summary>
public interface IPeerMessageRouter : IDisposable
{
    /// <summary>
    /// Register a handler for a specific message type.
    /// Multiple handlers can be registered for the same type.
    /// </summary>
    /// <param name="type">Message type to handle</param>
    /// <param name="handler">Async handler function</param>
    void RegisterHandler(MessageType type, Func<IPeerConnection, PeerMessage, Task> handler);

    /// <summary>
    /// Unregister a previously registered handler.
    /// </summary>
    /// <param name="type">Message type</param>
    /// <param name="handler">Handler to remove</param>
    void UnregisterHandler(MessageType type, Func<IPeerConnection, PeerMessage, Task> handler);

    /// <summary>
    /// Dispatch a message to all registered handlers for its type.
    /// </summary>
    /// <param name="peer">Peer that sent the message</param>
    /// <param name="message">The message to dispatch</param>
    Task DispatchAsync(IPeerConnection peer, PeerMessage message);

    /// <summary>
    /// Check if any handlers are registered for a message type.
    /// </summary>
    bool HasHandlers(MessageType type);

    /// <summary>
    /// Get count of registered handlers for a message type.
    /// </summary>
    int GetHandlerCount(MessageType type);
}
