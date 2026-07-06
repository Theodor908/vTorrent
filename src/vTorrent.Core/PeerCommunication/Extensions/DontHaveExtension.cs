using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Bencode.Objects;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Implements the lt_donthave extension (BEP 54).
/// Notifies peers when we no longer have a piece, and handles
/// incoming DONTHAVE to update our view of peer availability.
/// </summary>
public class DontHaveExtension : IExtension
{
    private readonly ILogger<DontHaveExtension> _logger;
    private readonly Action<int> _onPeerLostPiece;
    private readonly Func<PeerMessage, Task> _sendMessageAsync;
    private readonly int _totalPieces;

    public string Name => "lt_donthave";
    public byte LocalExtensionId { get; } = 3;
    public byte? RemoteExtensionId { get; set; }
    public bool IsEnabled => true;

    public DontHaveExtension(
        ILogger<DontHaveExtension> logger,
        Action<int> onPeerLostPiece,
        Func<PeerMessage, Task> sendMessageAsync,
        int totalPieces)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onPeerLostPiece = onPeerLostPiece ?? throw new ArgumentNullException(nameof(onPeerLostPiece));
        _sendMessageAsync = sendMessageAsync ?? throw new ArgumentNullException(nameof(sendMessageAsync));
        _totalPieces = totalPieces;
    }

    public Task OnExtensionHandshakeReceivedAsync(BDictionary handshake)
    {
        if (handshake.TryGetValue("m", out var mObj) && mObj is BDictionary mDict)
        {
            if (mDict.TryGetValue(Name, out var idObj) && idObj is BNumber idNum)
            {
                RemoteExtensionId = (byte)idNum.Value;
                _logger.LogDebug("Peer supports {Extension} with ID {Id}", Name, RemoteExtensionId);
            }
        }

        return Task.CompletedTask;
    }

    public void AddToHandshake(BDictionary handshake)
    {
        if (!handshake.TryGetValue("m", out var mObj) || mObj is not BDictionary mDict)
        {
            mDict = new BDictionary();
            handshake.Add("m", mDict);
        }

        mDict.AddNumber(Name, LocalExtensionId);
    }

    public Task<byte[]> GenerateMessageAsync(CancellationToken cancellationToken = default)
    {
        // DONTHAVE is event-driven, not periodic
        return Task.FromResult<byte[]>(null);
    }

    public Task OnMessageReceivedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length < 4)
        {
            _logger.LogDebug("lt_donthave message too short ({Length} bytes), ignoring", payload.Length);
            return Task.CompletedTask;
        }

        int pieceIndex = BinaryPrimitives.ReadInt32BigEndian(payload.Span);

        if (pieceIndex < 0 || pieceIndex >= _totalPieces)
        {
            _logger.LogDebug("lt_donthave piece index {Index} out of range [0, {Total}), ignoring",
                pieceIndex, _totalPieces);
            return Task.CompletedTask;
        }

        _logger.LogDebug("Received DONTHAVE for piece {Piece}", pieceIndex);
        _onPeerLostPiece(pieceIndex);

        return Task.CompletedTask;
    }

    public Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task OnDisconnectingAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a DONTHAVE message for the given piece to the remote peer.
    /// Only sends if the peer advertised lt_donthave in their handshake.
    /// Not part of IExtension — called directly via stored reference.
    /// </summary>
    public async Task SendDontHaveAsync(int pieceIndex)
    {
        if (RemoteExtensionId == null)
            return;

        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, pieceIndex);

        var message = PeerMessage.CreateExtended(RemoteExtensionId.Value, payload);

        try
        {
            await _sendMessageAsync(message).ConfigureAwait(false);
            _logger.LogDebug("Sent DONTHAVE for piece {Piece}", pieceIndex);
        }
        catch (ObjectDisposedException)
        {
            // Peer disconnected, ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send DONTHAVE for piece {Piece}", pieceIndex);
        }
    }
}
