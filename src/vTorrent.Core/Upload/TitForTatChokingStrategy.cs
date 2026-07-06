using System;

using System.Collections.Generic;

using System.Linq;

using Microsoft.Extensions.Logging;

using vTorrent.Core.Interfaces;

using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Engine;

namespace vTorrent.Core.Upload;

/// <summary>

/// Implements the standard BitTorrent Tit-for-Tat choking algorithm.

///

/// Key concepts:

/// - Every 15 seconds, evaluate all peers

/// - Sort interested peers by download rate (or upload rate if seeding)

/// - Unchoke the top N peers (regular slots)

/// - Maintain 1 optimistic slot that rotates every 30 seconds

/// - Optimistic slot allows new peers to prove themselves

///

/// Anti-snubbing integration:

/// - Peers detected as snubbing are excluded from regular slot selection

/// - They can still be selected for optimistic slot (to give them another chance)

///

/// Fast peer optimization:

/// - Peers with very high download rates can bypass the cycle

/// - Prevents missing fast peers due to timing

/// </summary>

public class TitForTatChokingStrategy : IChokingStrategy

{

    private readonly ISnubbingDetector _snubbingDetector;

    private readonly ILogger<TitForTatChokingStrategy> _logger;

    private readonly Random _random = new();

    // Configuration matching qBittorrent defaults

    public TimeSpan RechokingInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan OptimisticRotationInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int TotalUploadSlots { get; set; } = 4;  // qBittorrent default: 4 per torrent

    public int OptimisticSlots { get; } = 1;

    // Fast peer threshold (768 KB/s)

    private const double FastPeerThreshold = 768 * 1024;

    private const int MaxExtraSlots = 2; // Allow exceeding limit for fast peers

    public TitForTatChokingStrategy(

        ISnubbingDetector snubbingDetector = null,

        ILogger<TitForTatChokingStrategy> logger = null)

    {

        _snubbingDetector = snubbingDetector;

        _logger = logger;

    }

    /// <summary>

    /// Select peers for regular (rate-based) unchoke slots.

    ///

    /// When leeching: sort by download rate (reward peers who send us data)

    /// When seeding: sort by upload rate (reward peers who take our data efficiently)

    /// </summary>

    public IReadOnlyList<IPeerConnection> SelectRegularUnchokes(

        IEnumerable<IPeerConnection> candidates,

        IStatisticsTracker statistics,

        bool isSeeding)

    {

        if (candidates == null || statistics == null)

            return new List<IPeerConnection>();

        var eligible = candidates.Where(p => p?.IsConnected == true).ToList();

        // Filter out snubbing peers (if detector available)

        // Snubbing peers are unchoked but not sending - don't waste slots on them

        if (_snubbingDetector != null)

        {

            var snubbing = new HashSet<IPeerConnection>(_snubbingDetector.GetSnubbingPeers(eligible));

            int snubbingCount = snubbing.Count;

            if (snubbingCount > 0)

            {

                _logger?.LogDebug("Excluding {Count} snubbing peers from regular selection", snubbingCount);

                eligible = eligible.Where(p => !snubbing.Contains(p)).ToList();

            }

        }

        // Sort by rate

        // When leeching: reward peers who upload to us (their download = our download)

        // When seeding: reward peers who download from us quickly (efficient receivers)

        IOrderedEnumerable<IPeerConnection> sorted;

        if (isSeeding)

        {

            sorted = eligible.OrderByDescending(p => statistics.GetPeerUploadRate(p));

        }

        else

        {

            sorted = eligible.OrderByDescending(p => statistics.GetPeerDownloadRate(p));

        }

        var selected = sorted.Take(RegularSlots).ToList();

        if (_logger?.IsEnabled(LogLevel.Trace) == true)

        {

            foreach (var peer in selected)

            {

                var rate = isSeeding

                    ? statistics.GetPeerUploadRate(peer)

                    : statistics.GetPeerDownloadRate(peer);

                _logger.LogTrace("Selected {Peer} for regular slot (rate: {Rate})",

                    peer.PeerInfo?.EndPoint, TorrentUtilities.FormatRate(rate));

            }

        }

        return selected;

    }

    /// <summary>

    /// Select a peer for optimistic unchoke.

    ///

    /// The optimistic slot gives new peers a chance to prove themselves.

    /// Without it, new peers with no pieces could never start downloading.

    /// </summary>

    public IPeerConnection SelectOptimisticUnchoke(

        IEnumerable<IPeerConnection> candidates,

        IPeerConnection currentOptimistic)

    {

        if (candidates == null)

            return null;

        var list = candidates.Where(p => p?.IsConnected == true).ToList();

        if (list.Count == 0)

            return null;

        // Random selection for exploration

        // This is important: optimistic unchoking explores the peer space

        // Random selection ensures we don't get stuck with suboptimal peers

        var selected = list[_random.Next(list.Count)];

        _logger?.LogDebug("Selected {Peer} for optimistic slot (from {Count} candidates)",

            selected.PeerInfo?.EndPoint, list.Count);

        return selected;

    }

    /// <summary>

    /// Determines if a peer should be immediately unchoked.

    /// Fast peers bypass the normal cycle to ensure we don't miss them.

    /// </summary>

    public bool ShouldImmediatelyUnchoke(IPeerConnection peer, double downloadRate, int currentUnchokedCount)

    {

        if (peer == null)

            return false;

        // Only consider if peer is sending us data at a high rate

        if (downloadRate < FastPeerThreshold)

            return false;

        // Allow some slots beyond the limit for fast peers

        // This prevents missing them due to timing

        if (currentUnchokedCount >= TotalUploadSlots + MaxExtraSlots)

            return false;

        _logger?.LogInformation("Fast peer {Peer} ({Rate}) - immediate unchoke",

            peer.PeerInfo?.EndPoint, TorrentUtilities.FormatRate(downloadRate));

        return true;

    }

    private int RegularSlots => TotalUploadSlots - OptimisticSlots;

}
