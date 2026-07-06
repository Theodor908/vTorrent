using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace vTorrent.Core.PieceIO
{
    /// <summary>
    /// Binary part-file for storing piece data belonging to skipped files.
    /// Matches libtorrent's part_file.cpp on-disk format exactly.
    ///
    /// Format:
    ///   [uint32 num_pieces][uint32 piece_size][uint32 x num_pieces slot_map]
    ///   [padding to 1024-byte alignment]
    ///   [slot 0: piece_size bytes][slot 1: piece_size bytes]...
    ///
    /// Each slot_map entry is either a valid slot index (0-based) or
    /// 0xFFFFFFFF (piece not stored).
    /// </summary>
    internal sealed class PartFile : IDisposable
    {
        // ------------------------------------------------------------------ //
        //  Constants
        // ------------------------------------------------------------------ //

        private const uint NoSlot = 0xFFFF_FFFF;
        private const int HeaderAlignment = 1024;

        // ------------------------------------------------------------------ //
        //  Fields
        // ------------------------------------------------------------------ //

        private readonly string _filePath;
        private readonly int _numPieces;
        private readonly int _pieceSize;
        private readonly ILogger _logger;

        /// <summary>Maps pieceIndex -> slot index.</summary>
        private readonly Dictionary<int, int> _slotMap;

        /// <summary>Slot indices that have been freed and may be reused.</summary>
        private readonly List<int> _freeSlots;

        /// <summary>The next fresh slot index (never-used slots beyond all free ones).</summary>
        private int _nextSlot;

        /// <summary>Whether the header needs to be flushed to disk.</summary>
        private bool _metadataDirty;

        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        private SafeFileHandle? _handle;
        private bool _disposed;

        // ------------------------------------------------------------------ //
        //  Constructor
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Opens an existing .parts file and loads its header, or starts fresh if
        /// none exists.  A truncated or mismatched header is treated as empty with a
        /// warning logged.
        /// </summary>
        public PartFile(
            string directory,
            string name,
            int numPieces,
            int pieceSize,
            ILogger logger)
        {
            _filePath = Path.Combine(directory, name);
            _numPieces = numPieces;
            _pieceSize = pieceSize;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _slotMap = new Dictionary<int, int>();
            _freeSlots = new List<int>();

            TryLoadHeader();
        }

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>Returns true if <paramref name="pieceIndex"/> has a stored slot.</summary>
        public bool HasPiece(int pieceIndex)
        {
            _lock.Wait();
            try
            {
                return _slotMap.ContainsKey(pieceIndex);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Reads <paramref name="buffer"/>.Length bytes from the stored slot for
        /// <paramref name="pieceIndex"/> at <paramref name="offset"/> within that piece.
        /// Returns 0 when the piece is not stored.
        /// </summary>
        public async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            int pieceIndex,
            int offset)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            int slot;
            try
            {
                if (!_slotMap.TryGetValue(pieceIndex, out slot))
                    return 0;
            }
            finally
            {
                _lock.Release();
            }

            // I/O outside the lock
            var fileOffset = SlotFileOffset(slot) + offset;
            var handle = EnsureHandle();
            return await RandomAccess.ReadAsync(handle, buffer, fileOffset).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes <paramref name="buffer"/> into the stored slot for
        /// <paramref name="pieceIndex"/> at <paramref name="offset"/>.  Allocates a new
        /// slot on first write.
        /// </summary>
        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            int pieceIndex,
            int offset)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            int slot;
            bool isNew = false;
            try
            {
                if (!_slotMap.TryGetValue(pieceIndex, out slot))
                {
                    slot = AllocateSlotLocked();
                    _slotMap[pieceIndex] = slot;
                    _metadataDirty = true;
                    isNew = true;
                }
            }
            finally
            {
                _lock.Release();
            }

            // I/O outside the lock
            var handle = EnsureHandle();

            // If it's a brand-new slot, zero-initialise the full piece region so
            // partial writes don't leave uninitialised bytes (matches libtorrent).
            if (isNew && offset != 0)
            {
                var zeros = ArrayPool<byte>.Shared.Rent(_pieceSize);
                try
                {
                    Array.Clear(zeros, 0, _pieceSize);
                    await RandomAccess.WriteAsync(handle, zeros.AsMemory(0, _pieceSize), SlotFileOffset(slot))
                        .ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(zeros);
                }
            }

            var fileOffset = SlotFileOffset(slot) + offset;
            await RandomAccess.WriteAsync(handle, buffer, fileOffset).ConfigureAwait(false);
        }

        /// <summary>
        /// Exports stored pieces that overlap the file range
        /// [<paramref name="fileOffset"/>, <paramref name="fileOffset"/> +
        /// <paramref name="fileSize"/>).
        ///
        /// For each stored piece in that range the data is delivered via
        /// <paramref name="writeCallback"/>.  If
        /// <paramref name="isPieceFullyExportable"/> returns <c>true</c> for a piece
        /// the slot is freed after the export.
        /// </summary>
        public async ValueTask ExportFileAsync(
            Func<long, ReadOnlyMemory<byte>, ValueTask> writeCallback,
            long fileOffset,
            long fileSize,
            Func<int, bool> isPieceFullyExportable)
        {
            long fileEnd = fileOffset + fileSize;

            // Determine which piece indices overlap the file range.
            int firstPiece = (int)(fileOffset / _pieceSize);
            int lastPiece  = (int)((fileEnd - 1) / _pieceSize);

            for (int pieceIndex = firstPiece; pieceIndex <= lastPiece && pieceIndex < _numPieces; pieceIndex++)
            {
                // Check slot existence under lock
                await _lock.WaitAsync().ConfigureAwait(false);
                int slot;
                bool found;
                try
                {
                    found = _slotMap.TryGetValue(pieceIndex, out slot);
                }
                finally
                {
                    _lock.Release();
                }

                if (!found)
                    continue;

                // Compute the byte range within this piece that falls inside the file.
                long pieceStart = (long)pieceIndex * _pieceSize;
                long pieceEnd   = pieceStart + _pieceSize;

                long readFrom = Math.Max(fileOffset, pieceStart);
                long readTo   = Math.Min(fileEnd, pieceEnd);
                int  readLen  = (int)(readTo - readFrom);

                var buf = new byte[readLen];
                int slotOffset = (int)(readFrom - pieceStart);
                var fileReadOffset = SlotFileOffset(slot) + slotOffset;

                var handle = EnsureHandle();
                await RandomAccess.ReadAsync(handle, buf.AsMemory(), fileReadOffset).ConfigureAwait(false);

                // The callback offset is relative to the file being exported.
                long callbackOffset = readFrom - fileOffset;
                await writeCallback(callbackOffset, buf.AsMemory()).ConfigureAwait(false);

                // Re-acquire lock to verify slot still exists, then free if eligible.
                await _lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_slotMap.TryGetValue(pieceIndex, out _) && isPieceFullyExportable(pieceIndex))
                    {
                        _freeSlots.Add(_slotMap[pieceIndex]);
                        _slotMap.Remove(pieceIndex);
                        _metadataDirty = true;
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }
        }

        /// <summary>Frees the slot for <paramref name="pieceIndex"/>.</summary>
        public void FreePiece(int pieceIndex)
        {
            _lock.Wait();
            try
            {
                if (_slotMap.TryGetValue(pieceIndex, out int slot))
                {
                    _freeSlots.Add(slot);
                    _slotMap.Remove(pieceIndex);
                    _metadataDirty = true;
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Flushes a dirty header to disk.</summary>
        public void FlushMetadata()
        {
            _lock.Wait();
            try
            {
                if (_metadataDirty)
                    WriteHeaderLocked();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Flushes metadata, then deletes the file if no pieces remain.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _lock.Wait();
            try
            {
                if (_metadataDirty && _handle is not null)
                    WriteHeaderLocked();
            }
            finally
            {
                _lock.Release();
            }

            _handle?.Dispose();
            _handle = null;

            // Delete when all slots are free (no pieces stored).
            bool isEmpty;
            _lock.Wait();
            try
            {
                isEmpty = _slotMap.Count == 0;
            }
            finally
            {
                _lock.Release();
            }

            if (isEmpty && File.Exists(_filePath))
            {
                try { File.Delete(_filePath); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PartFile: failed to delete empty part file '{Path}'.", _filePath);
                }
            }

            _lock.Dispose();
        }

        // ------------------------------------------------------------------ //
        //  Private helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Returns the byte offset of <paramref name="slotIndex"/> inside the file.
        /// </summary>
        private long SlotFileOffset(int slotIndex)
            => HeaderSize() + (long)slotIndex * _pieceSize;

        /// <summary>
        /// Computes the padded header size for the current <see cref="_numPieces"/>.
        /// </summary>
        private int HeaderSize()
        {
            int raw = 4 + 4 + 4 * _numPieces; // num_pieces + piece_size + slot_map entries
            return AlignUp(raw, HeaderAlignment);
        }

        private static int AlignUp(int value, int alignment)
        {
            int rem = value % alignment;
            return rem == 0 ? value : value + (alignment - rem);
        }

        /// <summary>Opens or creates the file handle (lazy).</summary>
        private SafeFileHandle EnsureHandle()
        {
            if (_handle is not null)
                return _handle;

            // Lazily create the file on first I/O.
            _handle = File.OpenHandle(
                _filePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.Asynchronous);

            return _handle;
        }

        /// <summary>
        /// Tries to read the existing header.  On any error (missing, truncated,
        /// mismatched) the in-memory state is left empty and a warning is logged.
        /// </summary>
        private void TryLoadHeader()
        {
            if (!File.Exists(_filePath))
                return; // brand new — lazy creation

            try
            {
                using var handle = File.OpenHandle(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    FileOptions.None);

                // Read the fixed 8-byte prefix first.
                Span<byte> prefix = stackalloc byte[8];
                int read = RandomAccess.Read(handle, prefix, fileOffset: 0);
                if (read < 8)
                {
                    _logger.LogWarning(
                        "PartFile '{Path}': header too short ({Bytes} bytes) — treating as empty.",
                        _filePath, read);
                    return;
                }

                uint storedNumPieces = MemoryMarshal.Read<uint>(prefix[0..4]);
                uint storedPieceSize = MemoryMarshal.Read<uint>(prefix[4..8]);

                if (storedNumPieces != (uint)_numPieces || storedPieceSize != (uint)_pieceSize)
                {
                    _logger.LogWarning(
                        "PartFile '{Path}': metadata mismatch " +
                        "(stored numPieces={SN}, pieceSize={SP}; expected {EN}, {EP}) — treating as empty.",
                        _filePath, storedNumPieces, storedPieceSize, _numPieces, _pieceSize);
                    return;
                }

                // Read the full slot map.
                int mapBytes = 4 * _numPieces;
                var mapBuf = new byte[mapBytes];
                int mapRead = RandomAccess.Read(handle, mapBuf.AsSpan(), fileOffset: 8);
                if (mapRead < mapBytes)
                {
                    _logger.LogWarning(
                        "PartFile '{Path}': slot map truncated ({Got}/{Expected} bytes) — treating as empty.",
                        _filePath, mapRead, mapBytes);
                    return;
                }

                // Reconstruct in-memory maps.
                var usedSlots = new HashSet<int>();
                for (int i = 0; i < _numPieces; i++)
                {
                    uint entry = MemoryMarshal.Read<uint>(mapBuf.AsSpan(i * 4, 4));
                    if (entry != NoSlot)
                    {
                        int slot = (int)entry;
                        _slotMap[i] = slot;
                        if (slot + 1 > _nextSlot)
                            _nextSlot = slot + 1;
                        usedSlots.Add(slot);
                    }
                }

                // Identify free slots (any index < _nextSlot not currently in use).
                for (int s = 0; s < _nextSlot; s++)
                {
                    if (!usedSlots.Contains(s))
                        _freeSlots.Add(s);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PartFile '{Path}': error loading header — treating as empty.", _filePath);
                _slotMap.Clear();
                _freeSlots.Clear();
                _nextSlot = 0;
            }
        }

        /// <summary>
        /// Allocates the next available slot.
        /// Must be called with <see cref="_lock"/> held.
        /// </summary>
        private int AllocateSlotLocked()
        {
            if (_freeSlots.Count > 0)
            {
                int last = _freeSlots.Count - 1;
                int slot = _freeSlots[last];
                _freeSlots.RemoveAt(last);
                return slot;
            }

            return _nextSlot++;
        }

        /// <summary>
        /// Writes the complete header to disk.
        /// Must be called with <see cref="_lock"/> held and <see cref="_handle"/> open.
        /// </summary>
        private void WriteHeaderLocked()
        {
            int headerSize = HeaderSize();
            var buf = new byte[headerSize];

            // num_pieces
            MemoryMarshal.Write(buf.AsSpan(0), (uint)_numPieces);
            // piece_size
            MemoryMarshal.Write(buf.AsSpan(4), (uint)_pieceSize);

            // slot_map — default all entries to NoSlot (0xFF bytes fill is correct for uint max)
            for (int i = 8; i < 8 + 4 * _numPieces; i++)
                buf[i] = 0xFF;

            foreach (var (pieceIdx, slot) in _slotMap)
                MemoryMarshal.Write(buf.AsSpan(8 + pieceIdx * 4), (uint)slot);

            // Remaining bytes in the padding are already zero from new byte[].
            var handle = _handle!;
            RandomAccess.Write(handle, buf.AsSpan(), fileOffset: 0);

            _metadataDirty = false;
        }
    }
}
