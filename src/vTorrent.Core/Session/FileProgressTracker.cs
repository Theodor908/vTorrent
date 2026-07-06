using System;
using System.Collections.Generic;
using System.Linq;
using vTorrent.Bencode.Torrents;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Core.Session;

/// <summary>
/// Tracks per-file download progress by mapping pieces to files.
/// Computes which pieces belong to which files and updates progress
/// as pieces are completed.
/// </summary>
public class FileProgressTracker
{
    private readonly FileProgress[] _files;
    private readonly int _pieceLength;
    private readonly int _lastPieceLength;
    private readonly int _totalPieces;
    private readonly long _totalSize;

    // Piece-to-files mapping for efficient lookups
    // Each piece can span multiple files
    private readonly List<int>[] _pieceToFiles;

    // Track which pieces are complete
    private readonly bool[] _completedPieces;

    private readonly object _lock = new();

    /// <summary>
    /// All files in the torrent with their progress
    /// </summary>
    public IReadOnlyList<FileProgress> Files => _files;

    /// <summary>
    /// Total number of files
    /// </summary>
    public int FileCount => _files.Length;

    /// <summary>
    /// Total torrent size
    /// </summary>
    public long TotalSize => _totalSize;

    /// <summary>
    /// Total bytes completed across all files
    /// </summary>
    public long TotalBytesCompleted
    {
        get
        {
            lock (_lock)
            {
                return _files.Sum(f => f.BytesCompleted);
            }
        }
    }

    /// <summary>
    /// Overall progress as fraction 0.0 to 1.0
    /// </summary>
    public float TotalProgress => _totalSize > 0 ? (float)TotalBytesCompleted / _totalSize : 0f;

    /// <summary>
    /// Number of completed files
    /// </summary>
    public int CompletedFileCount
    {
        get
        {
            lock (_lock)
            {
                return _files.Count(f => f.IsComplete);
            }
        }
    }

    public FileProgressTracker(TorrentInfo torrentInfo)
    {
        if (torrentInfo == null)
            throw new ArgumentNullException(nameof(torrentInfo));

        _pieceLength = (int)torrentInfo.PieceLength;
        _totalPieces = torrentInfo.PieceCount;
        _totalSize = torrentInfo.TotalSize;

        // Calculate last piece length
        var lastPieceSize = _totalSize % _pieceLength;
        _lastPieceLength = lastPieceSize > 0 ? (int)lastPieceSize : _pieceLength;

        // Initialize completed pieces tracking
        _completedPieces = new bool[_totalPieces];

        // Build file mappings
        _files = BuildFileMappings(torrentInfo);

        // Build reverse mapping (piece -> files)
        _pieceToFiles = BuildPieceToFilesMapping();
    }

    private FileProgress[] BuildFileMappings(TorrentInfo torrentInfo)
    {
        var files = new FileProgress[torrentInfo.Files.Count];
        long currentOffset = 0;

        for (int i = 0; i < torrentInfo.Files.Count; i++)
        {
            var file = torrentInfo.Files[i];
            var path = file.GetFullPath();
            var size = file.Length;

            // Calculate piece boundaries
            int firstPiece = (int)(currentOffset / _pieceLength);
            int firstPieceOffset = (int)(currentOffset % _pieceLength);

            long endOffset = currentOffset + size;
            int lastPiece = size > 0 ? (int)((endOffset - 1) / _pieceLength) : firstPiece;
            int lastPieceEndOffset = (int)(endOffset % _pieceLength);
            if (lastPieceEndOffset == 0 && size > 0)
                lastPieceEndOffset = _pieceLength;

            files[i] = new FileProgress(
                fileIndex: i,
                path: path,
                size: size,
                startOffset: currentOffset,
                firstPiece: firstPiece,
                lastPiece: lastPiece,
                firstPieceOffset: firstPieceOffset,
                lastPieceEndOffset: lastPieceEndOffset);

            currentOffset += size;
        }

        return files;
    }

    private List<int>[] BuildPieceToFilesMapping()
    {
        var mapping = new List<int>[_totalPieces];

        for (int p = 0; p < _totalPieces; p++)
        {
            mapping[p] = new List<int>();
        }

        for (int f = 0; f < _files.Length; f++)
        {
            var file = _files[f];
            for (int p = file.FirstPiece; p <= file.LastPiece; p++)
            {
                mapping[p].Add(f);
            }
        }

        return mapping;
    }

    /// <summary>
    /// Mark a piece as completed and update affected file progress
    /// </summary>
    public void OnPieceCompleted(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= _totalPieces)
            return;

        lock (_lock)
        {
            if (_completedPieces[pieceIndex])
                return; // Already processed

            _completedPieces[pieceIndex] = true;

            // Get the actual piece length (last piece may be smaller)
            int actualPieceLength = pieceIndex == _totalPieces - 1 ? _lastPieceLength : _pieceLength;

            // Update all files that contain this piece
            foreach (var fileIndex in _pieceToFiles[pieceIndex])
            {
                var file = _files[fileIndex];
                int bytesForFile = file.GetBytesInPiece(pieceIndex, actualPieceLength);
                file.BytesCompleted += bytesForFile;
            }
        }
    }

    /// <summary>
    /// Reverses OnPieceCompleted when a piece fails hash verification.
    /// Without this, BytesCompleted stays inflated after hash failure,
    /// causing progress to never decrease — the progress bar fluctuates
    /// as different update paths use stale vs fresh data.
    /// </summary>
    public void OnPieceFailed(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= _totalPieces)
            return;

        lock (_lock)
        {
            if (!_completedPieces[pieceIndex])
                return; // Not marked as completed — nothing to undo

            _completedPieces[pieceIndex] = false;

            int actualPieceLength = pieceIndex == _totalPieces - 1 ? _lastPieceLength : _pieceLength;

            foreach (var fileIndex in _pieceToFiles[pieceIndex])
            {
                var file = _files[fileIndex];
                int bytesForFile = file.GetBytesInPiece(pieceIndex, actualPieceLength);
                file.BytesCompleted = Math.Max(0, file.BytesCompleted - bytesForFile);
            }
        }
    }

    /// <summary>
    /// Initialize progress from existing bitfield (for resume)
    /// </summary>
    public void InitializeFromBitfield(bool[] havePieces)
    {
        if (havePieces == null)
            return;

        lock (_lock)
        {
            // Reset all progress
            foreach (var file in _files)
            {
                file.BytesCompleted = 0;
            }
            Array.Clear(_completedPieces, 0, _completedPieces.Length);

            // Process each completed piece
            for (int i = 0; i < Math.Min(havePieces.Length, _totalPieces); i++)
            {
                if (havePieces[i])
                {
                    _completedPieces[i] = true;
                    int actualPieceLength = i == _totalPieces - 1 ? _lastPieceLength : _pieceLength;

                    foreach (var fileIndex in _pieceToFiles[i])
                    {
                        var file = _files[fileIndex];
                        int bytesForFile = file.GetBytesInPiece(i, actualPieceLength);
                        file.BytesCompleted += bytesForFile;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Get progress for a specific file
    /// </summary>
    public FileProgress GetFileProgress(int fileIndex)
    {
        if (fileIndex < 0 || fileIndex >= _files.Length)
            throw new ArgumentOutOfRangeException(nameof(fileIndex));

        lock (_lock)
        {
            return _files[fileIndex].CreateSnapshot();
        }
    }

    /// <summary>
    /// Get progress for all files
    /// </summary>
    public IReadOnlyList<FileProgress> GetAllFileProgress()
    {
        lock (_lock)
        {
            return _files.Select(f => f.CreateSnapshot()).ToList();
        }
    }

    /// <summary>
    /// Get files that contain a specific piece
    /// </summary>
    public IReadOnlyList<int> GetFilesForPiece(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= _totalPieces)
            return Array.Empty<int>();

        return _pieceToFiles[pieceIndex];
    }

    /// <summary>
    /// Check if a piece is needed based on file priorities
    /// </summary>
    public bool IsPieceWanted(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= _totalPieces)
            return false;

        lock (_lock)
        {
            // A piece is wanted if any file containing it is wanted
            foreach (var fileIndex in _pieceToFiles[pieceIndex])
            {
                if (_files[fileIndex].IsWanted)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Set priority for a file
    /// </summary>
    public void SetFilePriority(int fileIndex, int priority)
    {
        if (fileIndex < 0 || fileIndex >= _files.Length)
            throw new ArgumentOutOfRangeException(nameof(fileIndex));

        if (priority < 0 || priority > 7)
            throw new ArgumentOutOfRangeException(nameof(priority), "Priority must be 0-7");

        lock (_lock)
        {
            _files[fileIndex].Priority = priority;
        }
    }

    /// <summary>
    /// Set priorities for all files at once from a FilePriority enum array.
    /// </summary>
    public void SetFilePriorities(FilePriority[] priorities)
    {
        lock (_lock)
        {
            for (int i = 0; i < Math.Min(priorities.Length, _files.Length); i++)
            {
                _files[i].Priority = (int)priorities[i];
            }
        }
    }

    /// <summary>
    /// Get pieces that are wanted but not yet complete
    /// </summary>
    public IEnumerable<int> GetWantedPieces()
    {
        lock (_lock)
        {
            for (int i = 0; i < _totalPieces; i++)
            {
                if (!_completedPieces[i] && IsPieceWanted(i))
                    yield return i;
            }
        }
    }

    /// <summary>
    /// Calculate total wanted bytes based on file priorities
    /// </summary>
    public long GetTotalWantedBytes()
    {
        lock (_lock)
        {
            return _files.Where(f => f.IsWanted).Sum(f => f.Size);
        }
    }

    /// <summary>
    /// Calculate completed wanted bytes based on file priorities
    /// </summary>
    public long GetWantedBytesCompleted()
    {
        lock (_lock)
        {
            return _files.Where(f => f.IsWanted).Sum(f => f.BytesCompleted);
        }
    }

    #region Availability Calculation

    // Per-piece availability (number of peers that have each piece)
    private int[] _pieceAvailability;

    /// <summary>
    /// Update availability from peer bitfields.
    /// Call this periodically to refresh availability data.
    /// </summary>
    /// <param name="peerBitfields">Collection of peer bitfields (byte arrays)</param>
    public void UpdateAvailability(IEnumerable<byte[]> peerBitfields)
    {
        lock (_lock)
        {
            // Reset availability
            _pieceAvailability ??= new int[_totalPieces];
            Array.Clear(_pieceAvailability, 0, _pieceAvailability.Length);

            // Count peers for each piece
            foreach (var bitfield in peerBitfields)
            {
                if (bitfield == null)
                    continue;

                for (int pieceIndex = 0; pieceIndex < _totalPieces; pieceIndex++)
                {
                    if (HasPieceInBitfield(bitfield, pieceIndex))
                    {
                        _pieceAvailability[pieceIndex]++;
                    }
                }
            }

            // Update file availability
            foreach (var file in _files)
            {
                file.Availability = CalculateFileAvailability(file);
            }
        }
    }

    /// <summary>
    /// Get availability for a specific piece (number of peers that have it)
    /// </summary>
    public int GetPieceAvailability(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= _totalPieces)
            return 0;

        lock (_lock)
        {
            return _pieceAvailability?[pieceIndex] ?? 0;
        }
    }

    /// <summary>
    /// Get overall torrent availability (distributed copies of rarest piece)
    /// </summary>
    public float GetOverallAvailability()
    {
        lock (_lock)
        {
            if (_pieceAvailability == null || _pieceAvailability.Length == 0)
                return 0f;

            // Find minimum availability among incomplete pieces
            int minAvailability = int.MaxValue;
            bool foundIncompletePiece = false;

            for (int i = 0; i < _totalPieces; i++)
            {
                if (!_completedPieces[i])
                {
                    foundIncompletePiece = true;
                    if (_pieceAvailability[i] < minAvailability)
                    {
                        minAvailability = _pieceAvailability[i];
                    }
                }
            }

            if (!foundIncompletePiece)
                return float.PositiveInfinity; // We have all pieces - availability is infinite

            return minAvailability;
        }
    }

    /// <summary>
    /// Calculate availability for a specific file.
    /// Returns the minimum availability among the file's pieces.
    /// </summary>
    private float CalculateFileAvailability(FileProgress file)
    {
        if (_pieceAvailability == null)
            return 0f;

        int minAvailability = int.MaxValue;
        bool foundIncompletePiece = false;

        for (int pieceIndex = file.FirstPiece; pieceIndex <= file.LastPiece; pieceIndex++)
        {
            if (!_completedPieces[pieceIndex])
            {
                foundIncompletePiece = true;
                if (_pieceAvailability[pieceIndex] < minAvailability)
                {
                    minAvailability = _pieceAvailability[pieceIndex];
                }
            }
        }

        if (!foundIncompletePiece)
            return float.PositiveInfinity; // File is complete - availability is infinite

        return minAvailability;
    }

    /// <summary>
    /// Check if a piece is present in a bitfield
    /// </summary>
    private static bool HasPieceInBitfield(byte[] bitfield, int pieceIndex)
    {
        int byteIndex = pieceIndex / 8;
        int bitIndex = 7 - (pieceIndex % 8);

        if (byteIndex >= bitfield.Length)
            return false;

        return (bitfield[byteIndex] & (1 << bitIndex)) != 0;
    }

    /// <summary>
    /// Get pieces sorted by rarity (ascending availability)
    /// </summary>
    public IEnumerable<int> GetPiecesByRarity()
    {
        lock (_lock)
        {
            if (_pieceAvailability == null)
                yield break;

            var pieces = Enumerable.Range(0, _totalPieces)
                .Where(i => !_completedPieces[i] && IsPieceWanted(i))
                .OrderBy(i => _pieceAvailability[i])
                .ThenBy(_ => Random.Shared.Next()); // Randomize within same availability

            foreach (var piece in pieces)
                yield return piece;
        }
    }

    #endregion
}
