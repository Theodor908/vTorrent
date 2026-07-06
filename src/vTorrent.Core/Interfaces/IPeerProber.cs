using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Interfaces;

/// <summary>
/// Probes candidate peers to evaluate their quality before committing to keep them.
/// Uses a trial period to compare new peers against existing connections.
/// </summary>
public interface IPeerProber : IDisposable
{
    /// <summary>
    /// Whether probing is enabled.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Total number of peers that have been probed.
    /// </summary>
    int TotalProbed { get; }

    /// <summary>
    /// Number of probed peers that were kept.
    /// </summary>
    int TotalKept { get; }

    /// <summary>
    /// Number of probed peers that were dropped.
    /// </summary>
    int TotalDropped { get; }

    /// <summary>
    /// Success rate (TotalKept / TotalProbed).
    /// </summary>
    double SuccessRate { get; }

    /// <summary>
    /// Start the probing system.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Stop the probing system.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Add candidate peers for probing.
    /// These will be tested during trial periods.
    /// </summary>
    void AddCandidatePeers(IEnumerable<PeerInfo> peers);

    /// <summary>
    /// Enter endgame mode - more aggressive probing.
    /// </summary>
    void EnterEndgameMode();

    /// <summary>
    /// Exit endgame mode - return to normal probing.
    /// </summary>
    void ExitEndgameMode();
}
