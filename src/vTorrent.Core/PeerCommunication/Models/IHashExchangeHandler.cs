using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>
/// Handles BEP 52 hash exchange messages (types 21-23).
/// Injected into PeerConnection for v2/hybrid torrents.
/// </summary>
public interface IHashExchangeHandler
{
    Task OnHashRequestAsync(IPeerConnection peer, HashRequestMessage msg, CancellationToken ct);
    Task OnHashesReceivedAsync(IPeerConnection peer, HashesMessage msg, CancellationToken ct);
    Task OnHashRejectAsync(IPeerConnection peer, HashRejectMessage msg, CancellationToken ct);
}
