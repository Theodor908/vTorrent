using System.Collections.Generic;
using System.Net;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Interfaces;

/// <summary>
/// Calculates globally-agreed connection priority between peers.
/// Both peers independently calculate the same priority for their connection,
/// enabling consistent peer selection across the swarm.
///
/// Based on the libtorrent/uTorrent proposal for improved swarm connectivity:
/// https://blog.libtorrent.org/2012/12/swarm-connectivity/
/// </summary>
public interface IPeerPriorityCalculator
{
    /// <summary>
    /// Calculates a globally-agreed priority for a connection between two endpoints.
    /// Must be commutative: CalculatePriority(A, B) == CalculatePriority(B, A)
    /// </summary>
    /// <param name="local">Local endpoint (our side)</param>
    /// <param name="remote">Remote endpoint (peer's side)</param>
    /// <returns>Priority value - higher means higher priority</returns>
    uint CalculatePriority(IPEndPoint local, IPEndPoint remote);

    /// <summary>
    /// Compares two peers by their connection priority.
    /// </summary>
    /// <param name="a">First peer</param>
    /// <param name="b">Second peer</param>
    /// <param name="localEndpoint">Our local endpoint</param>
    /// <returns>Negative if a &lt; b, positive if a &gt; b, zero if equal</returns>
    int Compare(IPeerConnection a, IPeerConnection b, IPEndPoint localEndpoint);

    /// <summary>
    /// Finds the peer with the lowest priority (best candidate for disconnection).
    /// </summary>
    IPeerConnection FindLowestPriority(IEnumerable<IPeerConnection> peers, IPEndPoint localEndpoint);

    /// <summary>
    /// Finds the peer with the highest priority.
    /// </summary>
    IPeerConnection FindHighestPriority(IEnumerable<IPeerConnection> peers, IPEndPoint localEndpoint);
}
