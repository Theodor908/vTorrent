using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace vTorrent.Core.PieceIO
{
    public interface IPieceManager
    {
        // Write Operations
        /// <summary>
        /// Writes a piece to disk asynchronously.
        /// </summary>
        /// <param name="pieceIndex">The index of the piece to write.</param>
        /// <param name="data">The piece data to write.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="skipVerification">When true, skips hash verification. Use when caller has already verified.</param>
        Task<PieceWriteResult> WritePieceAsync(int pieceIndex, byte[] data, CancellationToken cancellationToken = default, bool skipVerification = false);
        PieceWriteResult WritePiece(int pieceIndex, byte[] data);

        // Read Operations
        Task<PieceReadResult> ReadPieceAsync(int pieceIndex, CancellationToken cancellationToken = default);
        PieceReadResult ReadPiece(int pieceIndex);

        /// <summary>
        /// Reads a specific block from a piece without reading the entire piece.
        /// Used for upload serving where the piece is already hash-verified.
        /// </summary>
        Task<PieceReadResult> ReadBlockAsync(int pieceIndex, int offset, int length, CancellationToken cancellationToken = default);

        // Verify piece
        bool VerifyPiece(int pieceIndex, byte[] data);

        // Verify piece validity
        bool HasValidPiece(int pieceIndex);
        BitArray GetBitfield();

        // Resume data support
        /// <summary>
        /// Initializes the internal bitfield from resume data, avoiding expensive disk verification.
        /// This enables fast resume by restoring piece completion state from saved data.
        /// </summary>
        /// <param name="resumeBitfield">The bitfield from resume data (pieces that were previously verified)</param>
        void InitializeFromResumeBitfield(BitArray resumeBitfield);

        /// <summary>
        /// Sets a specific piece's completion state without verification.
        /// </summary>
        void SetPieceComplete(int pieceIndex, bool complete);

        /// <summary>
        /// Gets whether a specific piece is marked as complete.
        /// </summary>
        bool IsPieceComplete(int pieceIndex);

        /// <summary>
        /// Gets the count of completed pieces.
        /// </summary>
        int CompletedPieceCount { get; }

        // Move storage support (libtorrent-style)

        /// <summary>
        /// Raises the disk fence - blocks new I/O, drains pending writes, closes file handles.
        /// Call before move_storage operation.
        /// </summary>
        /// <param name="timeout">Maximum time to wait for pending operations to complete.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if fence was raised successfully, false if timeout.</returns>
        Task<bool> RaiseDiskFenceAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates the base path after files have been moved.
        /// Must call RaiseDiskFenceAsync first.
        /// </summary>
        /// <param name="newBasePath">The new base path for the torrent files.</param>
        void UpdateBasePath(string newBasePath);

        /// <summary>
        /// Lowers the disk fence - allows I/O to resume.
        /// Call after move_storage operation completes.
        /// </summary>
        void LowerDiskFence();

        /// <summary>
        /// Whether the disk fence is currently raised.
        /// </summary>
        bool IsFenced { get; }

        /// <summary>
        /// Releases all write file handles, keeping only read handles open.
        /// Call this when transitioning to seeding (100% complete) to allow
        /// external programs to execute downloaded files (especially .exe files).
        /// </summary>
        ValueTask ReleaseWriteHandlesAsync();

        /// <summary>
        /// Flush file handles covering a specific piece to ensure data is visible to external readers.
        /// </summary>
        Task FlushPieceAsync(int pieceIndex, CancellationToken cancellationToken = default);

        /// <summary>
        /// Hint the disk layer about sequential vs random access patterns.
        /// Only new file opens use the hint — existing handles are unaffected.
        /// </summary>
        void SetSequentialAccessHint(bool sequential);

    }
}
