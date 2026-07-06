using vTorrent.Abstractions.Storage;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace vTorrent.Core.PieceIO;

/// <summary>
/// Producer-consumer pipeline for piece hash verification.
/// One reader feeds a bounded channel; N hasher tasks consume it concurrently.
/// Models libtorrent's checking_resume_data parallel recheck design.
/// </summary>
internal sealed class PieceVerificationPipeline
{
    private readonly IDiskBackend _backend;
    private readonly PieceVerifier _verifier;
    private readonly PieceMapper _mapper;
    private readonly int _totalPieces;
    private readonly int _hashThreads;
    private readonly long _maxMemoryBytes;
    private readonly int _channelCapacity;

    // Tracks in-flight bytes across reader and all hashers.
    private long _currentMemoryBytes;

    // ---------------------------------------------------------------------------
    // Inner types
    // ---------------------------------------------------------------------------

    private readonly record struct VerificationItem(
        int PieceIndex,
        byte[] Data,        // Rented from ArrayPool<byte>.Shared
        int PieceSize);

    public readonly record struct VerificationProgress(
        int PieceIndex,
        int TotalPieces,
        bool Valid,
        PieceVerifyResult Result)
    {
        public double PercentComplete => (PieceIndex + 1.0) / TotalPieces * 100;
    }

    // ---------------------------------------------------------------------------
    // Constructor
    // ---------------------------------------------------------------------------

    internal PieceVerificationPipeline(
        IDiskBackend backend,
        PieceVerifier verifier,
        PieceMapper mapper,
        int totalPieces,
        int checkingMemUsageBlocks,  // In 16 KiB blocks
        int hashThreads)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _totalPieces = totalPieces;
        _hashThreads = Math.Max(1, hashThreads);

        _maxMemoryBytes = (long)checkingMemUsageBlocks * 16384;

        // Estimate an average piece size from first piece to size the channel.
        // Falls back to 256 KiB if totalPieces is 0 to avoid division by zero.
        long averagePieceSize = totalPieces > 0
            ? (long)mapper.GetPieceSize(0)
            : 256 * 1024;

        // Minimum channel depth = hashThreads * 2, matching libtorrent's tuning.
        _channelCapacity = (int)Math.Max(
            _maxMemoryBytes / Math.Max(averagePieceSize, 1),
            _hashThreads * 2);
    }

    // ---------------------------------------------------------------------------
    // Public entry point
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Reads and verifies every piece, returning a BitArray where bit i is true
    /// when piece i passed hash verification.
    /// </summary>
    public async Task<BitArray> VerifyAllPiecesAsync(
        IProgress<VerificationProgress>? progress,
        int startPiece,
        IReadOnlySet<int>? skipPieces,
        CancellationToken ct)
    {
        var bitfield = new BitArray(_totalPieces);

        var channel = Channel.CreateBounded<VerificationItem>(
            new BoundedChannelOptions(_channelCapacity)
            {
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

        // One logical producer — reads pieces sequentially and respects the
        // memory budget before renting buffers.
        var readerTask = Task.Run(() => ReaderLoopAsync(channel.Writer, startPiece, skipPieces, ct), ct);

        // N concurrent consumers — hash and update the shared BitArray.
        var hasherTasks = Enumerable.Range(0, _hashThreads)
            .Select(_ => Task.Run(() => HasherLoopAsync(channel.Reader, bitfield, progress, ct), ct))
            .ToArray();

        // Propagate reader exceptions; complete() is called inside finally.
        await readerTask.ConfigureAwait(false);
        await Task.WhenAll(hasherTasks).ConfigureAwait(false);

        return bitfield;
    }

    // ---------------------------------------------------------------------------
    // Reader loop
    // ---------------------------------------------------------------------------

    private async Task ReaderLoopAsync(
        ChannelWriter<VerificationItem> writer,
        int startPiece,
        IReadOnlySet<int>? skipPieces,
        CancellationToken ct)
    {
        try
        {
            for (int i = startPiece; i < _totalPieces; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (skipPieces is not null && skipPieces.Contains(i))
                    continue;

                var pieceSize = (int)_mapper.GetPieceSize(i);

                // Memory gate — spin-wait (with yield) until budget is available.
                while (Interlocked.Read(ref _currentMemoryBytes) + pieceSize > _maxMemoryBytes)
                    await Task.Delay(1, ct).ConfigureAwait(false);

                Interlocked.Add(ref _currentMemoryBytes, pieceSize);

                var buffer = ArrayPool<byte>.Shared.Rent(pieceSize);
                try
                {
                    var location = _mapper.MapPieceToFiles(i);
                    bool readFailed = false;

                    foreach (var segment in location.FileSegments)
                    {
                        var segmentLength = (int)segment.Length;
                        var pieceOffset = (int)segment.PieceOffset;

                        int totalRead = 0;
                        while (totalRead < segmentLength)
                        {
                            var read = await _backend.ReadAsync(
                                segment.FilePath,
                                segment.FileOffset + totalRead,
                                buffer.AsMemory(pieceOffset + totalRead, segmentLength - totalRead),
                                ct).ConfigureAwait(false);

                            if (read == 0)
                            {
                                readFailed = true;
                                break;
                            }
                            totalRead += read;
                        }

                        if (readFailed) break;
                    }

                    if (readFailed)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        Interlocked.Add(ref _currentMemoryBytes, -pieceSize);
                        continue; // Piece stays false in bitfield
                    }

                    // Transfer buffer ownership to the channel item; hasher returns it.
                    await writer.WriteAsync(new VerificationItem(i, buffer, pieceSize), ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Return buffer and memory budget on any error for this piece.
                    ArrayPool<byte>.Shared.Return(buffer);
                    Interlocked.Add(ref _currentMemoryBytes, -pieceSize);
                    // Piece stays false — continue to next piece.
                }
            }
        }
        finally
        {
            // Always signal completion so hashers drain and exit.
            writer.Complete();
        }
    }

    // ---------------------------------------------------------------------------
    // Hasher loop
    // ---------------------------------------------------------------------------

    private async Task HasherLoopAsync(
        ChannelReader<VerificationItem> reader,
        BitArray bitfield,
        IProgress<VerificationProgress>? progress,
        CancellationToken ct)
    {
        await foreach (var item in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                // ArrayPool.Rent may return a buffer larger than PieceSize.
                // The verifier hashes the entire array, so we must slice to
                // the actual piece length to avoid hashing trailing garbage.
                // This especially affects the last piece which is typically
                // smaller than the standard piece size.
                var data = item.Data.Length == item.PieceSize
                    ? item.Data
                    : item.Data[..item.PieceSize];

                var result = _verifier.VerifyPieceResult(item.PieceIndex, data);
                var valid = result == PieceVerifyResult.Valid;

                lock (bitfield)
                {
                    bitfield[item.PieceIndex] = valid;
                }

                progress?.Report(new VerificationProgress(item.PieceIndex, _totalPieces, valid, result));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(item.Data);
                Interlocked.Add(ref _currentMemoryBytes, -item.PieceSize);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Download-time verification (per-piece, data already in memory)
    // ---------------------------------------------------------------------------

    internal readonly struct DownloadVerificationJob
    {
        public int PieceIndex { get; init; }
        public byte[] Data { get; init; }
        public TaskCompletionSource<bool> Completion { get; init; }
    }

    private Channel<DownloadVerificationJob>? _downloadChannel;
    private Task[]? _downloadHasherTasks;
    private CancellationTokenSource? _downloadCts;
    private int _downloadStarted;

    /// <summary>
    /// Starts the download-time verification pipeline with N hasher workers.
    /// Independent of the bulk VerifyAllPiecesAsync channel — they can coexist.
    /// </summary>
    internal void StartDownloadVerification()
    {
        if (Interlocked.CompareExchange(ref _downloadStarted, 1, 0) != 0) return;

        _downloadCts = new CancellationTokenSource();
        _downloadChannel = Channel.CreateBounded<DownloadVerificationJob>(
            new BoundedChannelOptions(64)
            {
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        _downloadHasherTasks = Enumerable.Range(0, _hashThreads)
            .Select(_ => Task.Run(() => DownloadHasherLoopAsync(_downloadCts.Token)))
            .ToArray();
    }

    /// <summary>
    /// Stops the download-time verification pipeline and waits for all workers to drain.
    /// </summary>
    internal async Task StopDownloadVerificationAsync()
    {
        if (_downloadChannel == null) return;

        _downloadCts?.Cancel();
        _downloadChannel.Writer.TryComplete();
        if (_downloadHasherTasks != null)
            await Task.WhenAll(_downloadHasherTasks).ConfigureAwait(false);
        _downloadCts?.Dispose();
        _downloadChannel = null;
        _downloadHasherTasks = null;
        _downloadCts = null;
        Interlocked.Exchange(ref _downloadStarted, 0);
    }

    /// <summary>
    /// Enqueues a single piece for hash verification and returns the result asynchronously.
    /// Used during download to offload hashing from the download path.
    /// </summary>
    internal async Task<bool> VerifyPieceAsync(int pieceIndex, byte[] data)
    {
        if (_downloadChannel == null)
            throw new InvalidOperationException("Download verification not started. Call StartDownloadVerification first.");

        var job = new DownloadVerificationJob
        {
            PieceIndex = pieceIndex,
            Data = data,
            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        await _downloadChannel.Writer.WriteAsync(job).ConfigureAwait(false);
        return await job.Completion.Task.ConfigureAwait(false);
    }

    private async Task DownloadHasherLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _downloadChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var valid = _verifier.VerifyPiece(job.PieceIndex, job.Data);
                    job.Completion.TrySetResult(valid);
                }
                catch (Exception ex)
                {
                    job.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Drain any remaining items to prevent TCS leaks
            while (_downloadChannel!.Reader.TryRead(out var remaining))
                remaining.Completion.TrySetCanceled();
        }
    }
}
