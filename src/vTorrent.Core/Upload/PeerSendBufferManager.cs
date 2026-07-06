using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Storage;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Events;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PieceIO;

namespace vTorrent.Core.Upload;

/// <summary>
/// Per-torrent orchestrator for guided read-ahead send buffers.
/// Pre-reads 16 KiB blocks from disk when peers are unchoked, scaling
/// the read-ahead depth proportional to each peer's upload rate.
/// </summary>
internal sealed class PeerSendBufferManager : IDisposable
{
    private const int BlockSize = 16384;

    private readonly IDiskBackend _diskBackend;
    private readonly PieceMapper _pieceMapper;
    private readonly IOptionsMonitor<PeerSettings> _peerSettings;
    private readonly TorrentInfo _torrentInfo;
    private readonly ILogger<PeerSendBufferManager> _logger;
    private readonly CancellationToken _masterToken;

    private readonly ConcurrentDictionary<IPeerConnection, PeerSendBuffer> _peerBuffers = new();
    private readonly ConcurrentDictionary<IPeerConnection, CancellationTokenSource> _peerCts = new();
    private readonly TorrentSendBufferAccounting _accounting;

    // Diagnostic counters
    private long _bufferHits;
    private long _bufferMisses;
    private long _readAheadInvalidations;

    public PeerSendBufferManager(
        IDiskBackend diskBackend,
        PieceMapper pieceMapper,
        IOptionsMonitor<PeerSettings> peerSettings,
        TorrentInfo torrentInfo,
        ILogger<PeerSendBufferManager> logger,
        CancellationToken masterToken)
    {
        _diskBackend = diskBackend;
        _pieceMapper = pieceMapper;
        _peerSettings = peerSettings;
        _torrentInfo = torrentInfo;
        _logger = logger;
        _masterToken = masterToken;

        var settings = peerSettings.CurrentValue;
        _accounting = new TorrentSendBufferAccounting(settings.SendBufferWatermark);
    }

    // ---- Event handlers (wired by EnginePhaseInitializer) ----

    public void OnPeerUnchoked(object? sender, PeerChokeChangedEventArgs e)
    {
        if (e.IsChoked || _masterToken.IsCancellationRequested) return;

        var peer = e.Peer;
        var settings = _peerSettings.CurrentValue;
        var buffer = new PeerSendBuffer(settings.SendBufferLowWatermark, settings.SendBufferWatermarkFactor);

        if (!_peerBuffers.TryAdd(peer, buffer))
        {
            buffer.Dispose();
            return; // Already tracked
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_masterToken);
        _peerCts[peer] = cts;

        _ = ReadAheadLoopAsync(peer, buffer, cts.Token);
    }

    public void OnPeerChoked(object? sender, PeerChokeChangedEventArgs e)
    {
        if (!e.IsChoked) return;
        RemovePeer(e.Peer);
    }

    public void OnPeerDisconnected(object? sender, PeerCommunication.Events.PeerDisconnectedEventArgs e)
    {
        // PeerDisconnectedEventArgs contains PeerInfo, not IPeerConnection.
        // Match by endpoint against tracked peers.
        foreach (var kvp in _peerBuffers)
        {
            if (kvp.Key.PeerInfo?.EndPoint?.ToString() == e.PeerInfo?.EndPoint?.ToString())
            {
                RemovePeer(kvp.Key);
                break;
            }
        }
    }

    // ---- Public API for UploadCoordinator ----

    /// <summary>
    /// Gets the send buffer for a specific peer. Returns null if peer has no buffer.
    /// </summary>
    public PeerSendBuffer? GetPeerBuffer(IPeerConnection peer)
    {
        return _peerBuffers.TryGetValue(peer, out var buffer) ? buffer : null;
    }

    /// <summary>
    /// Recalculates watermarks for all active peer buffers.
    /// Called from the rechoke cycle.
    /// </summary>
    public void RecalculateWatermarks()
    {
        foreach (var buffer in _peerBuffers.Values)
            buffer.RecalculateWatermark();
    }

    /// <summary>
    /// Try to serve a block from the pre-read buffer. Returns true on hit.
    /// Caller must return entry.Data to ArrayPool after sending.
    /// </summary>
    public bool TryServe(IPeerConnection peer, int pieceIndex, int begin, int length, out SendBufferEntry entry)
    {
        if (_peerBuffers.TryGetValue(peer, out var buffer))
        {
            if (buffer.TryDequeue(pieceIndex, begin, length, out entry))
            {
                _accounting.Release(entry.Length);
                _accounting.RecordUpload(entry.Length);
                buffer.UploadMeter.Record(entry.Length);
                Interlocked.Increment(ref _bufferHits);

                // Signal drain to wake read-ahead loop
                try { buffer.DrainSignal.Release(); }
                catch (SemaphoreFullException) { }

                return true;
            }
            else
            {
                // Miss — trigger invalidation if out-of-order
                int drained = buffer.Invalidate(pieceIndex, begin);
                if (drained > 0)
                {
                    Interlocked.Add(ref _readAheadInvalidations, drained);
                    buffer.NextPieceIndex = pieceIndex;
                    buffer.NextBlockOffset = begin;

                    try { buffer.DrainSignal.Release(); }
                    catch (SemaphoreFullException) { }
                }
            }
        }

        Interlocked.Increment(ref _bufferMisses);
        entry = default;
        return false;
    }

    public SendBufferStats GetStats() => new(
        TotalBufferedBytes: _accounting.TotalBufferedBytes,
        BufferHits: Interlocked.Read(ref _bufferHits),
        BufferMisses: Interlocked.Read(ref _bufferMisses),
        ReadAheadInvalidations: Interlocked.Read(ref _readAheadInvalidations),
        ActivePeerBuffers: _peerBuffers.Count,
        Pressure: _accounting.State);

    // ---- Read-Ahead Loop ----

    private async Task ReadAheadLoopAsync(IPeerConnection peer, PeerSendBuffer buffer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && peer.IsConnected)
            {
                var settings = _peerSettings.CurrentValue;
                var targetBlocks = buffer.CalculateTargetBlocks(
                    buffer.UploadMeter.BytesPerSecond,
                    _accounting.EffectiveCeiling);

                // Apply guided reduction under SoftPressure
                if (_accounting.State == PressureState.SoftPressure)
                    targetBlocks = Math.Max(1, targetBlocks / 2);

                if (buffer.BlockCount >= targetBlocks)
                {
                    // Buffer is full — wait for drain signal
                    try { await buffer.DrainSignal.WaitAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                if (_accounting.State == PressureState.HardPause)
                {
                    try { await _accounting.WaitForRecoveryAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                int needed = targetBlocks - buffer.BlockCount;
                for (int i = 0; i < needed && !ct.IsCancellationRequested; i++)
                {
                    if (!_accounting.TryReserve(BlockSize))
                        break; // Global pressure

                    var data = ArrayPool<byte>.Shared.Rent(BlockSize);
                    try
                    {
                        int bytesRead = await ReadBlockFromDiskAsync(
                            buffer.NextPieceIndex, buffer.NextBlockOffset, data, ct).ConfigureAwait(false);

                        if (bytesRead > 0)
                        {
                            buffer.Enqueue(new SendBufferEntry(
                                buffer.NextPieceIndex, buffer.NextBlockOffset, data, bytesRead));
                            AdvancePosition(buffer);
                        }
                        else
                        {
                            ArrayPool<byte>.Shared.Return(data);
                            _accounting.Release(BlockSize);
                            AdvancePosition(buffer); // Skip unreadable block
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ArrayPool<byte>.Shared.Return(data);
                        _accounting.Release(BlockSize);
                        _logger.LogDebug(ex, "Read-ahead disk error for piece {Piece} offset {Offset}",
                            buffer.NextPieceIndex, buffer.NextBlockOffset);
                        AdvancePosition(buffer);
                    }
                }

                // Yield to prevent tight loop when buffer fills instantly
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) { /* Expected on choke/disconnect */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read-ahead loop terminated unexpectedly for peer {Peer}", peer.PeerInfo?.EndPoint);
        }
        finally
        {
            // Guaranteed cleanup: return all rented buffers
            if (_peerBuffers.TryRemove(peer, out var buf))
            {
                var remaining = buf.BlockCount;
                if (remaining > 0)
                    _accounting.Release(remaining * BlockSize);
                buf.Dispose();
            }
        }
    }

    private async ValueTask<int> ReadBlockFromDiskAsync(int pieceIndex, int blockOffset, byte[] buffer, CancellationToken ct)
    {
        if (pieceIndex >= _torrentInfo.Pieces.Count)
            return 0;

        var location = _pieceMapper.MapPieceToFiles(pieceIndex);
        int blockEnd = blockOffset + BlockSize;
        int totalRead = 0;

        foreach (var segment in location.FileSegments)
        {
            // Check if this segment overlaps with our block range
            long segPieceStart = segment.PieceOffset;
            long segPieceEnd = segment.PieceOffset + segment.Length;

            if (blockOffset >= segPieceEnd || blockEnd <= segPieceStart)
                continue; // No overlap

            long readStart = Math.Max(blockOffset, segPieceStart);
            long readEnd = Math.Min(blockEnd, segPieceEnd);
            int readLen = (int)(readEnd - readStart);

            long fileOffset = segment.FileOffset + (readStart - segPieceStart);
            int bufferOffset = (int)(readStart - blockOffset);

            int bytesRead = await _diskBackend.ReadAsync(
                segment.FilePath, fileOffset,
                buffer.AsMemory(bufferOffset, readLen), ct).ConfigureAwait(false);

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private void AdvancePosition(PeerSendBuffer buffer)
    {
        buffer.NextBlockOffset += BlockSize;
        var pieceSize = _pieceMapper.GetPieceSize(
            Math.Min(buffer.NextPieceIndex, _torrentInfo.Pieces.Count - 1));
        if (buffer.NextBlockOffset >= pieceSize)
        {
            buffer.NextPieceIndex++;
            buffer.NextBlockOffset = 0;
        }
    }

    private void RemovePeer(IPeerConnection peer)
    {
        if (_peerCts.TryRemove(peer, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        // PeerSendBuffer cleanup happens in ReadAheadLoopAsync's finally block
    }

    public void CancelAll()
    {
        foreach (var kvp in _peerCts)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _peerCts.Clear();
    }

    public void Dispose()
    {
        CancelAll();
        foreach (var kvp in _peerBuffers)
            kvp.Value.Dispose();
        _peerBuffers.Clear();
    }
}
