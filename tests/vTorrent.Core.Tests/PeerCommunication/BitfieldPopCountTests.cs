using FluentAssertions;
using vTorrent.Core.PeerCommunication.Utilities;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication;

public class BitfieldPopCountTests
{
    [Fact]
    public void CompletePieces_AllSet_ReturnsCorrectCount()
    {
        var bf = new Bitfield(64);
        bf.SetAll();
        bf.CompletePieces.Should().Be(64);
    }

    [Fact]
    public void CompletePieces_NoneSet_ReturnsZero()
    {
        var bf = new Bitfield(64);
        bf.CompletePieces.Should().Be(0);
    }

    [Fact]
    public void CompletePieces_SparseSet_ReturnsCorrectCount()
    {
        var bf = new Bitfield(100);
        bf.SetPiece(0, true);
        bf.SetPiece(7, true);
        bf.SetPiece(63, true);
        bf.SetPiece(64, true);
        bf.SetPiece(99, true);
        bf.CompletePieces.Should().Be(5);
    }

    [Fact]
    public void CompletePieces_NonAligned_ReturnsCorrectCount()
    {
        var bf = new Bitfield(13);
        for (int i = 0; i < 13; i++)
            bf.SetPiece(i, true);
        bf.CompletePieces.Should().Be(13);
    }

    [Fact]
    public void CompletePieces_FromByteArray_MatchesManualCount()
    {
        // 16 pieces = 2 bytes. Set pieces 0,1,2,3 (byte 0 = 0xF0 MSB-first) and piece 8 (byte 1 = 0x80)
        var data = new byte[] { 0xF0, 0x80 };
        var bf = new Bitfield(data, 16);
        bf.CompletePieces.Should().Be(5);
    }

    [Fact]
    public void CompletePieces_LargeBitfield_IsAccurate()
    {
        var bf = new Bitfield(10000);
        for (int i = 0; i < 10000; i += 3)
            bf.SetPiece(i, true);
        bf.CompletePieces.Should().Be(3334);
    }
}
