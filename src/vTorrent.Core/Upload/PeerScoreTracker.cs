using System;
using System.Collections.Generic;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Upload;

/// <summary>
/// Download phase for adaptive weight selection.
/// </summary>
public enum DownloadPhase { Early, Mid, Late, Endgame, Seeding }

/// <summary>
/// Phase-specific signal weights. All 5 must sum to 1.0.
/// </summary>
public readonly record struct PhaseWeights(
    double Reciprocity, double Stability, double Redistribution, double Latency, double Freshness);

/// <summary>
/// Adaptive choker scoring engine. Computes a composite peer score from 5 normalized signals
/// with phase-adaptive weights. vTorrent-original algorithm.
///
/// Signals:
/// 1. Reciprocity — download rate from peer (normalized by max)
/// 2. Stability — consistency of download rate over time (CV inverse)
/// 3. Redistribution — piece count growth (is peer sharing our data?)
/// 4. Latency — inverse RTT (lower latency = higher score)
/// 5. Freshness — recency of last data received
/// </summary>
public class PeerScoreTracker
{
    private const int RingSize = 10;

    private readonly Dictionary<IPeerConnection, PeerHistory> _history = new();

    private struct PeerHistory
    {
        public double[] RateRing;
        public int RingIndex;
        public int SampleCount;
        public int PreviousPieceCount;
        public int PreviousPexPeerCount;
        public double LastScore;
    }

    // === Phase detection ===

    public static DownloadPhase DetectPhase(double completionRatio, bool isSeeding, bool isEndgame)
    {
        if (isSeeding) return DownloadPhase.Seeding;
        if (isEndgame) return DownloadPhase.Endgame;
        if (completionRatio > 0.85) return DownloadPhase.Late;
        if (completionRatio > 0.15) return DownloadPhase.Mid;
        return DownloadPhase.Early;
    }

    // === Phase weights ===

    private static readonly Dictionary<DownloadPhase, PhaseWeights> WeightTable = new()
    {
        [DownloadPhase.Early]   = new(0.5, 0.1, 0.1, 0.2, 0.1),
        [DownloadPhase.Mid]     = new(0.4, 0.2, 0.1, 0.1, 0.2),
        [DownloadPhase.Late]    = new(0.2, 0.3, 0.1, 0.2, 0.2),
        [DownloadPhase.Endgame] = new(0.1, 0.1, 0.1, 0.5, 0.2),
        [DownloadPhase.Seeding] = new(0.0, 0.2, 0.5, 0.1, 0.2),
    };

    public static PhaseWeights GetWeights(DownloadPhase phase) => WeightTable[phase];

    // === Per-peer state management ===

    public void OnPeerConnected(IPeerConnection peer)
    {
        _history[peer] = new PeerHistory
        {
            RateRing = new double[RingSize],
            RingIndex = 0,
            SampleCount = 0,
            PreviousPieceCount = 0,
            PreviousPexPeerCount = 0,
            LastScore = 0
        };
    }

    public void OnPeerDisconnected(IPeerConnection peer)
    {
        _history.Remove(peer);
    }

    /// <summary>
    /// Record a rate sample for a peer. Called each rechoke cycle (10s).
    /// </summary>
    public void RecordSample(IPeerConnection peer, double downloadRate, int pieceCount, int pexPeerCount = 0)
    {
        if (!_history.TryGetValue(peer, out var h))
            return;

        h.RateRing[h.RingIndex] = downloadRate;
        h.RingIndex = (h.RingIndex + 1) % RingSize;
        h.SampleCount = Math.Min(h.SampleCount + 1, RingSize);
        h.PreviousPieceCount = pieceCount;
        h.PreviousPexPeerCount = pexPeerCount;
        _history[peer] = h; // re-assign since it's a struct
    }

    /// <summary>
    /// Compute scores for all tracked peers.
    /// </summary>
    public Dictionary<IPeerConnection, double> ComputeScores(
        IReadOnlyList<IPeerConnection> peers,
        DownloadPhase phase,
        bool pexEnabled,
        Func<IPeerConnection, double> getDownloadRate,
        Func<IPeerConnection, int> getPieceCount,
        Func<IPeerConnection, double> getRttMs,
        Func<IPeerConnection, double> getSecsSinceLastData,
        double snubbedTimeoutSecs)
    {
        var weights = GetWeights(phase);
        var scores = new Dictionary<IPeerConnection, double>(peers.Count);

        if (peers.Count == 0) return scores;

        // Find maxes for normalization
        double maxRate = 0;
        int maxPieceDelta = 0;
        foreach (var peer in peers)
        {
            double rate = getDownloadRate(peer);
            if (rate > maxRate) maxRate = rate;

            if (_history.TryGetValue(peer, out var h))
            {
                int currentPieces = getPieceCount(peer);
                int delta = currentPieces - h.PreviousPieceCount;
                if (delta > maxPieceDelta) maxPieceDelta = delta;
            }
        }

        foreach (var peer in peers)
        {
            double rate = getDownloadRate(peer);

            // 1. Reciprocity
            double reciprocity = maxRate > 0 ? rate / maxRate : 0.0;

            // 2. Stability
            double stability = 0.5; // default
            if (_history.TryGetValue(peer, out var h) && h.SampleCount >= 2)
            {
                double sum = 0, sumSq = 0;
                int count = h.SampleCount;
                for (int i = 0; i < count; i++)
                {
                    sum += h.RateRing[i];
                    sumSq += h.RateRing[i] * h.RateRing[i];
                }
                double mean = sum / count;
                if (mean > 0)
                {
                    double variance = sumSq / count - mean * mean;
                    double stdev = Math.Sqrt(Math.Max(0, variance));
                    stability = Math.Max(0, 1.0 - stdev / mean);
                }
                // else mean == 0 -> stability stays 0.5
            }

            // 3. Redistribution
            double redistribution = 0;
            if (_history.TryGetValue(peer, out var hist))
            {
                int currentPieces = getPieceCount(peer);
                int pieceDelta = currentPieces - hist.PreviousPieceCount;
                redistribution = maxPieceDelta > 0 ? (double)pieceDelta / maxPieceDelta : 0.0;
                redistribution = Math.Max(0, redistribution); // clamp negative
            }

            // 4. Latency
            double rttMs = getRttMs(peer);
            double latency = 1.0 / (1.0 + rttMs / 100.0);

            // 5. Freshness
            double secsSince = getSecsSinceLastData(peer);
            double freshness = Math.Max(0, 1.0 - secsSince / snubbedTimeoutSecs);

            // Composite score
            double score =
                weights.Reciprocity * reciprocity +
                weights.Stability * stability +
                weights.Redistribution * redistribution +
                weights.Latency * latency +
                weights.Freshness * freshness;

            scores[peer] = score;

            // Update cached score
            if (_history.TryGetValue(peer, out var hUpdate))
            {
                hUpdate.LastScore = score;
                _history[peer] = hUpdate;
            }
        }

        return scores;
    }
}
