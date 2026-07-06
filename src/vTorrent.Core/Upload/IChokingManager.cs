using System;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Upload;

/// <summary>
/// Interface for managing peer choking/unchoking decisions.
/// Implements the BitTorrent tit-for-tat algorithm.
/// </summary>
public interface IChokingManager
{
    /// <summary>
    /// Fired at the end of each rechoke cycle. UploadCoordinator subscribes to
    /// trigger send-buffer watermark recalculation after slot changes.
    /// </summary>
    event Action? RechokeCycleCompleted;
    /// <summary>
    /// Gets whether a specific peer is unchoked.
    /// </summary>
    bool IsPeerUnchoked(IPeerConnection peer);

    /// <summary>
    /// Gets the number of unchoked peers.
    /// </summary>
    int UnchokedPeerCount { get; }

    /// <summary>
    /// Total bytes uploaded to all peers.
    /// </summary>
    long TotalUploaded { get; }

    /// <summary>
    /// Total bytes downloaded from all peers.
    /// </summary>
    long TotalDownloaded { get; }

    /// <summary>
    /// Handle peer interested message.
    /// </summary>
    Task HandlePeerInterestedAsync(IPeerConnection peer, PeerMessage message);

    /// <summary>
    /// Handle peer not interested message.
    /// </summary>
    Task HandlePeerNotInterestedAsync(IPeerConnection peer, PeerMessage message);

    /// <summary>
    /// Notify that a local piece was completed (may trigger optimistic unchoke).
    /// </summary>
    void OnLocalPieceCompleted(int pieceIndex);

    /// <summary>
    /// Start the choking manager.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the choking manager.
    /// </summary>
    Task StopAsync();
}
