using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;
using vTorrent.Core.PeerCommunication.Extensions;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Manages metadata download for magnet link torrents.
/// Coordinates metadata piece requests across multiple peers to efficiently
/// download the torrent info dictionary.
///
/// Based on libtorrent's ut_metadata implementation patterns.
/// </summary>
public class MetadataManager : IDisposable
{
    private readonly ILogger<MetadataManager> _logger;
    private readonly ManagedTorrent _torrent;
    private readonly byte[] _infoHash;
    private readonly IOptionsMonitor<PeerSettings> _peerMonitor;

    // Metadata piece tracking
    private readonly object _lock = new();
    private byte[] _metadataBuffer;
    private int _metadataSize;
    private int _totalPieces;
    private readonly HashSet<int> _receivedPieces = new();
    private readonly ConcurrentDictionary<int, (DateTime RequestTime, string PeerId)> _pendingRequests = new();

    // Peer tracking
    private readonly ConcurrentDictionary<string, PeerMetadataState> _peers = new();

    // Configuration (based on libtorrent defaults)
    public const int MetadataPieceSize = 16384; // 16 KB per BEP 9
    public const int MaxConcurrentRequestsPerPeer = 2;
    public const int MaxTotalPendingRequests = 10;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PeerPenaltyDuration = TimeSpan.FromSeconds(30);

    // State
    private bool _isComplete;
    private bool _isDisposed;
    private CancellationTokenSource _cts;

    /// <summary>
    /// Raised when a metadata piece is received.
    /// </summary>
    public event Action<int, int> ProgressChanged; // (received, total)

    /// <summary>
    /// Raised when metadata download completes successfully.
    /// </summary>
    public event Action<byte[]> MetadataReceived;

    /// <summary>
    /// Raised when metadata download fails.
    /// </summary>
    public event Action<string> MetadataFailed;

    /// <summary>
    /// Whether metadata download is complete.
    /// </summary>
    public bool IsComplete
    {
        get { lock (_lock) return _isComplete; }
    }

    /// <summary>
    /// Current progress (0.0 to 1.0).
    /// </summary>
    public double Progress
    {
        get
        {
            lock (_lock)
            {
                if (_totalPieces == 0) return 0;
                return (double)_receivedPieces.Count / _totalPieces;
            }
        }
    }

    /// <summary>
    /// Number of peers that have metadata.
    /// </summary>
    public int PeersWithMetadata => _peers.Count(p => p.Value.HasMetadata);

    public MetadataManager(
        ILogger<MetadataManager> logger,
        ManagedTorrent torrent,
        IOptionsMonitor<PeerSettings> peerMonitor = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _torrent = torrent ?? throw new ArgumentNullException(nameof(torrent));
        _infoHash = torrent.InfoHashBytes ?? Convert.FromHexString(torrent.InfoHash);
        _peerMonitor = peerMonitor;
        _cts = new CancellationTokenSource();

        if (_infoHash.Length != 20)
            throw new ArgumentException("Info hash must be 20 bytes");
    }

    /// <summary>
    /// Registers a peer that supports ut_metadata.
    /// </summary>
    /// <param name="peerId">Unique peer identifier.</param>
    /// <param name="metadataSize">The metadata size reported by the peer (0 if unknown).</param>
    public void RegisterPeer(string peerId, int metadataSize)
    {
        if (string.IsNullOrEmpty(peerId))
            return;

        var state = _peers.GetOrAdd(peerId, _ => new PeerMetadataState());
        state.HasMetadata = metadataSize > 0;

        if (metadataSize > 0 && metadataSize <= (_peerMonitor?.CurrentValue.MaxMetadataSize ?? 31457280))
        {
            lock (_lock)
            {
                if (_metadataSize == 0)
                {
                    _metadataSize = metadataSize;
                    _totalPieces = (metadataSize + MetadataPieceSize - 1) / MetadataPieceSize;
                    _metadataBuffer = new byte[metadataSize];

                    _logger.LogDebug(
                        "Metadata size set to {Size} bytes ({Pieces} pieces) from peer {Peer}",
                        metadataSize, _totalPieces, peerId);

                    // Update torrent progress info
                    _torrent.MetadataPiecesTotal = _totalPieces;
                }
                else if (_metadataSize != metadataSize)
                {
                    _logger.LogWarning(
                        "Peer {Peer} reported different metadata size: {Reported} vs {Expected}",
                        peerId, metadataSize, _metadataSize);
                }
            }

            state.MetadataSize = metadataSize;
        }
    }

    /// <summary>
    /// Unregisters a peer.
    /// </summary>
    public void UnregisterPeer(string peerId)
    {
        if (string.IsNullOrEmpty(peerId))
            return;

        _peers.TryRemove(peerId, out _);

        // Cancel pending requests from this peer
        var toRemove = _pendingRequests
            .Where(kvp => kvp.Value.PeerId == peerId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var piece in toRemove)
        {
            _pendingRequests.TryRemove(piece, out _);
        }
    }

    /// <summary>
    /// Gets the next piece to request from a specific peer.
    /// Returns -1 if no piece should be requested.
    /// </summary>
    public int GetNextPieceToRequest(string peerId)
    {
        if (_isComplete)
            return -1;

        if (!_peers.TryGetValue(peerId, out var peerState) || !peerState.HasMetadata)
            return -1;

        // Check if peer is penalized
        if (peerState.PenalizedUntil > DateTime.UtcNow)
            return -1;

        // Check peer's request limit
        var peerPendingCount = _pendingRequests.Count(kvp => kvp.Value.PeerId == peerId);
        if (peerPendingCount >= MaxConcurrentRequestsPerPeer)
            return -1;

        // Check global pending limit
        if (_pendingRequests.Count >= MaxTotalPendingRequests)
            return -1;

        lock (_lock)
        {
            // Find next piece to request
            for (int i = 0; i < _totalPieces; i++)
            {
                if (_receivedPieces.Contains(i))
                    continue;

                if (_pendingRequests.ContainsKey(i))
                    continue;

                // Found a piece to request
                _pendingRequests[i] = (DateTime.UtcNow, peerId);
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Handles a received metadata piece.
    /// </summary>
    /// <param name="peerId">The peer that sent the piece.</param>
    /// <param name="piece">The piece index.</param>
    /// <param name="data">The piece data.</param>
    /// <param name="totalSize">The total metadata size (for validation).</param>
    public void OnPieceReceived(string peerId, int piece, ReadOnlySpan<byte> data, int totalSize)
    {
        if (_isComplete)
            return;

        lock (_lock)
        {
            // Validate piece index
            if (piece < 0 || piece >= _totalPieces)
            {
                _logger.LogWarning("Invalid metadata piece index {Piece} from {Peer}", piece, peerId);
                return;
            }

            // Validate total size
            if (totalSize != _metadataSize)
            {
                _logger.LogWarning(
                    "Metadata size mismatch from {Peer}: expected {Expected}, got {Got}",
                    peerId, _metadataSize, totalSize);
                return;
            }

            // Already have this piece?
            if (_receivedPieces.Contains(piece))
            {
                _pendingRequests.TryRemove(piece, out _);
                return;
            }

            // Validate piece size
            var pieceStart = piece * MetadataPieceSize;
            var expectedSize = Math.Min(MetadataPieceSize, _metadataSize - pieceStart);

            if (data.Length != expectedSize)
            {
                _logger.LogWarning(
                    "Metadata piece {Piece} size mismatch from {Peer}: expected {Expected}, got {Got}",
                    piece, peerId, expectedSize, data.Length);
                PenalizePeer(peerId, "piece size mismatch");
                return;
            }

            // Copy data to buffer
            data.CopyTo(_metadataBuffer.AsSpan(pieceStart));
            _receivedPieces.Add(piece);
            _pendingRequests.TryRemove(piece, out _);

            _logger.LogDebug(
                "Received metadata piece {Piece}/{Total} from {Peer}",
                piece + 1, _totalPieces, peerId);

            // Update torrent progress
            _torrent.MetadataPiecesReceived = _receivedPieces.Count;
            _torrent.MetadataProgress = (double)_receivedPieces.Count / _totalPieces;

            // Notify progress
            ProgressChanged?.Invoke(_receivedPieces.Count, _totalPieces);

            // Check if complete
            if (_receivedPieces.Count >= _totalPieces)
            {
                ValidateAndComplete(peerId);
            }
        }
    }

    /// <summary>
    /// Handles a reject message for a metadata piece.
    /// </summary>
    public void OnPieceRejected(string peerId, int piece)
    {
        _pendingRequests.TryRemove(piece, out _);

        if (_peers.TryGetValue(peerId, out var state))
        {
            state.RejectCount++;
            if (state.RejectCount >= 3)
            {
                state.HasMetadata = false;
                _logger.LogDebug("Peer {Peer} rejected too many requests, marking as no metadata", peerId);
            }
        }
    }

    /// <summary>
    /// Cleans up timed out requests.
    /// </summary>
    public void CleanupTimedOutRequests()
    {
        var now = DateTime.UtcNow;
        var timedOut = _pendingRequests
            .Where(kvp => now - kvp.Value.RequestTime > RequestTimeout)
            .ToList();

        foreach (var (piece, (_, peerId)) in timedOut)
        {
            _pendingRequests.TryRemove(piece, out _);
            _logger.LogDebug("Metadata request for piece {Piece} timed out (peer: {Peer})", piece, peerId);
        }
    }

    private void ValidateAndComplete(string sourcePeer)
    {
        // Validate against info hash
        var hash = SHA1.HashData(_metadataBuffer);

        if (!hash.AsSpan().SequenceEqual(_infoHash))
        {
            _logger.LogWarning("Metadata validation failed: hash mismatch");
            _logger.LogDebug("Expected: {Expected}", Convert.ToHexString(_infoHash));
            _logger.LogDebug("Got: {Got}", Convert.ToHexString(hash));

            // Penalize the source peer more heavily
            PenalizePeer(sourcePeer, "hash mismatch", TimeSpan.FromMinutes(5));

            // Reset and try again
            Reset();
            MetadataFailed?.Invoke("Hash mismatch");
            return;
        }

        _logger.LogDebug("Metadata validation successful ({Size} bytes)", _metadataBuffer.Length);
        _isComplete = true;

        // Make a copy to pass to callback
        var metadata = new byte[_metadataBuffer.Length];
        Array.Copy(_metadataBuffer, metadata, metadata.Length);

        // Try to set metadata on the torrent
        if (_torrent.SetMetadata(metadata))
        {
            MetadataReceived?.Invoke(metadata);
        }
        else
        {
            _logger.LogError("Failed to set metadata on torrent");
            MetadataFailed?.Invoke("Failed to parse metadata");
        }
    }

    private void PenalizePeer(string peerId, string reason, TimeSpan? duration = null)
    {
        if (_peers.TryGetValue(peerId, out var state))
        {
            state.PenalizedUntil = DateTime.UtcNow + (duration ?? PeerPenaltyDuration);
            state.PenaltyCount++;
            _logger.LogDebug("Penalized peer {Peer} for {Reason}", peerId, reason);
        }
    }

    /// <summary>
    /// Resets the metadata download state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _receivedPieces.Clear();
            _pendingRequests.Clear();
            _isComplete = false;

            if (_metadataSize > 0)
            {
                _metadataBuffer = new byte[_metadataSize];
            }

            _torrent.MetadataPiecesReceived = 0;
            _torrent.MetadataProgress = 0;
        }
    }

    /// <summary>
    /// Gets the raw metadata buffer (only valid after IsComplete is true).
    /// </summary>
    public byte[] GetMetadataBuffer()
    {
        lock (_lock)
        {
            if (!_isComplete || _metadataBuffer == null)
                return null;

            var copy = new byte[_metadataBuffer.Length];
            Array.Copy(_metadataBuffer, copy, copy.Length);
            return copy;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _peers.Clear();
        _pendingRequests.Clear();
    }

    /// <summary>
    /// Tracks state for each peer.
    /// </summary>
    private class PeerMetadataState
    {
        public bool HasMetadata { get; set; }
        public int MetadataSize { get; set; }
        public int RequestCount { get; set; }
        public int RejectCount { get; set; }
        public int PenaltyCount { get; set; }
        public DateTime PenalizedUntil { get; set; }
        public DateTime LastRequest { get; set; }
    }
}
