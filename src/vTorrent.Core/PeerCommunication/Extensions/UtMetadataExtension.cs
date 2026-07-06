using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Implements the ut_metadata extension (BEP 9) for downloading torrent metadata
/// from peers. This enables magnet link support by allowing metadata to be
/// fetched from the swarm without a .torrent file.
///
/// Protocol:
/// - Extension handshake includes "metadata_size" field (if we have metadata)
/// - Messages use msg_type: 0=request, 1=data, 2=reject
/// - Metadata is split into 16KB pieces
/// - Each piece is validated against the info hash when complete
///
/// Based on libtorrent's ut_metadata.cpp implementation.
/// </summary>
public class UtMetadataExtension : IExtension
{
    private readonly ILogger<UtMetadataExtension> _logger;
    private readonly byte[] _infoHash;
    private readonly Func<byte[]> _getMetadata;
    private readonly Action<byte[]> _onMetadataReceived;
    private readonly IOptionsMonitor<PeerSettings> _peerMonitor;

    // Metadata piece size (16 KB per BEP 9)
    public const int MetadataPieceSize = 16384;

    // Maximum concurrent requests per peer
    public const int MaxConcurrentRequests = 2;

    // Request timeout
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // Message types per BEP 9
    private const int MsgTypeRequest = 0;
    private const int MsgTypeData = 1;
    private const int MsgTypeReject = 2;

    // State
    private int? _remoteMetadataSize;
    private byte[] _metadataBuffer;
    private readonly HashSet<int> _receivedPieces = new();
    private readonly HashSet<int> _pendingRequests = new();
    private readonly Dictionary<int, DateTime> _requestTimestamps = new();
    private int _totalPieces;
    private bool _metadataComplete;
    private readonly object _lock = new();

    // Rate limiting for incoming requests
    private DateTime _lastRequestSent = DateTime.MinValue;
    private int _requestsSentThisPeriod = 0;

    public string Name => "ut_metadata";
    public byte LocalExtensionId { get; } = 2; // Standard ID for ut_metadata
    public byte? RemoteExtensionId { get; set; }
    public bool IsEnabled { get; }

    /// <summary>
    /// Event raised when metadata download progresses.
    /// </summary>
    public event Action<int, int> ProgressChanged; // (received, total)

    /// <summary>
    /// Event raised when metadata download fails.
    /// </summary>
    public event Action<string> MetadataFailed;

    /// <summary>
    /// Creates a new ut_metadata extension instance.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="infoHash">The expected info hash (20 bytes).</param>
    /// <param name="getMetadata">Function to get our metadata (if we have it), or null.</param>
    /// <param name="onMetadataReceived">Callback when complete metadata is received and validated.</param>
    /// <param name="isEnabled">Whether to enable this extension.</param>
    public UtMetadataExtension(
        ILogger<UtMetadataExtension> logger,
        byte[] infoHash,
        Func<byte[]> getMetadata,
        Action<byte[]> onMetadataReceived,
        bool isEnabled = true,
        IOptionsMonitor<PeerSettings> peerMonitor = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _infoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));
        _getMetadata = getMetadata;
        _onMetadataReceived = onMetadataReceived ?? throw new ArgumentNullException(nameof(onMetadataReceived));
        IsEnabled = isEnabled;
        _peerMonitor = peerMonitor;

        if (_infoHash.Length != 20)
            throw new ArgumentException("Info hash must be 20 bytes", nameof(infoHash));
    }

    public Task OnExtensionHandshakeReceivedAsync(BDictionary handshake)
    {
        // Extract the remote extension ID from the "m" dictionary
        if (handshake.TryGetValue("m", out var mObj) && mObj is BDictionary mDict)
        {
            if (mDict.TryGetValue(Name, out var idObj) && idObj is BNumber idNum)
            {
                RemoteExtensionId = (byte)idNum.Value;
                _logger.LogDebug("Peer supports {Extension} with ID {Id}", Name, RemoteExtensionId);
            }
        }

        // Extract metadata_size if peer has metadata
        if (handshake.TryGetValue("metadata_size", out var sizeObj) && sizeObj is BNumber sizeNum)
        {
            var size = (int)sizeNum.Value;
            if (size > 0 && size <= (_peerMonitor?.CurrentValue.MaxMetadataSize ?? 31457280))
            {
                lock (_lock)
                {
                    _remoteMetadataSize = size;
                    _totalPieces = (size + MetadataPieceSize - 1) / MetadataPieceSize;

                    // Initialize buffer if we don't have metadata yet
                    if (_metadataBuffer == null && !_metadataComplete)
                    {
                        _metadataBuffer = new byte[size];
                        _logger.LogDebug("Peer has metadata ({Size} bytes, {Pieces} pieces)",
                            size, _totalPieces);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    public void AddToHandshake(BDictionary handshake)
    {
        if (!IsEnabled)
            return;

        // Ensure "m" dictionary exists
        if (!handshake.TryGetValue("m", out var mObj) || mObj is not BDictionary mDict)
        {
            mDict = new BDictionary();
            handshake.Add("m", mDict);
        }

        // Add our extension ID
        mDict.AddNumber(Name, LocalExtensionId);

        // If we have metadata, advertise its size
        var ourMetadata = _getMetadata?.Invoke();
        if (ourMetadata != null && ourMetadata.Length > 0)
        {
            handshake.AddNumber("metadata_size", ourMetadata.Length);
        }
    }

    public async Task<byte[]> GenerateMessageAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || RemoteExtensionId == null)
            return null;

        // Don't request if we already have complete metadata
        if (_metadataComplete)
            return null;

        // Don't request if peer doesn't have metadata
        lock (_lock)
        {
            if (_remoteMetadataSize == null || _remoteMetadataSize <= 0)
                return null;
        }

        // Rate limiting: don't flood the peer
        var now = DateTime.UtcNow;
        if ((now - _lastRequestSent).TotalMilliseconds < 100)
            return null;

        // Check for timed out requests
        CleanupTimedOutRequests();

        // Find next piece to request
        int? pieceToRequest = null;
        lock (_lock)
        {
            if (_pendingRequests.Count >= MaxConcurrentRequests)
                return null;

            for (int i = 0; i < _totalPieces; i++)
            {
                if (!_receivedPieces.Contains(i) && !_pendingRequests.Contains(i))
                {
                    pieceToRequest = i;
                    break;
                }
            }

            if (pieceToRequest == null)
                return null;

            _pendingRequests.Add(pieceToRequest.Value);
            _requestTimestamps[pieceToRequest.Value] = now;
        }

        _lastRequestSent = now;
        _logger.LogDebug("Requesting metadata piece {Piece}/{Total}", pieceToRequest.Value, _totalPieces);

        return CreateRequestMessage(pieceToRequest.Value);
    }

    public async Task OnMessageReceivedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        try
        {
            // Parse the bencoded message header
            var parser = new BencodeParser();
            var obj = parser.Parse(payload.Span, out int consumed);

            if (obj is not BDictionary dict)
            {
                _logger.LogDebug("Invalid ut_metadata message: not a dictionary");
                return;
            }

            // Get message type
            if (!dict.TryGetValue("msg_type", out var msgTypeObj) || msgTypeObj is not BNumber msgTypeNum)
            {
                _logger.LogDebug("Invalid ut_metadata message: missing msg_type");
                return;
            }

            var msgType = (int)msgTypeNum.Value;

            // Get piece index
            if (!dict.TryGetValue("piece", out var pieceObj) || pieceObj is not BNumber pieceNum)
            {
                _logger.LogDebug("Invalid ut_metadata message: missing piece");
                return;
            }

            var piece = (int)pieceNum.Value;

            switch (msgType)
            {
                case MsgTypeRequest:
                    await HandleRequestAsync(piece, cancellationToken);
                    break;

                case MsgTypeData:
                    // Data follows after the bencoded dict
                    var dataOffset = consumed;
                    if (dataOffset < payload.Length)
                    {
                        var data = payload.Slice(dataOffset);
                        await HandleDataAsync(piece, data, dict, cancellationToken);
                    }
                    break;

                case MsgTypeReject:
                    HandleReject(piece);
                    break;

                default:
                    _logger.LogDebug("Unknown ut_metadata message type: {Type}", msgType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing ut_metadata message");
        }
    }

    private async Task HandleRequestAsync(int piece, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Received metadata request for piece {Piece}", piece);

        var ourMetadata = _getMetadata?.Invoke();
        if (ourMetadata == null || ourMetadata.Length == 0)
        {
            // We don't have metadata, send reject
            _logger.LogDebug("Rejecting metadata request (we don't have metadata)");
            return;
        }

        // Calculate piece bounds
        var pieceStart = piece * MetadataPieceSize;
        if (pieceStart >= ourMetadata.Length)
        {
            _logger.LogDebug("Invalid piece request: {Piece} (metadata size: {Size})", piece, ourMetadata.Length);
            return;
        }

        var pieceLength = Math.Min(MetadataPieceSize, ourMetadata.Length - pieceStart);
        var pieceData = new byte[pieceLength];
        Array.Copy(ourMetadata, pieceStart, pieceData, 0, pieceLength);

        // Create and queue response
        // Note: In a real implementation, this would be queued and sent
        _logger.LogDebug("Would send metadata piece {Piece} ({Length} bytes)", piece, pieceLength);
    }

    private async Task HandleDataAsync(int piece, ReadOnlyMemory<byte> data, BDictionary dict, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_metadataComplete)
                return;

            // Remove from pending
            _pendingRequests.Remove(piece);
            _requestTimestamps.Remove(piece);

            // Validate piece index
            if (piece < 0 || piece >= _totalPieces)
            {
                _logger.LogDebug("Invalid metadata piece index: {Piece}", piece);
                return;
            }

            // Already have this piece?
            if (_receivedPieces.Contains(piece))
            {
                _logger.LogDebug("Already have metadata piece {Piece}", piece);
                return;
            }

            // Get total_size from message (optional but useful for validation)
            if (dict.TryGetValue("total_size", out var totalSizeObj) && totalSizeObj is BNumber totalSizeNum)
            {
                var totalSize = (int)totalSizeNum.Value;
                if (_remoteMetadataSize != totalSize)
                {
                    _logger.LogDebug("Metadata size mismatch: expected {Expected}, got {Got}",
                        _remoteMetadataSize, totalSize);
                    return;
                }
            }

            // Calculate expected piece size
            var pieceStart = piece * MetadataPieceSize;
            var expectedSize = Math.Min(MetadataPieceSize, _remoteMetadataSize.Value - pieceStart);

            if (data.Length != expectedSize)
            {
                _logger.LogDebug("Metadata piece {Piece} size mismatch: expected {Expected}, got {Got}",
                    piece, expectedSize, data.Length);
                return;
            }

            // Copy data to buffer
            data.Span.CopyTo(_metadataBuffer.AsSpan(pieceStart));
            _receivedPieces.Add(piece);

            _logger.LogDebug("Received metadata piece {Piece}/{Total} ({Received}/{Total} complete)",
                piece, _totalPieces, _receivedPieces.Count, _totalPieces);

            // Notify progress
            ProgressChanged?.Invoke(_receivedPieces.Count, _totalPieces);

            // Check if we have all pieces
            if (_receivedPieces.Count >= _totalPieces)
            {
                ValidateAndCompleteMetadata();
            }
        }
    }

    private void HandleReject(int piece)
    {
        _logger.LogDebug("Peer rejected metadata request for piece {Piece}", piece);

        lock (_lock)
        {
            _pendingRequests.Remove(piece);
            _requestTimestamps.Remove(piece);
        }
    }

    private void ValidateAndCompleteMetadata()
    {
        // Validate against info hash
        var hash = SHA1.HashData(_metadataBuffer);

        if (!hash.SequenceEqual(_infoHash))
        {
            _logger.LogWarning("Metadata validation failed: hash mismatch");
            _logger.LogDebug("Expected: {Expected}", Convert.ToHexString(_infoHash));
            _logger.LogDebug("Got: {Got}", Convert.ToHexString(hash));

            // Reset and try again
            _receivedPieces.Clear();
            _metadataBuffer = new byte[_remoteMetadataSize.Value];
            MetadataFailed?.Invoke("Hash mismatch");
            return;
        }

        _logger.LogDebug("Metadata validation successful ({Size} bytes)", _metadataBuffer.Length);
        _metadataComplete = true;

        // Make a copy to pass to callback
        var metadata = new byte[_metadataBuffer.Length];
        Array.Copy(_metadataBuffer, metadata, metadata.Length);

        // Notify callback
        _onMetadataReceived(metadata);
    }

    private void CleanupTimedOutRequests()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var timedOut = _requestTimestamps
                .Where(kvp => now - kvp.Value > RequestTimeout)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var piece in timedOut)
            {
                _pendingRequests.Remove(piece);
                _requestTimestamps.Remove(piece);
                _logger.LogDebug("Metadata request for piece {Piece} timed out", piece);
            }
        }
    }

    private byte[] CreateRequestMessage(int piece)
    {
        // Create bencoded message: d8:msg_typei0e5:piecei<piece>ee
        var dict = new BDictionary();
        dict.AddNumber("msg_type", MsgTypeRequest);
        dict.AddNumber("piece", piece);
        return dict.EncodeAsBytes();
    }

    private byte[] CreateDataMessage(int piece, byte[] data, int totalSize)
    {
        // Create bencoded message followed by raw data
        var dict = new BDictionary();
        dict.AddNumber("msg_type", MsgTypeData);
        dict.AddNumber("piece", piece);
        dict.AddNumber("total_size", totalSize);

        var header = dict.EncodeAsBytes();
        var result = new byte[header.Length + data.Length];
        Array.Copy(header, result, header.Length);
        Array.Copy(data, 0, result, header.Length, data.Length);
        return result;
    }

    private byte[] CreateRejectMessage(int piece)
    {
        var dict = new BDictionary();
        dict.AddNumber("msg_type", MsgTypeReject);
        dict.AddNumber("piece", piece);
        return dict.EncodeAsBytes();
    }

    public Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        // Reset per-connection state
        lock (_lock)
        {
            _pendingRequests.Clear();
            _requestTimestamps.Clear();
            _requestsSentThisPeriod = 0;
            _lastRequestSent = DateTime.MinValue;
        }

        return Task.CompletedTask;
    }

    public Task OnDisconnectingAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the current metadata download progress.
    /// </summary>
    public (int Received, int Total) GetProgress()
    {
        lock (_lock)
        {
            return (_receivedPieces.Count, _totalPieces);
        }
    }

    /// <summary>
    /// Whether metadata download is complete.
    /// </summary>
    public bool IsMetadataComplete
    {
        get
        {
            lock (_lock) { return _metadataComplete; }
        }
    }

    /// <summary>
    /// Whether the peer has metadata we can download.
    /// </summary>
    public bool PeerHasMetadata
    {
        get
        {
            lock (_lock) { return _remoteMetadataSize.HasValue && _remoteMetadataSize.Value > 0; }
        }
    }

    /// <summary>
    /// Resets the metadata download state (e.g., after validation failure).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _receivedPieces.Clear();
            _pendingRequests.Clear();
            _requestTimestamps.Clear();
            _metadataComplete = false;

            if (_remoteMetadataSize.HasValue)
            {
                _metadataBuffer = new byte[_remoteMetadataSize.Value];
            }
        }
    }
}
