using System;
using FluentAssertions;
using Xunit;
using vTorrent.Core.PieceIO;
using vTorrent.Tests.Mocks;

namespace vTorrent.Tests.Unit.PieceIO;

public class FileOffsetToPieceMapperTests
{
    [Fact]
    public void Map_SingleFileTorrent_ReturnsCorrectPieceAndOffset()
    {
        // Single file: 10 pieces x 16384 bytes = 163840 bytes total
        var info = MockFactories.CreateTorrentInfo(pieceCount: 10, pieceLength: 16384);
        var basePath = "/tmp/test";
        var pieceMapper = new PieceMapper(basePath, info);
        var reverseMapper = new FileOffsetToPieceMapper(pieceMapper);

        // File offset 0 -> piece 0, offset 0
        var (pieceIndex, offsetInPiece) = reverseMapper.Map(fileIndex: 0, fileOffset: 0);
        pieceIndex.Should().Be(0);
        offsetInPiece.Should().Be(0);

        // File offset 16384 -> piece 1, offset 0
        (pieceIndex, offsetInPiece) = reverseMapper.Map(fileIndex: 0, fileOffset: 16384);
        pieceIndex.Should().Be(1);
        offsetInPiece.Should().Be(0);

        // File offset 16384 + 100 -> piece 1, offset 100
        (pieceIndex, offsetInPiece) = reverseMapper.Map(fileIndex: 0, fileOffset: 16484);
        pieceIndex.Should().Be(1);
        offsetInPiece.Should().Be(100);
    }

    [Fact]
    public void Map_MultiFileTorrent_BoundaryPiece()
    {
        // 3 files, 100 pieces x 16384 = 1638400 bytes
        // File sizes: 546133, 546133, 546134
        var info = MockFactories.CreateMultiFileTorrentInfo(
            pieceCount: 100, pieceLength: 16384, fileCount: 3);
        var basePath = "/tmp/test";
        var pieceMapper = new PieceMapper(basePath, info);
        var reverseMapper = new FileOffsetToPieceMapper(pieceMapper);

        // File 1 at fileOffset 0 -> the piece and offset where file 1 begins
        var (pieceIndex, offsetInPiece) = reverseMapper.Map(fileIndex: 1, fileOffset: 0);

        // File 1's torrent offset = file 0's length = 546133
        // Piece = 546133 / 16384 = 33
        // Offset in piece = 546133 % 16384 = 5461
        pieceIndex.Should().Be(33);
        offsetInPiece.Should().Be(5461);
    }

    [Fact]
    public void Map_FileOffset_MidPiece()
    {
        var info = MockFactories.CreateTorrentInfo(pieceCount: 10, pieceLength: 16384);
        var basePath = "/tmp/test";
        var pieceMapper = new PieceMapper(basePath, info);
        var reverseMapper = new FileOffsetToPieceMapper(pieceMapper);

        // File offset 8192 -> piece 0, offset 8192 (middle of first piece)
        var (pieceIndex, offsetInPiece) = reverseMapper.Map(fileIndex: 0, fileOffset: 8192);
        pieceIndex.Should().Be(0);
        offsetInPiece.Should().Be(8192);
    }
}
