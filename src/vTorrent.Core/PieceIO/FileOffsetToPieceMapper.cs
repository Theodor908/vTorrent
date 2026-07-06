using System;
using System.Collections.Generic;

namespace vTorrent.Core.PieceIO;

/// <summary>
/// Reverse mapper: converts (fileIndex, fileOffset) to (pieceIndex, offsetWithinPiece).
/// Built from PieceMapper's file layout data.
/// </summary>
internal sealed class FileOffsetToPieceMapper
{
    private readonly long[] _fileStartOffsets; // torrent-absolute start offset per file
    private readonly long _pieceLength;

    public FileOffsetToPieceMapper(PieceMapper pieceMapper)
    {
        if (pieceMapper == null) throw new ArgumentNullException(nameof(pieceMapper));

        var mappings = pieceMapper.FileMappings;
        _fileStartOffsets = new long[mappings.Count];
        for (int i = 0; i < mappings.Count; i++)
            _fileStartOffsets[i] = mappings[i].StartOffset;

        _pieceLength = pieceMapper.PieceLength;
    }

    /// <summary>
    /// Maps a file-relative offset to a piece index and offset within that piece.
    /// </summary>
    /// <returns>(pieceIndex, offsetWithinPiece) where offsetWithinPiece is the byte
    /// position within the piece's partfile slot (equivalent to FileSegment.PieceOffset).</returns>
    public (int pieceIndex, int offsetWithinPiece) Map(int fileIndex, long fileOffset)
    {
        long torrentOffset = _fileStartOffsets[fileIndex] + fileOffset;
        int pieceIndex = (int)(torrentOffset / _pieceLength);
        int offsetInPiece = (int)(torrentOffset % _pieceLength);
        return (pieceIndex, offsetInPiece);
    }
}
