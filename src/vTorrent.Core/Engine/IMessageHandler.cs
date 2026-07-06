using vTorrent.Core;

namespace vTorrent.Core.Engine;

/// <summary>
/// Interface for components that handle peer messages.
/// Implementers can register their message handlers with the message router.
/// </summary>
public interface IMessageHandler
{
    /// <summary>
    /// Registers message handlers with the provided message router.
    /// </summary>
    /// <param name="router">The message router to register handlers with.</param>
    void RegisterHandlers(PeerMessageRouter router);
}
