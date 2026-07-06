using System;
using System.Buffers;

using System.Collections.Concurrent;
using System.Collections.Generic;

using System.Threading;

using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using vTorrent.Abstractions.Models;
using vTorrent.Bencode.Torrents;

using vTorrent.Core.PeerCommunication.Models;

using vTorrent.Core.PieceIO;
using vTorrent.Core.Engine;
using vTorrent.Core.Interfaces;

namespace vTorrent.Core.Upload;

public class UploadCoordinator : IMessageHandler, IDisposable

{

    private readonly ILogger<UploadCoordinator> _logger;

    private readonly IPeerManager _peerManager;

    private readonly IPieceManager _pieceManager;

    private readonly IChokingManager _chokingManager;

    private readonly IStatisticsTracker _statisticsTracker;

    private readonly TorrentInfo _torrentInfo;

    private readonly Func<int, bool> _hasPiece;

    private PeerSendBufferManager? _sendBufferManager;

    /// <summary>Sets the send buffer manager for read-ahead cache hits. Called by EnginePhaseInitializer.</summary>
    internal void SetSendBufferManager(PeerSendBufferManager? value) => _sendBufferManager = value;

    private SeedModeVerifier? _seedModeVerifier;

    /// <summary>Sets the seed mode verifier for lazy hash-on-upload. Called by EnginePhaseInitializer.</summary>
    internal void SetSeedModeVerifier(SeedModeVerifier? value) => _seedModeVerifier = value;

    /// <summary>
    /// Recalculates send buffer watermarks for all active peers.
    /// Called from the rechoke cycle (every 15s).
    /// </summary>
    public void RecalculateWatermarks()
    {
        _sendBufferManager?.RecalculateWatermarks();
    }

    // Configuration

    private readonly int _maxConcurrentUploads;

    private readonly int _maxBlockSize = 131072; // 128 KB

    // State

    private readonly SemaphoreSlim _uploadSemaphore;

    private readonly ConcurrentDictionary<UploadKey, CancellationTokenSource> _pendingUploads = new();

    private int _activeUploads;

    private long _blocksUploaded;

    private bool _disposed;

    // Dispatch loop state

    private readonly ConcurrentDictionary<IPeerConnection, PeerRequestQueue> _peerQueues = new();

    private readonly SemaphoreSlim _dispatchSignal = new(0);

    private CancellationTokenSource _dispatchCts;

    private Task _dispatchTask;

    private const int MaxQueueDepthPerPeer = 250;

    // Read failure tracking (libtorrent parity: peer_connection.cpp:5601-5614)

    private const int ReadFailureDisconnectThreshold = 100;
    private readonly ConcurrentDictionary<IPeerConnection, int> _peerReadFailures = new();
    private readonly ConcurrentDictionary<string, byte> _raisedReadErrors = new();
    private Func<IPeerConnection, int, Task>? _sendDontHave;

    /// <summary>
    /// Raised when a disk read fails during upload. libtorrent parity: file_error_alert.
    /// </summary>
    public event EventHandler<FileReadFailedEventArgs>? FileReadFailed;

    /// <summary>
    /// Set callback to send BEP 54 DONTHAVE to a peer. Wired by engine after creation.
    /// </summary>
    public void SetDontHaveCallback(Func<IPeerConnection, int, Task> callback) => _sendDontHave = callback;

    // Events

    public event EventHandler<BlockUploadedEventArgs> BlockUploaded;

    // Properties (delegate to statistics tracker)

    public long BytesUploaded => _statisticsTracker.TotalUploaded;

    public double UploadRate => _statisticsTracker.UploadRate;

    public long BlocksUploaded => Interlocked.Read(ref _blocksUploaded);

    public int ActiveUploads => _activeUploads;

    public UploadCoordinator(

        IPeerManager peerManager,

        IPieceManager pieceManager,

        IChokingManager chokingManager,

        IStatisticsTracker statisticsTracker,

        TorrentInfo torrentInfo,

        Func<int, bool> hasPiece,

        ILogger<UploadCoordinator> logger,

        int maxConcurrentUploads = 8)

    {

        _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));

        _pieceManager = pieceManager ?? throw new ArgumentNullException(nameof(pieceManager));

        _chokingManager = chokingManager ?? throw new ArgumentNullException(nameof(chokingManager));

        _statisticsTracker = statisticsTracker ?? throw new ArgumentNullException(nameof(statisticsTracker));

        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));

        _hasPiece = hasPiece ?? throw new ArgumentNullException(nameof(hasPiece));

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _maxConcurrentUploads = maxConcurrentUploads;

        _uploadSemaphore = new SemaphoreSlim(maxConcurrentUploads);

        _chokingManager.RechokeCycleCompleted += RecalculateWatermarks;

        _logger.LogDebug("UploadCoordinator created - max concurrent uploads: {Max}", maxConcurrentUploads);

    }

    public Task StartAsync(CancellationToken cancellationToken = default)

    {

        StartDispatchLoop();

        _logger.LogDebug("UploadCoordinator started");

        return Task.CompletedTask;

    }

    public Task StopAsync()

    {

        StopDispatchLoop();

        foreach (var cts in _pendingUploads.Values)

        {

            cts.Cancel();

        }

        _pendingUploads.Clear();

        foreach (var queue in _peerQueues.Values)

        {

            queue.Clear();

        }

        _peerQueues.Clear();

        _logger.LogDebug("UploadCoordinator stopped - uploaded: {Bytes} in {Blocks} blocks",

            TorrentUtilities.FormatBytes(BytesUploaded), _blocksUploaded);

        return Task.CompletedTask;

    }

    public void RegisterHandlers(PeerMessageRouter router)

    {

        router.RegisterHandler(MessageType.Request, HandleRequestAsync);

        router.RegisterHandler(MessageType.Cancel, HandleCancelAsync);

    }

    #region Dispatch Loop Lifecycle

    private void StartDispatchLoop()

    {

        _dispatchCts = new CancellationTokenSource();

        _dispatchTask = Task.Run(() => UploadDispatchLoop(_dispatchCts.Token));

        _logger.LogDebug("Upload dispatch loop started");

    }

    private void StopDispatchLoop()

    {

        if (_dispatchCts == null)

            return;

        _dispatchCts.Cancel();

        // Release dispatch signal so the loop can observe cancellation
        _dispatchSignal.Release();

        try

        {

            _dispatchTask?.Wait(TimeSpan.FromSeconds(5));

        }

        catch (AggregateException)

        {

            // Cancellation exceptions are expected

        }

        _dispatchCts.Dispose();

        _dispatchCts = null;

        _logger.LogDebug("Upload dispatch loop stopped");

    }

    private async Task UploadDispatchLoop(CancellationToken cancellationToken)

    {

        _logger.LogDebug("Upload dispatch loop running");

        while (!cancellationToken.IsCancellationRequested)

        {

            try

            {

                await _dispatchSignal.WaitAsync(cancellationToken).ConfigureAwait(false);

                // Drain accumulated signal count to avoid spinning on empty queues
                while (_dispatchSignal.Wait(0)) { }

            }

            catch (OperationCanceledException)

            {

                break;

            }

            if (cancellationToken.IsCancellationRequested)

                break;

            // Round-robin drain: keep making passes over all peers until no peer has a
            // dequeuable request left. Each pass dispatches at most one block per peer
            // (fairness across peers); the outer do/while repeats so an entire batch of
            // queued requests is fully served on a single wake. Previously the loop drained
            // the accumulated signal count and then made ONE pass, dispatching one block per
            // peer and stranding the rest of the queue until a fresh signal arrived — which
            // capped upload throughput at ~one block per incoming REQUEST and forced the
            // remote peer to time out and re-request every block (getting snubbed).
            bool anyDispatched;

            do

            {

                anyDispatched = false;

                // Re-snapshot each pass so peers/queues added mid-drain are also serviced.
                var peers = new List<IPeerConnection>(_peerQueues.Keys);

                foreach (var peer in peers)

                {

                    if (cancellationToken.IsCancellationRequested)

                        break;

                    // Remove disconnected peers
                    if (!peer.IsConnected)

                {

                    if (_peerQueues.TryRemove(peer, out var removedQueue))

                        removedQueue.Clear();

                    continue;

                }

                if (!_peerQueues.TryGetValue(peer, out var queue) || queue.IsEmpty)

                    continue;

                if (!queue.TryDequeue(out var request))

                    continue;

                // NOTE: No send-buffer watermark gate here. The read-ahead depth is
                // already throttled inside PeerSendBufferManager.ReadAheadLoopAsync
                // (it fills up to CalculateTargetBlocks then waits on the drain signal),
                // mirroring libtorrent's fill_send_buffer() watermark which caps how much
                // is *read* into the send buffer — not whether a queued request is served.
                // A previous gate here re-checked the read-ahead cache fill level and, when
                // it was "over watermark" (i.e. blocks were pre-read and ready to serve),
                // re-enqueued the request and re-signalled the loop. That produced a tight
                // infinite spin that never served the block and never drained the cache.

                // Acquire semaphore slot — fire off the actual send as a separate task so the
                // dispatch loop stays responsive and handles the remaining peers in this round.
                await _uploadSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                anyDispatched = true;

                Interlocked.Increment(ref _activeUploads);

                // Capture locals for the task closure
                var capturedPeer = peer;

                var capturedRequest = request;

                _ = Task.Run(async () =>

                {

                    try

                    {

                        // Try cache first, fall back to disk read
                        if (_sendBufferManager != null &&
                            _sendBufferManager.TryServe(capturedPeer, capturedRequest.PieceIndex, capturedRequest.Begin, capturedRequest.Length, out var buffered))

                        {

                            try

                            {

                                var blockData = new byte[buffered.Length];

                                Buffer.BlockCopy(buffered.Data, 0, blockData, 0, buffered.Length);

                                await capturedPeer.SendBlockAsync(capturedRequest.PieceIndex, capturedRequest.Begin, blockData).ConfigureAwait(false);

                                Interlocked.Increment(ref _blocksUploaded);

                                BlockUploaded?.Invoke(this, new BlockUploadedEventArgs(capturedPeer, capturedRequest.PieceIndex, capturedRequest.Begin, capturedRequest.Length));

                                _statisticsTracker.RecordPayloadUpload(capturedPeer, capturedRequest.Length);

                            }

                            finally

                            {

                                ArrayPool<byte>.Shared.Return(buffered.Data);

                            }

                        }

                        else

                        {

                            var uploadKey = new UploadKey(capturedPeer, capturedRequest.PieceIndex, capturedRequest.Begin);

                            // Atomically claim the pending entry — if it's gone, the request was cancelled
                            if (!_pendingUploads.TryRemove(uploadKey, out var originalCts))
                            {
                                // Request was cancelled via HandleCancelAsync before dispatch
                                return;
                            }

                            originalCts.Dispose();

                            try

                            {

                                await SendBlockAsync(capturedPeer, capturedRequest.PieceIndex, capturedRequest.Begin, capturedRequest.Length, cancellationToken).ConfigureAwait(false);

                            }

                            catch (OperationCanceledException)

                            {

                                _logger.LogDebug("Upload cancelled: piece {Piece}, offset {Offset} to {Peer}",
                                    capturedRequest.PieceIndex, capturedRequest.Begin, capturedPeer.PeerInfo.EndPoint);

                            }

                        }

                    }

                    catch (ObjectDisposedException)

                    {

                        // Peer disconnected between dispatch and send

                    }

                    catch (Exception ex)

                    {

                        _logger.LogError(ex, "Error uploading block to {Peer}", capturedPeer.PeerInfo.EndPoint);

                    }

                    finally

                    {

                        Interlocked.Decrement(ref _activeUploads);

                        _uploadSemaphore.Release();

                    }

                }, CancellationToken.None);

                }

            } while (anyDispatched && !cancellationToken.IsCancellationRequested);

        }

        _logger.LogDebug("Upload dispatch loop exited");

    }

    #endregion

    #region Message Handlers (called by PeerMessageRouter)

    public Task HandleRequestAsync(IPeerConnection peer, PeerMessage message)

    {

        var (pieceIndex, begin, length) = message.ParseRequest();

        _logger.LogDebug("Request from {Peer}: piece {Piece}, offset {Offset}, length {Length}",

            peer.PeerInfo.EndPoint, pieceIndex, begin, length);

        if (!ValidateRequest(peer, pieceIndex, begin, length))

            return Task.CompletedTask;

        if (!_chokingManager.IsPeerUnchoked(peer))

        {

            _logger.LogDebug("Rejecting request from choked peer {Peer}", peer.PeerInfo.EndPoint);

            // Fire-and-forget the reject send — we don't want to block the message handler
            _ = TrySendRejectAsync(peer, pieceIndex, begin, length);

            return Task.CompletedTask;

        }

        // Register a pending entry so cancel can find it
        var uploadKey = new UploadKey(peer, pieceIndex, begin);

        var cts = new CancellationTokenSource();

        _pendingUploads[uploadKey] = cts;

        // Get or create queue for this peer
        var queue = _peerQueues.GetOrAdd(peer, _ => new PeerRequestQueue(MaxQueueDepthPerPeer));

        var request = new BlockRequest(pieceIndex, begin, length);

        if (!queue.Enqueue(request))

        {

            // Queue is full — drop the request and clean up the pending entry
            _logger.LogDebug("Upload queue full for {Peer}, dropping request piece={Piece} offset={Offset}",

                peer.PeerInfo.EndPoint, pieceIndex, begin);

            _pendingUploads.TryRemove(uploadKey, out _);

            cts.Dispose();

            return Task.CompletedTask;

        }

        // Wake the dispatch loop
        _dispatchSignal.Release();

        return Task.CompletedTask;

    }

    public Task HandleCancelAsync(IPeerConnection peer, PeerMessage message)

    {

        var (pieceIndex, begin, length) = message.ParseRequest();

        _logger.LogTrace("Cancel from {Peer}: piece {Piece}, offset {Offset}",

            peer.PeerInfo.EndPoint, pieceIndex, begin);

        var uploadKey = new UploadKey(peer, pieceIndex, begin);

        // Cancel any in-flight disk read/send for this block
        if (_pendingUploads.TryRemove(uploadKey, out var cts))

        {

            cts.Cancel();

            cts.Dispose();

            _logger.LogDebug("Cancelled upload: piece {Piece}, offset {Offset} to {Peer}",

                pieceIndex, begin, peer.PeerInfo.EndPoint);

        }

        // Also remove from the queue if it hasn't been dispatched yet
        if (_peerQueues.TryGetValue(peer, out var queue))

        {

            queue.Cancel(pieceIndex, begin);

        }

        return Task.CompletedTask;

    }

    #endregion

    #region Peer Lifecycle Events

    /// <summary>Clears a peer's request queue when they disconnect.</summary>
    public void OnPeerDisconnected(IPeerConnection peer)

    {

        if (_peerQueues.TryRemove(peer, out var queue))

        {

            queue.Clear();

            _logger.LogDebug("Cleared upload queue for disconnected peer {Peer}", peer.PeerInfo.EndPoint);

        }

    }

    /// <summary>Clears a peer's request queue when they are choked (pending requests become invalid).</summary>
    public void OnPeerChoked(IPeerConnection peer)

    {

        if (_peerQueues.TryGetValue(peer, out var queue))

        {

            queue.Clear();

            _logger.LogDebug("Cleared upload queue for choked peer {Peer}", peer.PeerInfo.EndPoint);

        }

    }

    #endregion

    private async Task TrySendRejectAsync(IPeerConnection peer, int pieceIndex, int begin, int length)

    {

        try

        {

            await peer.SendMessageAsync(PeerMessage.CreateRejectRequest(pieceIndex, begin, length)).ConfigureAwait(false);

        }

        catch (ObjectDisposedException)

        {

            // Peer already disconnected, ignore

        }

        catch (Exception ex)

        {

            _logger.LogDebug(ex, "Failed to send reject message to {Peer}", peer.PeerInfo.EndPoint);

        }

    }

    private bool ValidateRequest(IPeerConnection peer, int pieceIndex, int begin, int length)

    {

        if (pieceIndex < 0 || pieceIndex >= _torrentInfo.PieceCount)

        {

            _logger.LogWarning("Invalid piece index {Index} from {Peer}", pieceIndex, peer.PeerInfo.EndPoint);

            return false;

        }

        if (!_hasPiece(pieceIndex))

        {

            _logger.LogWarning("Peer {Peer} requested piece {Index} we don't have",

                peer.PeerInfo.EndPoint, pieceIndex);

            return false;

        }

        if (length <= 0 || length > _maxBlockSize)

        {

            _logger.LogWarning("Invalid block size {Size} from {Peer}", length, peer.PeerInfo.EndPoint);

            return false;

        }

        var pieceSize = GetPieceSize(pieceIndex);

        if (begin < 0 || begin >= pieceSize || begin + length > pieceSize)

        {

            _logger.LogWarning("Invalid block bounds: piece {Piece}, offset {Offset}, length {Length} from {Peer}",

                pieceIndex, begin, length, peer.PeerInfo.EndPoint);

            return false;

        }

        return true;

    }

    private async Task SendBlockAsync(IPeerConnection peer, int pieceIndex, int begin, int length, CancellationToken cancellationToken)

    {

        var readResult = await _pieceManager.ReadBlockAsync(pieceIndex, begin, length, cancellationToken).ConfigureAwait(false);

        if (!readResult.IsSuccess)
        {
            _logger.LogWarning("Read failed for piece={Piece} offset={Offset}: {Error}",
                pieceIndex, begin, readResult.ErrorMessage);

            // libtorrent parity (peer_connection.cpp:5601-5614):
            // Tell peer we don't have this piece + reject the request
            if (_sendDontHave != null)
                _ = _sendDontHave(peer, pieceIndex);
            _ = TrySendRejectAsync(peer, pieceIndex, begin, length);

            // Track consecutive failures per peer — disconnect after threshold
            var failures = _peerReadFailures.AddOrUpdate(peer, 1, (_, count) => count + 1);
            if (failures >= ReadFailureDisconnectThreshold)
            {
                _logger.LogWarning("Disconnecting {Peer}: {Count} consecutive read failures",
                    peer.PeerInfo.EndPoint, failures);
                _ = peer.DisconnectAsync();
            }

            // Post file error alert — deduplicate to prevent spam
            var errorKey = readResult.ErrorMessage ?? "Unknown read error";
            if (_raisedReadErrors.TryAdd(errorKey, 0))
            {
                FileReadFailed?.Invoke(this, new FileReadFailedEventArgs(pieceIndex, errorKey));
            }

            return;
        }

        // Seed mode: verify piece hash before uploading (libtorrent parity)
        if (_seedModeVerifier != null && !_seedModeVerifier.IsVerified(pieceIndex))
        {
            var verifyResult = await _seedModeVerifier.VerifyPieceAsync(pieceIndex, cancellationToken).ConfigureAwait(false);
            if (verifyResult == SeedVerifyResult.Failed)
            {
                _logger.LogWarning("Seed mode: piece {Piece} failed verification, not uploading to {Peer}",
                    pieceIndex, peer.PeerInfo.EndPoint);
                return;
            }
        }

        await peer.SendBlockAsync(pieceIndex, begin, readResult.Data, cancellationToken).ConfigureAwait(false);

        _statisticsTracker.RecordPayloadUpload(peer, length);

        Interlocked.Increment(ref _blocksUploaded);

        _logger.LogDebug("↑ Uploaded {Length} KB to {Peer} (piece {Piece}) - Total: {Total}",

            length / 1024, peer.PeerInfo.EndPoint, pieceIndex, TorrentUtilities.FormatBytes(BytesUploaded));

        BlockUploaded?.Invoke(this, new BlockUploadedEventArgs(peer, pieceIndex, begin, length));

        // Reset failure counter on successful read
        _peerReadFailures.TryRemove(peer, out _);

    }

    private long GetPieceSize(int pieceIndex)

    {

        return TorrentUtilities.GetPieceSize(_torrentInfo, pieceIndex);

    }

    public void Dispose()

    {

        if (_disposed)

            return;

        _disposed = true;

        StopDispatchLoop();

        foreach (var cts in _pendingUploads.Values)

        {

            cts.Cancel();

            cts.Dispose();

        }

        _pendingUploads.Clear();

        foreach (var queue in _peerQueues.Values)

        {

            queue.Clear();

        }

        _peerQueues.Clear();

        _uploadSemaphore.Dispose();

        _dispatchSignal.Dispose();

        _logger.LogDebug("UploadCoordinator disposed");

    }

    private readonly record struct UploadKey(IPeerConnection Peer, int PieceIndex, int Begin);

}
