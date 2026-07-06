using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Storage;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PieceIO;

namespace vTorrent.Core.PieceIO.Backends
{
    /// <summary>
    /// An <see cref="IDiskBackend"/> decorator that routes I/O for skipped-file segments
    /// to a <see cref="PartFile"/>, mirroring libtorrent's part_file behaviour.
    ///
    /// When a file's priority is <see cref="FilePriority.Skip"/> and no real file exists on
    /// disk for it, all reads/writes are redirected to the .parts file so that pieces
    /// spanning the skipped file can still be stored and later exported when the priority is
    /// raised back to a wanted level.
    /// </summary>
    internal sealed class PartFileAwareDiskBackend : IDiskBackend
    {
        // ------------------------------------------------------------------ //
        //  Fields
        // ------------------------------------------------------------------ //

        private readonly IDiskBackend _inner;
        private PieceMapper _pieceMapper;
        private FileOffsetToPieceMapper _reverseMapper;
        private readonly TorrentInfo _torrentInfo;
        private string _directory;
        private readonly string _infoHashHex;
        private readonly string _partFileName;
        private readonly ILogger _logger;

        private FilePriority[] _filePriorities;
        private bool[] _usePartfile;
        private Dictionary<string, int> _pathToFileIndex;

        // Lazy-created part file.
        private PartFile? _partFile;

        // Fence state — used to quiesce in-flight I/O before priority transitions.
        private readonly SemaphoreSlim _fenceLock = new SemaphoreSlim(1, 1);
        private volatile bool _fenceActive;
        private int _activeIoCount;

        // ------------------------------------------------------------------ //
        //  Constructor
        // ------------------------------------------------------------------ //

        public PartFileAwareDiskBackend(
            IDiskBackend inner,
            PieceMapper pieceMapper,
            TorrentInfo torrentInfo,
            string directory,
            string infoHashHex,
            FilePriority[] initialPriorities,
            ILogger logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _pieceMapper = pieceMapper ?? throw new ArgumentNullException(nameof(pieceMapper));
            _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _infoHashHex = infoHashHex ?? throw new ArgumentNullException(nameof(infoHashHex));
            _partFileName = $".{infoHashHex}.parts";
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (initialPriorities == null) throw new ArgumentNullException(nameof(initialPriorities));

            _filePriorities = (FilePriority[])initialPriorities.Clone();
            _usePartfile = new bool[initialPriorities.Length];
            // Default all files to use the partfile (they may not exist yet).
            for (int i = 0; i < _usePartfile.Length; i++)
                _usePartfile[i] = true;

            _reverseMapper = new FileOffsetToPieceMapper(pieceMapper);
            _pathToFileIndex = BuildPathToFileIndex(pieceMapper);
        }

        // ------------------------------------------------------------------ //
        //  IDiskBackend — ReadAsync
        // ------------------------------------------------------------------ //

        public async ValueTask<int> ReadAsync(
            string filePath,
            long fileOffset,
            Memory<byte> buffer,
            CancellationToken ct = default)
        {
            await EnterIoAsync().ConfigureAwait(false);
            try
            {
                if (ShouldRouteToPartFile(filePath, out int fileIndex))
                {
                    var pf = NeedPartFile();
                    var (pieceIndex, offsetInPiece) = _reverseMapper.Map(fileIndex, fileOffset);
                    return await pf.ReadAsync(buffer, pieceIndex, offsetInPiece).ConfigureAwait(false);
                }

                return await _inner.ReadAsync(filePath, fileOffset, buffer, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeIoCount);
            }
        }

        // ------------------------------------------------------------------ //
        //  IDiskBackend — WriteAsync
        // ------------------------------------------------------------------ //

        public async ValueTask WriteAsync(
            string filePath,
            long fileOffset,
            ReadOnlyMemory<byte> buffer,
            CancellationToken ct = default)
        {
            await EnterIoAsync().ConfigureAwait(false);
            try
            {
                if (ShouldRouteToPartFile(filePath, out int fileIndex))
                {
                    var pf = NeedPartFile();
                    var (pieceIndex, offsetInPiece) = _reverseMapper.Map(fileIndex, fileOffset);
                    await pf.WriteAsync(buffer, pieceIndex, offsetInPiece).ConfigureAwait(false);
                    return;
                }

                await _inner.WriteAsync(filePath, fileOffset, buffer, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeIoCount);
            }
        }

        // ------------------------------------------------------------------ //
        //  IDiskBackend — EnsureAllocatedAsync
        // ------------------------------------------------------------------ //

        public ValueTask EnsureAllocatedAsync(
            string filePath,
            long requiredSize,
            CancellationToken ct = default)
        {
            // Skip allocation for files that are routed to the partfile.
            if (ShouldRouteToPartFile(filePath, out _))
                return ValueTask.CompletedTask;

            return _inner.EnsureAllocatedAsync(filePath, requiredSize, ct);
        }

        // ------------------------------------------------------------------ //
        //  IDiskBackend — pass-throughs
        // ------------------------------------------------------------------ //

        public ValueTask FlushAsync(string filePath, CancellationToken ct = default)
            => _inner.FlushAsync(filePath, ct);

        public ValueTask CloseFileAsync(string filePath, CancellationToken ct = default)
            => _inner.CloseFileAsync(filePath, ct);

        public ValueTask CloseAllAsync(CancellationToken ct = default)
            => _inner.CloseAllAsync(ct);

        public DiskBackendStats GetStats()
            => _inner.GetStats();

        // ------------------------------------------------------------------ //
        //  Priority change API
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Applies a bulk priority change. Files transitioning from Skip→Normal are
        /// exported from the part file; files transitioning from Normal→Skip are checked
        /// for on-disk presence to decide whether the partfile should be used.
        /// </summary>
        public async Task OnFilePrioritiesChangedAsync(FilePriority[] newPriorities)
        {
            if (newPriorities == null) throw new ArgumentNullException(nameof(newPriorities));

            await AcquireFenceAsync().ConfigureAwait(false);
            try
            {
                var mappings = _pieceMapper.FileMappings;

                for (int i = 0; i < newPriorities.Length && i < _filePriorities.Length; i++)
                {
                    var oldPriority = _filePriorities[i];
                    var newPriority = newPriorities[i];

                    if (oldPriority == newPriority)
                        continue;

                    if (oldPriority == FilePriority.Skip && newPriority != FilePriority.Skip)
                    {
                        // Skip → Normal: export data from partfile to inner backend.
                        await ExportFileFromPartFileAsync(i, mappings[i], newPriorities).ConfigureAwait(false);
                    }
                    else if (oldPriority != FilePriority.Skip && newPriority == FilePriority.Skip)
                    {
                        // Normal → Skip: check if file already exists on disk.
                        bool exists = File.Exists(mappings[i].FilePath);
                        _usePartfile[i] = !exists;
                    }
                }

                _filePriorities = (FilePriority[])newPriorities.Clone();
            }
            finally
            {
                ReleaseFence();
            }
        }

        /// <summary>
        /// Applies a single-file priority change.
        /// </summary>
        public async Task OnSingleFilePriorityChangedAsync(int fileIndex, FilePriority newPriority)
        {
            if (fileIndex < 0 || fileIndex >= _filePriorities.Length)
                return;

            if (_filePriorities[fileIndex] == newPriority)
                return;

            await AcquireFenceAsync().ConfigureAwait(false);
            try
            {
                // Build projected priorities array for use in exportability checks.
                var projected = (FilePriority[])_filePriorities.Clone();
                projected[fileIndex] = newPriority;

                var oldPriority = _filePriorities[fileIndex];
                var mappings = _pieceMapper.FileMappings;

                if (oldPriority == FilePriority.Skip && newPriority != FilePriority.Skip)
                {
                    await ExportFileFromPartFileAsync(fileIndex, mappings[fileIndex], projected).ConfigureAwait(false);
                }
                else if (oldPriority != FilePriority.Skip && newPriority == FilePriority.Skip)
                {
                    bool exists = File.Exists(mappings[fileIndex].FilePath);
                    _usePartfile[fileIndex] = !exists;
                }

                _filePriorities[fileIndex] = newPriority;
            }
            finally
            {
                ReleaseFence();
            }
        }

        // ------------------------------------------------------------------ //
        //  Additional public helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Updates the base path (e.g. after a move_storage operation).
        /// Flushes/disposes the current partfile, moves the .parts file,
        /// then rebuilds the path→fileIndex lookup.
        /// </summary>
        public void UpdateBasePath(string newBasePath)
        {
            if (_partFile != null)
            {
                _partFile.FlushMetadata();
                _partFile.Dispose();
                _partFile = null;
            }

            // Move the .parts file from the old directory to the new directory.
            var oldPartPath = Path.Combine(_directory, _partFileName);
            var newPartPath = Path.Combine(newBasePath, _partFileName);

            if (File.Exists(oldPartPath) && !string.Equals(oldPartPath, newPartPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Move(oldPartPath, newPartPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to move part file from {OldPath} to {NewPath}", oldPartPath, newPartPath);
                }
            }

            _directory = newBasePath;

            // Rebuild the piece mapper and reverse mapper with the new base path,
            // then rebuild the path→fileIndex lookup from the new mapper.
            _pieceMapper = new PieceMapper(newBasePath, _torrentInfo);
            _reverseMapper = new FileOffsetToPieceMapper(_pieceMapper);
            _pathToFileIndex = BuildPathToFileIndex(_pieceMapper);
        }

        /// <summary>
        /// Returns true if the piece at <paramref name="pieceIndex"/> is stored in the partfile.
        /// </summary>
        public bool HasPieceInPartFile(int pieceIndex)
            => _partFile?.HasPiece(pieceIndex) ?? false;

        // ------------------------------------------------------------------ //
        //  IAsyncDisposable
        // ------------------------------------------------------------------ //

        public async ValueTask DisposeAsync()
        {
            _partFile?.Dispose();
            _partFile = null;
            await _inner.DisposeAsync().ConfigureAwait(false);
            _fenceLock.Dispose();
        }

        // ------------------------------------------------------------------ //
        //  Fence helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Atomically waits for any active fence to clear, then increments the I/O count.
        /// Re-checks the fence after increment to close the TOCTOU window between
        /// <see cref="WaitForFenceAsync"/> and <see cref="Interlocked.Increment"/>.
        /// </summary>
        private async ValueTask EnterIoAsync()
        {
            while (true)
            {
                await WaitForFenceAsync().ConfigureAwait(false);
                Interlocked.Increment(ref _activeIoCount);
                if (!_fenceActive)
                    return; // Successfully entered
                // Fence was raised between our check and increment — back off
                Interlocked.Decrement(ref _activeIoCount);
            }
        }

        /// <summary>
        /// If the fence is active, waits for it to be released before returning.
        /// This is a barrier — normal I/O callers wait here during priority transitions.
        /// </summary>
        private async ValueTask WaitForFenceAsync()
        {
            if (!_fenceActive)
                return;

            await _fenceLock.WaitAsync().ConfigureAwait(false);
            _fenceLock.Release();
        }

        /// <summary>
        /// Raises the disk fence.  Waits until all in-flight I/O has completed.
        /// </summary>
        private async Task AcquireFenceAsync()
        {
            await _fenceLock.WaitAsync().ConfigureAwait(false);
            _fenceActive = true;

            // Spin-wait until all in-flight I/O drains.
            while (Volatile.Read(ref _activeIoCount) > 0)
                await Task.Yield();
        }

        /// <summary>
        /// Lowers the disk fence and allows I/O to proceed again.
        /// </summary>
        private void ReleaseFence()
        {
            _fenceActive = false;
            _fenceLock.Release();
        }

        // ------------------------------------------------------------------ //
        //  Routing helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Returns true when I/O for <paramref name="filePath"/> should be routed to the
        /// part file rather than the inner backend.
        /// </summary>
        private bool ShouldRouteToPartFile(string filePath, out int fileIndex)
        {
            if (_pathToFileIndex.TryGetValue(filePath, out fileIndex))
            {
                return _filePriorities[fileIndex] == FilePriority.Skip
                    && _usePartfile[fileIndex];
            }

            fileIndex = -1;
            return false;
        }

        /// <summary>
        /// Lazily creates the <see cref="PartFile"/> on first access.
        /// </summary>
        private PartFile NeedPartFile()
        {
            if (_partFile != null)
                return _partFile;

            _partFile = new PartFile(
                _directory,
                _partFileName,
                _torrentInfo.PieceCount,
                (int)_torrentInfo.PieceLength,
                _logger);

            return _partFile;
        }

        // ------------------------------------------------------------------ //
        //  Export helper
        // ------------------------------------------------------------------ //

        private async Task ExportFileFromPartFileAsync(
            int fileIndex,
            PieceMapper.FileMapping mapping,
            FilePriority[] projectedPriorities)
        {
            if (_partFile == null)
                return;

            async ValueTask WriteCallback(long fileRelativeOffset, ReadOnlyMemory<byte> data)
            {
                await _inner.WriteAsync(mapping.FilePath, fileRelativeOffset, data)
                    .ConfigureAwait(false);
            }

            bool IsPieceFullyExportable(int pieceIndex)
                => IsPieceFullyExportableImpl(pieceIndex, fileIndex, projectedPriorities);

            await _partFile.ExportFileAsync(
                WriteCallback,
                mapping.StartOffset,
                mapping.Length,
                IsPieceFullyExportable).ConfigureAwait(false);

            // After export, this file writes should now go to the real backend.
            _usePartfile[fileIndex] = false;
        }

        /// <summary>
        /// A piece slot may only be freed when ALL files that overlap it are no longer Skip.
        /// </summary>
        private bool IsPieceFullyExportableImpl(
            int pieceIndex,
            int changedFileIndex,
            FilePriority[] projectedPriorities)
        {
            var location = _pieceMapper.MapPieceToFiles(pieceIndex);
            foreach (var segment in location.FileSegments)
            {
                int idx = segment.FileIndex;
                if (idx < 0) continue;
                if (idx < projectedPriorities.Length && projectedPriorities[idx] == FilePriority.Skip)
                    return false;
            }

            return true;
        }

        // ------------------------------------------------------------------ //
        //  Path lookup builder
        // ------------------------------------------------------------------ //

        private static Dictionary<string, int> BuildPathToFileIndex(PieceMapper pieceMapper)
        {
            var mappings = pieceMapper.FileMappings;
            var dict = new Dictionary<string, int>(mappings.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < mappings.Count; i++)
                dict[mappings[i].FilePath] = i;
            return dict;
        }
    }
}
