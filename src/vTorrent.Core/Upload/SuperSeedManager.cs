using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Upload;

/// <summary>
/// Per-peer state tracked by SuperSeedManager.
/// Tracks the two piece slots assigned to this peer and whether each has been propagated
/// (i.e., another peer has reported having that piece via HAVE).
/// </summary>
internal sealed class SuperSeedPeerState
{
    /// <summary>Primary piece assigned to this peer (-1 = none).</summary>
    public int Piece0 { get; set; } = -1;

    /// <summary>Secondary piece assigned to this peer (-1 = none).</summary>
    public int Piece1 { get; set; } = -1;

    /// <summary>Whether Piece0 has been propagated (another peer reported HAVE for it).</summary>
    public bool Piece0Propagated { get; set; }

    /// <summary>Whether Piece1 has been propagated (another peer reported HAVE for it).</summary>
    public bool Piece1Propagated { get; set; }
}

/// <summary>
/// Implements BEP 16 Super Seeding (Initial Seeding) logic.
///
/// In super-seeding mode the local client hides its complete bitfield and instead
/// advertises each piece to exactly one peer at a time. A piece slot is considered
/// "propagated" once at least one other peer reports having it via a HAVE message,
/// at which point a new piece is revealed to that peer. This maximises the spread
/// of unique pieces in the swarm rather than letting many peers download the same
/// popular piece.
///
/// Each peer is given two simultaneous piece slots so the pipeline stays full.
/// Rarest-first selection ensures the scarcest pieces are distributed first.
///
/// Reference: http://bittorrent.org/beps/bep_0016.html
/// </summary>
public sealed class SuperSeedManager
{
    private readonly int _totalPieces;
    private readonly IPeerManager _peerManager;
    private readonly ILogger<SuperSeedManager> _logger;

    private volatile bool _enabled;

    /// <summary>
    /// Per-peer state keyed by peer connection reference.
    /// </summary>
    private readonly ConcurrentDictionary<IPeerConnection, SuperSeedPeerState> _peerStates = new();

    /// <summary>
    /// How many peers currently have each piece assigned to them (via super-seed slots).
    /// Indexed by piece index. Access via Interlocked.
    /// </summary>
    private readonly int[] _superSeedCount;

    public bool IsEnabled => _enabled;

    public SuperSeedManager(int totalPieces, IPeerManager peerManager, ILogger<SuperSeedManager> logger)
    {
        _totalPieces = totalPieces;
        _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _superSeedCount = new int[totalPieces];
    }

    /// <summary>
    /// Selects the next piece to super-seed to <paramref name="peer"/>.
    /// Uses rarest-first among pieces not already assigned to this peer.
    /// Returns -1 if no suitable piece is found.
    /// </summary>
    /// <param name="peer">The peer to assign a piece to.</param>
    /// <param name="peerBitfield">The peer's known bitfield (null = peer has nothing).</param>
    public int GetPieceToSuperSeed(IPeerConnection peer, byte[]? peerBitfield)
    {
        if (!_enabled) return -1;

        var state = _peerStates.GetOrAdd(peer, _ => new SuperSeedPeerState());

        // Collect candidate pieces: not already owned by the peer, not already in a slot for this peer.
        int bestPiece = -1;
        int bestCount = int.MaxValue;

        // Use random tie-breaking: pick a random start offset to avoid always favouring lower indices.
        int startOffset = Random.Shared.Next(_totalPieces);

        for (int i = 0; i < _totalPieces; i++)
        {
            int pieceIndex = (startOffset + i) % _totalPieces;

            // Skip if peer already has this piece.
            if (peerBitfield != null && HasPieceInBitfield(peerBitfield, pieceIndex))
                continue;

            // Skip if already assigned to a slot for this peer.
            if (state.Piece0 == pieceIndex || state.Piece1 == pieceIndex)
                continue;

            int count = Volatile.Read(ref _superSeedCount[pieceIndex]);

            if (count < bestCount)
            {
                bestCount = count;
                bestPiece = pieceIndex;
                // If count is 0 this is already the rarest possible — no need to scan further.
                if (count == 0) break;
            }
        }

        if (bestPiece < 0)
            return -1;

        // Assign to the appropriate slot.
        if (state.Piece0 < 0)
        {
            state.Piece0 = bestPiece;
            state.Piece0Propagated = false;
        }
        else
        {
            state.Piece1 = bestPiece;
            state.Piece1Propagated = false;
        }

        Interlocked.Increment(ref _superSeedCount[bestPiece]);

        _logger.LogDebug("SuperSeed: assigned piece {Piece} (count={Count}) to {Peer}",
            bestPiece, bestCount + 1, peer.PeerInfo?.EndPoint);

        return bestPiece;
    }

    /// <summary>
    /// Called when a HAVE message is received from <paramref name="fromPeer"/> for
    /// <paramref name="pieceIndex"/>. Marks any matching super-seed slots as propagated
    /// and returns a list of (peer, newPiece) pairs for pieces that should now be revealed
    /// to peers whose slots were just freed by propagation.
    /// </summary>
    public List<(IPeerConnection Peer, int NewPiece)>? OnHaveReceived(IPeerConnection fromPeer, int pieceIndex)
    {
        if (!_enabled) return null;

        List<(IPeerConnection, int)>? reveals = null;

        foreach (var kvp in _peerStates)
        {
            var peer = kvp.Key;
            var state = kvp.Value;

            // Don't count the peer that sent the HAVE — they already have the piece.
            if (ReferenceEquals(peer, fromPeer)) continue;

            bool gotNew = false;

            if (state.Piece0 == pieceIndex && !state.Piece0Propagated)
            {
                state.Piece0Propagated = true;
                gotNew = true;
                _logger.LogDebug("SuperSeed: piece {Piece} propagated (slot0 on {Peer})",
                    pieceIndex, peer.PeerInfo?.EndPoint);
            }
            else if (state.Piece1 == pieceIndex && !state.Piece1Propagated)
            {
                state.Piece1Propagated = true;
                gotNew = true;
                _logger.LogDebug("SuperSeed: piece {Piece} propagated (slot1 on {Peer})",
                    pieceIndex, peer.PeerInfo?.EndPoint);
            }

            if (gotNew)
            {
                // Determine the peer's effective bitfield for selection.
                byte[]? bf = peer.PeerBitfield;
                int newPiece = GetPieceToSuperSeed(peer, bf);
                if (newPiece >= 0)
                {
                    reveals ??= new List<(IPeerConnection, int)>();
                    reveals.Add((peer, newPiece));
                }
            }
        }

        return reveals;
    }

    /// <summary>
    /// Wraps the original bitfield provider. When super-seeding is enabled the wrapper
    /// returns null so that the peer manager sends HAVE_NONE instead of our full bitfield.
    /// </summary>
    public Func<byte[]?> CreateBitfieldProvider(Func<byte[]?> originalProvider)
    {
        return () => _enabled ? null : originalProvider();
    }

    /// <summary>
    /// Activates super-seeding mode.
    /// </summary>
    public void Enable()
    {
        _enabled = true;
        _logger.LogDebug("SuperSeedManager enabled ({Pieces} pieces)", _totalPieces);
    }

    /// <summary>
    /// Deactivates super-seeding mode. Sends HAVE messages to each connected peer for
    /// any pieces they were not shown (i.e., pieces missing from the super-seed slots
    /// we announced), then clears all per-peer state.
    /// </summary>
    public async Task DisableAsync()
    {
        _enabled = false;

        _logger.LogDebug("SuperSeedManager disabling — sending catch-up HAVEs to all peers");

        var peers = _peerManager.ConnectedPeers;

        foreach (var peer in peers)
        {
            if (!peer.IsConnected) continue;

            _peerStates.TryGetValue(peer, out var state);

            for (int pieceIndex = 0; pieceIndex < _totalPieces; pieceIndex++)
            {
                // Only send HAVE for pieces the peer wasn't explicitly shown.
                bool wasShown = (state != null) &&
                                (state.Piece0 == pieceIndex || state.Piece1 == pieceIndex);

                if (!wasShown)
                {
                    try
                    {
                        await peer.AnnounceHaveAsync(pieceIndex).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "SuperSeedManager: failed to send HAVE {Piece} to {Peer}",
                            pieceIndex, peer.PeerInfo?.EndPoint);
                    }
                }
            }
        }

        // Clear all state.
        _peerStates.Clear();
        Array.Clear(_superSeedCount, 0, _superSeedCount.Length);

        _logger.LogDebug("SuperSeedManager disabled");
    }

    /// <summary>
    /// Called when a peer disconnects. Cleans up state and decrements piece counts.
    /// </summary>
    public void OnPeerDisconnected(IPeerConnection peer)
    {
        if (!_peerStates.TryRemove(peer, out var state))
            return;

        if (state.Piece0 >= 0)
            Interlocked.Decrement(ref _superSeedCount[state.Piece0]);

        if (state.Piece1 >= 0)
            Interlocked.Decrement(ref _superSeedCount[state.Piece1]);

        _logger.LogDebug("SuperSeedManager: cleaned up state for disconnected peer {Peer}",
            peer.PeerInfo?.EndPoint);
    }

    /// <summary>
    /// Checks whether <paramref name="pieceIndex"/> is set in <paramref name="bitfield"/>
    /// using MSB-first (BitTorrent protocol) bit ordering.
    /// Piece 0 is bit 7 of byte 0.
    /// </summary>
    private static bool HasPieceInBitfield(byte[] bitfield, int pieceIndex)
    {
        int byteIndex = pieceIndex / 8;
        if (byteIndex >= bitfield.Length) return false;
        int bitPosition = 7 - (pieceIndex % 8); // MSB-first
        return (bitfield[byteIndex] & (1 << bitPosition)) != 0;
    }
}
