using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Storage;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PieceIO.Backends;
using vTorrent.Core.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Buffers;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace vTorrent.Core.PieceIO
{
    public class PieceManager : IPieceManager, IDisposable
    {

        private string _basePath;  // Mutable for move_storage support
        private readonly TorrentInfo _torrentInfo;
        private PieceMapper _pieceMapper;  // Mutable for move_storage support
        private readonly PieceVerifier _pieceVerifier;
        private readonly IFileLockManager? _lockManager;  // Only used by old constructors for disposal; backend owns locking
        private readonly BitArray _bitfield;
        private readonly TorrentStatistics _diskStats;
        private readonly IDiskBackend _backend;
        private readonly bool _ownsBackend;  // True when PieceManager created the backend (old constructors)
        private bool _disposed;
        private bool _skipInitialVerification;
        private readonly IOptionsMonitor<DiskSettings>? _diskMonitor;
        private readonly ILogger<PieceManager> _logger;
        private readonly DiskFence _diskFence = new();  // For move_storage coordination

        private readonly Channel<PieceWriteJob> _writeChannel;
        private readonly Task[] _writerTasks;
        private readonly CancellationTokenSource _writerCts;
        private const int DiskWriterCount = 2;

        // Custom ArrayPool for large piece buffers (>1MB up to 16MB).
        // ArrayPool.Shared handles up to 1MB well; pieces larger than that hit the Large Object
        // Heap (>85KB threshold) on every allocation. Renting from this pool reuses the buffers
        // and eliminates LOH pressure for the temporary read buffer. The returned array is always
        // oversized, so callers must copy the valid bytes to a right-sized array before returning.
        private static readonly ArrayPool<byte> s_largePiecePool =
            ArrayPool<byte>.Create(maxArrayLength: 16 * 1024 * 1024, maxArraysPerBucket: 4);

        // DiskWriteThrottler integration — wired by engine/orchestrator after construction
        private DiskWriteThrottler? _throttler;

        // Download-time verification pipeline — offloads hash verification from download path
        private PieceVerificationPipeline? _downloadVerificationPipeline;

        /// <summary>
        /// Sets the disk write throttler for backpressure coordination.
        /// </summary>
        internal void SetThrottler(DiskWriteThrottler throttler) => _throttler = throttler;

        /// <summary>
        /// Exposes the PieceMapper for use by PeerSendBufferManager (block-level reads).
        /// </summary>
        internal PieceMapper PieceMapperInternal => _pieceMapper;

        /// <summary>
        /// Gets the existing download verification pipeline, or null if not created yet.
        /// </summary>
        internal PieceVerificationPipeline? DownloadVerificationPipeline => _downloadVerificationPipeline;

        /// <summary>
        /// Gets or creates the download verification pipeline for offloading hash verification
        /// from the download path. Uses the same PieceVerifier and hash thread count as bulk verification.
        /// </summary>
        internal PieceVerificationPipeline GetOrCreateDownloadVerificationPipeline(int hashThreads)
        {
            if (_downloadVerificationPipeline != null)
                return _downloadVerificationPipeline;

            _downloadVerificationPipeline = new PieceVerificationPipeline(
                _backend, _pieceVerifier, _pieceMapper,
                TotalPieces, checkingMemUsageBlocks: 128, hashThreads);

            return _downloadVerificationPipeline;
        }

        public int TotalPieces => _torrentInfo.Pieces.Count;
        public long PieceSize => _torrentInfo.PieceLength;

        /// <summary>
        /// Disk I/O statistics tracker. Can be queried for monitoring.
        /// </summary>
        public TorrentStatistics DiskStats => _diskStats;

        public PieceManager(string basePath, TorrentInfo torrentInfo) : this(basePath, torrentInfo, new FileLockManager(), (TorrentStatistics?)null, false)
        {
        }

        public PieceManager(string basePath, TorrentInfo torrentInfo, IFileLockManager lockManager) : this(basePath, torrentInfo, lockManager, null, false)
        {
        }

        public PieceManager(string basePath, TorrentInfo torrentInfo, IFileLockManager lockManager, TorrentStatistics? diskStats) : this(basePath, torrentInfo, lockManager, diskStats, false)
        {
        }

        /// <summary>
        /// Creates a new PieceManager with optional deferred initialization.
        /// Backward-compatible constructor — creates a PosixDiskBackend internally.
        /// </summary>
        /// <param name="basePath">Base path for torrent files</param>
        /// <param name="torrentInfo">Torrent metadata</param>
        /// <param name="lockManager">File lock manager</param>
        /// <param name="diskStats">Statistics tracker with disk I/O tracking</param>
        /// <param name="skipInitialVerification">If true, skips initial piece verification (for resume data scenarios)</param>
        public PieceManager(string basePath, TorrentInfo torrentInfo, IFileLockManager lockManager, TorrentStatistics? diskStats, bool skipInitialVerification)
        {
            _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
            _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
            _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
            _diskStats = diskStats ?? new TorrentStatistics();
            _skipInitialVerification = skipInitialVerification;
            _diskMonitor = null;
            _logger = NullLogger<PieceManager>.Instance;

            _pieceMapper = new PieceMapper(basePath, torrentInfo);
            _pieceVerifier = new PieceVerifier(torrentInfo);
            _bitfield = new BitArray(_torrentInfo.Pieces.Count);

            // Create a PosixDiskBackend wrapping SparseFileManager and FileLockManager
            // so all I/O goes through IDiskBackend uniformly.
            var sparseFileManager = new SparseFileManager(basePath, torrentInfo);
            var defaultSettings = new DiskSettings();
            _backend = new PosixDiskBackend(sparseFileManager, lockManager, defaultSettings, null, NullLogger.Instance);
            _ownsBackend = true;

            // Write queue capacity set to 5000 to handle high-speed downloads.
            // This larger buffer helps prevent backpressure when disk I/O is temporarily
            // slower than network throughput, improving overall download performance.
            _writeChannel = Channel.CreateBounded<PieceWriteJob>(new BoundedChannelOptions(5000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
            _writerCts = new CancellationTokenSource();
            _writerTasks = new Task[DiskWriterCount];
            for (int i = 0; i < DiskWriterCount; i++)
            {
                _writerTasks[i] = Task.Factory.StartNew(
                    () => DiskWriterLoopAsync(_writerCts.Token),
                    TaskCreationOptions.LongRunning).Unwrap();
            }

            // Only run initial verification if not skipping (resume data will be applied externally)
            if (!skipInitialVerification)
            {
                InitializeBitfield();
            }
        }

        /// <summary>
        /// Creates a new PieceManager backed by an externally-provided IDiskBackend.
        /// The backend is NOT owned by this PieceManager — caller is responsible for its lifetime.
        /// </summary>
        /// <param name="basePath">Base path for torrent files</param>
        /// <param name="torrentInfo">Torrent metadata</param>
        /// <param name="diskBackend">Externally-provided disk backend (not owned by PieceManager)</param>
        /// <param name="diskStats">Statistics tracker with disk I/O tracking</param>
        /// <param name="skipInitialVerification">If true, skips initial piece verification (for resume data scenarios)</param>
        public PieceManager(
            string basePath,
            TorrentInfo torrentInfo,
            IDiskBackend diskBackend,
            TorrentStatistics? diskStats = null,
            bool skipInitialVerification = true,
            IOptionsMonitor<DiskSettings>? diskMonitor = null,
            ILogger<PieceManager>? logger = null)
        {
            _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
            _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
            _backend = diskBackend ?? throw new ArgumentNullException(nameof(diskBackend));
            _ownsBackend = false;
            _lockManager = null;  // Backend owns its own locking
            _diskStats = diskStats ?? new TorrentStatistics();
            _skipInitialVerification = skipInitialVerification;
            _diskMonitor = diskMonitor;
            _logger = logger ?? NullLogger<PieceManager>.Instance;

            if (_diskMonitor?.CurrentValue.DisableHashChecks == true)
                _logger.LogWarning("DisableHashChecks is enabled — piece data will NOT be verified. Debug-only setting.");

            _pieceMapper = new PieceMapper(basePath, torrentInfo);
            _pieceVerifier = new PieceVerifier(torrentInfo);
            _bitfield = new BitArray(_torrentInfo.Pieces.Count);

            // Write queue capacity set to 5000 to handle high-speed downloads.
            _writeChannel = Channel.CreateBounded<PieceWriteJob>(new BoundedChannelOptions(5000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
            _writerCts = new CancellationTokenSource();
            _writerTasks = new Task[DiskWriterCount];
            for (int i = 0; i < DiskWriterCount; i++)
            {
                _writerTasks[i] = Task.Factory.StartNew(
                    () => DiskWriterLoopAsync(_writerCts.Token),
                    TaskCreationOptions.LongRunning).Unwrap();
            }

            // Only run initial verification if not skipping (resume data will be applied externally)
            if (!skipInitialVerification)
            {
                InitializeBitfield();
            }
        }

        private async Task DiskWriterLoopAsync(CancellationToken cancellationToken)
        {
            await foreach (var job in _writeChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var result = await WritePieceInternalAsync(job.PieceIndex, job.Data, cancellationToken).ConfigureAwait(false);
                    job.Completion.TrySetResult(result);

                    // Notify throttler after successful write so backpressure can be released
                    _throttler?.OnWriteCompleted(job.Data.Length);
                }
                catch (Exception ex)
                {
                    job.Completion.TrySetResult(PieceWriteResult.Failure(job.PieceIndex, PieceWriteError.IoError, ex.Message));
                }
                finally
                {
                    // CRITICAL: Decrement pending count AFTER write completes (success or failure)
                    // This ensures DiskFence waits for actual I/O completion, not just job dequeue
                    _diskStats.DecrementPendingWrites();
                }
            }
        }


        private async Task<PieceWriteResult> WritePieceInternalAsync(int pieceIndex, byte[] data, CancellationToken cancellationToken)
        {
            try
            {
                var location = _pieceMapper.MapPieceToFiles(pieceIndex);
                long totalBytesWritten = 0;

                foreach (var segment in location.FileSegments)
                {
                    // Backend handles its own file locking — no explicit lock needed here
                    await WriteAndFlushSegmentAsync(segment, data, cancellationToken).ConfigureAwait(false);
                    totalBytesWritten += segment.Length;
                }

                // Record disk statistics
                _diskStats.RecordDiskWrite(totalBytesWritten);

                lock (_bitfield)
                {
                    _bitfield[pieceIndex] = true;
                }

                return PieceWriteResult.Success(pieceIndex, totalBytesWritten, true);
            }
            catch (Exception ex)
            {
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.IoError, ex.Message);
            }
        }

        public BitArray GetBitfield()
        {
            if(_disposed)
            {
                throw new ObjectDisposedException(nameof(_bitfield));
            }

            lock (_bitfield)
            {
                return new BitArray(_bitfield);
            }
        }

        /// <summary>
        /// Initializes the bitfield from resume data, avoiding expensive disk verification.
        /// This is the key method for fast resume - allows restoring piece state without
        /// reading and hashing every piece from disk.
        /// </summary>
        /// <param name="resumeBitfield">The bitfield from resume data (pieces that were previously verified)</param>
        public void InitializeFromResumeBitfield(BitArray resumeBitfield)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PieceManager));

            if (resumeBitfield == null)
                throw new ArgumentNullException(nameof(resumeBitfield));

            lock (_bitfield)
            {
                int count = Math.Min(_bitfield.Length, resumeBitfield.Length);
                for (int i = 0; i < count; i++)
                {
                    _bitfield[i] = resumeBitfield[i];
                }
            }
        }

        /// <summary>
        /// Sets a specific piece's completion state without verification.
        /// Used for resume data restoration.
        /// </summary>
        public void SetPieceComplete(int pieceIndex, bool complete)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PieceManager));

            if (pieceIndex < 0 || pieceIndex >= TotalPieces)
                throw new ArgumentOutOfRangeException(nameof(pieceIndex));

            lock (_bitfield)
            {
                _bitfield[pieceIndex] = complete;
            }
        }

        /// <summary>
        /// Gets whether a specific piece is marked as complete.
        /// </summary>
        public bool IsPieceComplete(int pieceIndex)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PieceManager));

            if (pieceIndex < 0 || pieceIndex >= TotalPieces)
                return false;

            lock (_bitfield)
            {
                return _bitfield[pieceIndex];
            }
        }

        /// <summary>
        /// Gets the count of completed pieces.
        /// </summary>
        public int CompletedPieceCount
        {
            get
            {
                lock (_bitfield)
                {
                    int count = 0;
                    for (int i = 0; i < _bitfield.Length; i++)
                    {
                        if (_bitfield[i]) count++;
                    }
                    return count;
                }
            }
        }

        /// <summary>
        /// Check if a piece has been verified and is marked as complete.
        /// Following libtorrent's have_piece pattern - returns true if the piece
        /// has been downloaded, verified, and is available for upload.
        /// </summary>
        public bool HasValidPiece(int pieceIndex)
        {
            if (_disposed)
                return false;

            if (pieceIndex < 0 || pieceIndex >= TotalPieces)
                return false;

            lock (_bitfield)
            {
                return _bitfield[pieceIndex];
            }
        }

        public PieceReadResult ReadPiece(int pieceIndex)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PieceManager));

            // Validate input
            if (pieceIndex < 0 || pieceIndex >= TotalPieces)
                return PieceReadResult.Failure(pieceIndex, PieceReadError.InvalidPieceIndex,
                    $"Piece index {pieceIndex} is out of range (0-{TotalPieces - 1})");

            try
            {
                // Map piece to file location(s)
                var location = _pieceMapper.MapPieceToFiles(pieceIndex);
                int pieceSize = (int)location.PieceSize;

                // Rent a temporary buffer from the pool to hold the raw disk read.
                // The rented array may be larger than pieceSize, so we must not expose it
                // directly to callers. After reading we copy the valid bytes into a
                // right-sized array for the return value and then return the rented buffer.
                byte[] rentedBuffer = s_largePiecePool.Rent(pieceSize);
                try
                {
                    long totalBytesRead = 0;

                    // Read each segment — backend handles its own locking
                    foreach (var segment in location.FileSegments)
                    {
                        ReadSegment(segment, rentedBuffer);
                        totalBytesRead += segment.Length;
                    }

                    // Record disk statistics
                    _diskStats.RecordDiskRead(totalBytesRead);

                    // Copy valid bytes to a right-sized array before verification and return.
                    // VerifyPiece uses data.Length internally, so it must receive the exact size.
                    var pieceData = new byte[pieceSize];
                    rentedBuffer.AsSpan(0, pieceSize).CopyTo(pieceData);

                    // Verify the data
                    bool hashValid = _pieceVerifier.VerifyPiece(pieceIndex, pieceData);
                    _diskStats.RecordDiskHashVerification(hashValid);

                    if (!hashValid)
                        return PieceReadResult.Failure(pieceIndex, PieceReadError.HashMismatch,
                            "Piece hash verification failed");

                    return PieceReadResult.Success(pieceIndex, pieceData, true);
                }
                finally
                {
                    s_largePiecePool.Return(rentedBuffer);
                }
            }
            catch (FileNotFoundException ex)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.FileNotFound,
                    $"File not found: {ex.FileName ?? ex.Message}");
            }
            catch (UnauthorizedAccessException)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.PermissionDenied,
                    "Permission denied reading from file");
            }
            catch (IOException ex)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.IoError,
                    $"I/O error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.UnknownError,
                    $"Unexpected error: {ex.Message}");
            }
        }

        public async Task<PieceReadResult> ReadPieceAsync(int pieceIndex, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PieceManager));

            // Validate input
            if (pieceIndex < 0 || pieceIndex >= TotalPieces)
                return PieceReadResult.Failure(pieceIndex, PieceReadError.InvalidPieceIndex,
                    $"Piece index {pieceIndex} is out of range (0-{TotalPieces - 1})");

            _diskStats.IncrementPendingReads();
            byte[]? rentedBuffer = null;
            try
            {
                // Map piece to file location(s)
                var location = _pieceMapper.MapPieceToFiles(pieceIndex);
                int pieceSize = (int)location.PieceSize;

                // Rent a temporary buffer from the pool to hold the raw disk read.
                // The rented array may be larger than pieceSize, so we must not expose it
                // directly to callers. After reading we copy the valid bytes into a
                // right-sized array for the return value and then return the rented buffer.
                rentedBuffer = s_largePiecePool.Rent(pieceSize);
                long totalBytesRead = 0;

                // Read each segment — backend handles its own locking
                foreach (var segment in location.FileSegments)
                {
                    await ReadSegmentAsync(segment, rentedBuffer, cancellationToken).ConfigureAwait(false);
                    totalBytesRead += segment.Length;
                }

                // Record disk statistics
                _diskStats.RecordDiskRead(totalBytesRead);

                // Copy valid bytes to a right-sized array before verification and return.
                // VerifyPiece uses data.Length internally, so it must receive the exact size.
                var pieceData = new byte[pieceSize];
                rentedBuffer.AsSpan(0, pieceSize).CopyTo(pieceData);

                // Verify the data
                bool hashValid = _pieceVerifier.VerifyPiece(pieceIndex, pieceData);
                _diskStats.RecordDiskHashVerification(hashValid);

                if (!hashValid)
                    return PieceReadResult.Failure(pieceIndex, PieceReadError.HashMismatch,
                        "Piece hash verification failed");

                return PieceReadResult.Success(pieceIndex, pieceData, true);
            }
            catch (FileNotFoundException ex)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.FileNotFound,
                    $"File not found: {ex.FileName ?? ex.Message}");
            }
            catch (UnauthorizedAccessException)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.PermissionDenied,
                    "Permission denied reading from file");
            }
            catch (IOException ex)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.IoError,
                    $"I/O error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.UnknownError,
                    $"Unexpected error: {ex.Message}");
            }
            finally
            {
                if (rentedBuffer != null)
                    s_largePiecePool.Return(rentedBuffer);
                _diskStats.DecrementPendingReads();
            }
        }

        public async Task<PieceReadResult> ReadBlockAsync(int pieceIndex, int offset, int length, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PieceManager));

            if (pieceIndex < 0 || pieceIndex >= TotalPieces)
                return PieceReadResult.Failure(pieceIndex, PieceReadError.InvalidPieceIndex,
                    $"Piece index {pieceIndex} is out of range (0-{TotalPieces - 1})");

            var pieceSize = (int)_pieceMapper.GetPieceSize(pieceIndex);
            if (offset < 0 || length <= 0 || offset + length > pieceSize)
                return PieceReadResult.Failure(pieceIndex, PieceReadError.InvalidPieceIndex,
                    $"Block range [{offset}, {offset + length}) is out of piece bounds [0, {pieceSize})");

            _diskStats.IncrementPendingReads();
            byte[]? rentedBuffer = null;
            try
            {
                var location = _pieceMapper.MapPieceToFiles(pieceIndex);

                // Rent a buffer sized exactly to the requested block.
                rentedBuffer = ArrayPool<byte>.Shared.Rent(length);

                int blockEnd = offset + length;

                foreach (var segment in location.FileSegments)
                {
                    // Segment range within the piece: [segment.PieceOffset, segment.PieceOffset + segment.Length)
                    long segPieceStart = segment.PieceOffset;
                    long segPieceEnd   = segment.PieceOffset + segment.Length;

                    // Overlap with the requested block range [offset, blockEnd)
                    long overlapStart = Math.Max(offset, segPieceStart);
                    long overlapEnd   = Math.Min(blockEnd, segPieceEnd);

                    if (overlapStart >= overlapEnd)
                        continue;

                    long readLength     = overlapEnd - overlapStart;
                    long fileReadOffset = segment.FileOffset + (overlapStart - segPieceStart);
                    int  bufferOffset   = (int)(overlapStart - offset);

                    int bytesRead = await _backend.ReadAsync(
                        segment.FilePath,
                        fileReadOffset,
                        rentedBuffer.AsMemory(bufferOffset, (int)readLength),
                        cancellationToken).ConfigureAwait(false);

                    if (bytesRead < readLength)
                        throw new IOException($"Unexpected EOF reading {segment.FilePath}");
                }

                _diskStats.RecordDiskRead(length);

                var blockData = new byte[length];
                rentedBuffer.AsSpan(0, length).CopyTo(blockData);

                return PieceReadResult.Success(pieceIndex, blockData, hashVerified: false);
            }
            catch (FileNotFoundException ex)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.FileNotFound,
                    $"File not found: {ex.FileName ?? ex.Message}");
            }
            catch (UnauthorizedAccessException)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.PermissionDenied,
                    "Permission denied reading from file");
            }
            catch (IOException ex)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.IoError,
                    $"I/O error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return PieceReadResult.Failure(pieceIndex, PieceReadError.UnknownError,
                    $"Unexpected error: {ex.Message}");
            }
            finally
            {
                if (rentedBuffer != null)
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                _diskStats.DecrementPendingReads();
            }
        }

        public bool VerifyPiece(int pieceIndex, byte[] data)
        {
            if(_disposed)
            {
                throw new ObjectDisposedException(nameof(PieceManager));
            }

            if (_diskMonitor?.CurrentValue.DisableHashChecks == true)
            {
                _logger.LogTrace("Hash check skipped for piece {Piece} — DisableHashChecks is enabled", pieceIndex);
                return true;
            }

            bool result = _pieceVerifier.VerifyPiece(pieceIndex, data);
            _diskStats.RecordDiskHashVerification(result);
            return result;
        }

        public PieceWriteResult WritePiece(int pieceIndex, byte[] data)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PieceManager));
            }

            if (pieceIndex < 0 || pieceIndex >= _torrentInfo.Pieces.Count)
            {
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.InvalidPieceIndex, $"Piece index {pieceIndex} is out of range (0-{TotalPieces - 1})");
            }

            if (data == null)
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.InvalidData, "Piece data is null");

            var expectedSize = _pieceMapper.GetPieceSize(pieceIndex);
            if (data.Length != expectedSize)
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.InvalidDataSize, $"Piece data size {data.Length} doesn't match expected size {expectedSize}");

            try
            {
                bool hashValid = _pieceVerifier.VerifyPiece(pieceIndex, data);
                _diskStats.RecordDiskHashVerification(hashValid);

                if(!hashValid)
                {
                    return PieceWriteResult.Failure(pieceIndex, PieceWriteError.HashMismatch, "Piece hash verification failed");
                }

                var location = _pieceMapper.MapPieceToFiles(pieceIndex);
                long totalBytesWritten = 0;

                foreach(var segment in location.FileSegments)
                {
                    // Backend handles its own file locking
                    WriteSegment(segment, data);
                    totalBytesWritten += segment.Length;
                }

                // Record disk statistics
                _diskStats.RecordDiskWrite(totalBytesWritten);

                lock (_bitfield)
                {
                    _bitfield[pieceIndex] = true;
                }

                return PieceWriteResult.Success(pieceIndex, totalBytesWritten, true);
            }
            catch (FileNotFoundException)
            {
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.FileNotFound,
                    "One or more files not found. Files must be allocated before writing pieces.");
            }
            catch (UnauthorizedAccessException)
            {
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.PermissionDenied,
                    "Permission denied writing to file");
            }
            catch (IOException ex)
            {
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.IoError,
                    $"I/O error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.UnknownError,
                    $"Unexpected error: {ex.Message}");
            }

        }

        /// <summary>
        /// Writes a piece to disk asynchronously.
        /// </summary>
        /// <param name="pieceIndex">The index of the piece to write.</param>
        /// <param name="data">The piece data to write.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="skipVerification">When true, skips hash verification. Use this when the caller
        /// (e.g., DownloadCoordinator) has already verified the hash to avoid redundant computation.</param>
        /// <returns>The result of the write operation.</returns>
        public async Task<PieceWriteResult> WritePieceAsync(int pieceIndex, byte[] data, CancellationToken cancellationToken = default, bool skipVerification = false)
        {
            if(_disposed)
            {
                throw new ObjectDisposedException(nameof(PieceManager));
            }

            if(pieceIndex < 0 || pieceIndex >= TotalPieces)
            {
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.InvalidPieceIndex, $"Piece index {pieceIndex} is out of range (0-{TotalPieces - 1})");

            }

            if(data == null)
            {
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.InvalidData, "Piece data is null");
            }

            var expectedSize = _pieceMapper.GetPieceSize(pieceIndex);
            if (data.Length != expectedSize)
            {
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.InvalidDataSize, $"Piece data size {data.Length} doesn't match expected size {expectedSize}");
            }

            // Skip hash verification if already verified by caller (e.g., DownloadCoordinator).
            // This avoids redundant SHA1 computation which is expensive for large pieces.
            if (!skipVerification)
            {
                bool hashValid = _pieceVerifier.VerifyPiece(pieceIndex, data);
                _diskStats.RecordDiskHashVerification(hashValid);

                if(!hashValid)
                    return PieceWriteResult.Failure(pieceIndex, PieceWriteError.HashMismatch, "Piece hash verification failed");
            }

            var job = new PieceWriteJob
            {
                PieceIndex = pieceIndex,
                Data = data,
                Completion = new TaskCompletionSource<PieceWriteResult>()
            };

            _diskStats.IncrementPendingWrites();
            await _writeChannel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
            return await job.Completion.Task.ConfigureAwait(false);

        }

        private void WriteSegment(FileSegment segment, byte[] pieceData)
        {
            // Delegate to backend (sync over async — acceptable for backward-compat sync path)
            _backend.WriteAsync(segment.FilePath, segment.FileOffset,
                pieceData.AsMemory((int)segment.PieceOffset, (int)segment.Length)).AsTask().GetAwaiter().GetResult();
        }

        private async Task WriteSegmentAsync(FileSegment segment, byte[] pieceData, CancellationToken cancellationToken)
        {
            // Delegate to backend — backend handles sparse allocation and locking internally
            await _backend.WriteAsync(segment.FilePath, segment.FileOffset,
                pieceData.AsMemory((int)segment.PieceOffset, (int)segment.Length), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes a segment and flushes to disk. Used by WritePieceInternalAsync to ensure
        /// data actually reaches the physical disk. Without flush, Windows write-back caching
        /// silently accepts writes even when disk is full — data is lost when the OS cache
        /// is evicted, causing pieces to appear completed but missing on recheck.
        /// Flushing per-piece (not per-block) limits performance impact while ensuring
        /// disk-full errors surface as IOExceptions immediately.
        /// </summary>
        private async Task WriteAndFlushSegmentAsync(FileSegment segment, byte[] pieceData, CancellationToken cancellationToken)
        {
            await _backend.WriteAsync(segment.FilePath, segment.FileOffset,
                pieceData.AsMemory((int)segment.PieceOffset, (int)segment.Length), cancellationToken).ConfigureAwait(false);
            await _backend.FlushAsync(segment.FilePath, cancellationToken).ConfigureAwait(false);
        }

        private void ReadSegment(FileSegment segment, byte[] pieceData)
        {
            // Delegate to backend (sync over async — acceptable for backward-compat sync path)
            int totalRead = _backend.ReadAsync(segment.FilePath, segment.FileOffset,
                pieceData.AsMemory((int)segment.PieceOffset, (int)segment.Length)).AsTask().GetAwaiter().GetResult();
            if (totalRead < segment.Length)
            {
                throw new IOException($"Unexpected EOF reading {segment.FilePath}");
            }
        }

        private async Task ReadSegmentAsync(FileSegment segment, byte[] pieceData, CancellationToken cancellationToken = default)
        {
            int totalRead = await _backend.ReadAsync(segment.FilePath, segment.FileOffset,
                pieceData.AsMemory((int)segment.PieceOffset, (int)segment.Length), cancellationToken).ConfigureAwait(false);
            if (totalRead < segment.Length)
            {
                throw new IOException($"Unexpected EOF reading {segment.FilePath}");
            }
        }

        private void InitializeBitfield()
        {
            for (int i = 0; i < TotalPieces; i++)
            {
                try
                {
                    var readResult = ReadPiece(i);
                    if (readResult.IsSuccess && readResult.HashVerified)
                    {
                        _bitfield[i] = true;
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't propagate - piece verification failure is expected for incomplete files
                    System.Diagnostics.Debug.WriteLine($"Piece {i} verification failed during initialization: {ex.Message}");
                    _bitfield[i] = false;
                }
            }
        }

        /// <summary>
        /// Asynchronous bitfield initialization using the PieceVerificationPipeline.
        /// Reads and hashes all pieces concurrently via a producer-consumer pipeline.
        /// </summary>
        /// <param name="checkingMemUsageBlocks">Memory budget in 16 KiB blocks for the pipeline.</param>
        /// <param name="hashThreads">Number of concurrent hash threads.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task InitializeBitfieldAsync(int checkingMemUsageBlocks, int hashThreads, CancellationToken ct = default)
        {
            var pipeline = new PieceVerificationPipeline(
                _backend, _pieceVerifier, _pieceMapper,
                TotalPieces, checkingMemUsageBlocks, hashThreads);

            var verifiedBitfield = await pipeline.VerifyAllPiecesAsync(progress: null, startPiece: 0, skipPieces: null, ct).ConfigureAwait(false);

            lock (_bitfield)
            {
                for (int i = 0; i < _bitfield.Length && i < verifiedBitfield.Length; i++)
                {
                    _bitfield[i] = verifiedBitfield[i];
                }
            }
        }

        /// <summary>
        /// Flush file handles covering a specific piece to ensure data is visible to external readers
        /// (e.g., video players reading ahead during streaming).
        /// </summary>
        public async Task FlushPieceAsync(int pieceIndex, CancellationToken cancellationToken = default)
        {
            var location = _pieceMapper.MapPieceToFiles(pieceIndex);
            foreach (var segment in location.FileSegments)
            {
                try
                {
                    await _backend.FlushAsync(segment.FilePath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort flush — file may have been closed/moved
                }
            }
        }

        public void SetSequentialAccessHint(bool sequential)
        {
            // Sequential access hint is managed internally by the backend's handle cache.
            // No explicit action needed at the PieceManager level.
        }

        #region Move Storage Support (libtorrent-style)

        /// <summary>
        /// Whether the disk fence is currently raised.
        /// </summary>
        public bool IsFenced => _diskFence.IsFenced;

        /// <summary>
        /// Raises the disk fence - blocks new I/O, drains pending writes, closes file handles.
        /// Call before move_storage operation.
        /// </summary>
        public async Task<bool> RaiseDiskFenceAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PieceManager));

            // 1. Raise the fence to block new I/O operations
            var fenceRaised = await _diskFence.RaiseFenceAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!fenceRaised)
                return false;

            // 2. Wait for write queue to drain
            var drainDeadline = DateTime.UtcNow + timeout;
            while (_diskStats.DiskPendingWrites > 0 && DateTime.UtcNow < drainDeadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            // 3. Close all file handles via backend
            await _backend.CloseAllAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Updates the base path after files have been moved.
        /// Must call RaiseDiskFenceAsync first.
        /// </summary>
        public void UpdateBasePath(string newBasePath)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PieceManager));

            if (!_diskFence.IsFenced)
                throw new InvalidOperationException("Fence must be raised before updating base path");

            if (string.IsNullOrWhiteSpace(newBasePath))
                throw new ArgumentNullException(nameof(newBasePath));

            // Update base path
            _basePath = newBasePath;

            // Recreate PieceMapper with new base path
            _pieceMapper = new PieceMapper(newBasePath, _torrentInfo);

            // Note: The backend's internal SparseFileManager is not updated here.
            // After move_storage, the backend will create files at the new paths as needed
            // since PieceMapper now resolves segment paths to the new base directory.
        }

        /// <summary>
        /// Lowers the disk fence - allows I/O to resume.
        /// Call after move_storage operation completes.
        /// </summary>
        public void LowerDiskFence()
        {
            if (_disposed)
                return;

            // Backend handles will be lazily reopened on next I/O operation.
            // Lower the fence to allow new I/O.
            _diskFence.LowerFence();
        }

        /// <summary>
        /// Releases all write file handles, keeping only read handles open.
        /// Call this when transitioning to seeding (100% complete) to allow
        /// external programs to execute downloaded files (especially .exe files).
        /// </summary>
        public async ValueTask ReleaseWriteHandlesAsync()
        {
            if (_disposed)
                return;

            // Close all handles — the backend's cache will lazily reopen read handles as needed.
            // This releases write locks so external programs can access downloaded files.
            await _backend.CloseAllAsync(CancellationToken.None).ConfigureAwait(false);
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Stop the writer
            _writeChannel.Writer.Complete();
            _writerCts.Cancel();

            try
            {
                Task.WhenAll(_writerTasks).Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException ex)
            {
                // Writer tasks may have been cancelled or faulted during shutdown
                System.Diagnostics.Debug.WriteLine($"PieceManager writer task exception during dispose: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PieceManager dispose error: {ex.Message}");
            }

            _writerCts.Dispose();

            // Stop download verification pipeline if active
            if (_downloadVerificationPipeline != null)
            {
                try
                {
                    _downloadVerificationPipeline.StopDownloadVerificationAsync()
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Download verification pipeline stop error: {ex.Message}");
                }
                _downloadVerificationPipeline = null;
            }

            _lockManager?.Dispose();

            // Only dispose the backend if we created it (old constructor path).
            // When backend is externally provided, the engine/orchestrator owns its lifetime.
            if (_ownsBackend)
            {
                _backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            // _backend is owned by engine when not _ownsBackend, not disposed here

            _diskFence?.Dispose();
        }
    }
}
