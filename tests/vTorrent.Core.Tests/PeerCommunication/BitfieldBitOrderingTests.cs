using FluentAssertions;
using vTorrent.Core.PeerCommunication.Utilities;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication;

public class BitfieldBitOrderingTests
{
    [Fact]
    public void SetPiece0_ShouldSetMsbOfByte0()
    {
        var bf = new Bitfield(8);
        bf.SetPiece(0);

        // BitTorrent protocol: piece 0 = bit 7 (MSB) of byte 0 = 0x80
        bf.Data[0].Should().Be(0x80,
            "piece 0 must be the MSB of byte 0 per BitTorrent protocol");
    }

    [Fact]
    public void SetPiece7_ShouldSetLsbOfByte0()
    {
        var bf = new Bitfield(8);
        bf.SetPiece(7);

        // BitTorrent protocol: piece 7 = bit 0 (LSB) of byte 0 = 0x01
        bf.Data[0].Should().Be(0x01,
            "piece 7 must be the LSB of byte 0 per BitTorrent protocol");
    }

    [Fact]
    public void SetPiece8_ShouldSetMsbOfByte1()
    {
        var bf = new Bitfield(16);
        bf.SetPiece(8);

        bf.Data[0].Should().Be(0x00);
        bf.Data[1].Should().Be(0x80,
            "piece 8 must be the MSB of byte 1");
    }

    [Fact]
    public void HasPiece_ShouldReadMsbFirst()
    {
        // Simulate receiving a protocol bitfield: byte 0 = 0x80 means piece 0
        var data = new byte[] { 0x80, 0x00 };
        var bf = new Bitfield(data, 16);

        bf.HasPiece(0).Should().BeTrue("0x80 in byte 0 = piece 0 in MSB-first");
        bf.HasPiece(7).Should().BeFalse();
    }

    [Fact]
    public void DataBytes_ShouldBeProtocolCompatible()
    {
        // Set pieces 0, 1, 7 in a 16-piece bitfield
        var bf = new Bitfield(16);
        bf.SetPiece(0);
        bf.SetPiece(1);
        bf.SetPiece(7);

        // Expected: byte 0 = 0b_1100_0001 = 0xC1
        bf.Data[0].Should().Be(0xC1);
        bf.Data[1].Should().Be(0x00);
    }

    [Fact]
    public void RoundTrip_SetAndHas_AllPieces()
    {
        var bf = new Bitfield(100);
        for (int i = 0; i < 100; i++)
        {
            bf.SetPiece(i);
            bf.HasPiece(i).Should().BeTrue($"piece {i} should be set");
        }
        bf.CompletePieces.Should().Be(100);
    }

    [Fact]
    public void ClearPiece_ShouldUnsetCorrectBit()
    {
        var bf = new Bitfield(8);
        bf.SetPiece(3);
        bf.HasPiece(3).Should().BeTrue();

        bf.ClearPiece(3);
        bf.HasPiece(3).Should().BeFalse();
        bf.CompletePieces.Should().Be(0);
    }

    [Fact]
    public void SetAll_ShouldSetAllPiecesAndClearSpareBits()
    {
        // 10 pieces = 2 bytes, last byte has 6 spare bits
        var bf = new Bitfield(10);
        bf.SetAll();

        bf.CompletePieces.Should().Be(10);
        bf.Data[0].Should().Be(0xFF);
        // Last byte: pieces 8,9 set (bits 7,6) = 0b_1100_0000 = 0xC0
        bf.Data[1].Should().Be(0xC0,
            "spare bits in last byte must be 0, valid piece bits must be 1");
    }

    [Fact]
    public void Constructor_FromProtocolBytes_ShouldParseCorrectly()
    {
        // Simulate a peer bitfield: pieces 0,2,4 set
        // Byte 0: piece 0 = bit 7, piece 2 = bit 5, piece 4 = bit 3
        // = 0b_1010_1000 = 0xA8
        var data = new byte[] { 0xA8 };
        var bf = new Bitfield(data, 8);

        bf.HasPiece(0).Should().BeTrue();
        bf.HasPiece(1).Should().BeFalse();
        bf.HasPiece(2).Should().BeTrue();
        bf.HasPiece(3).Should().BeFalse();
        bf.HasPiece(4).Should().BeTrue();
        bf.HasPiece(5).Should().BeFalse();
        bf.CompletePieces.Should().Be(3);
    }

    [Fact]
    public void Or_ShouldCombineBitfields()
    {
        var bf1 = new Bitfield(100);
        bf1.SetPiece(0);
        bf1.SetPiece(50);

        var bf2 = new Bitfield(100);
        bf2.SetPiece(50);
        bf2.SetPiece(99);

        bf1.Or(bf2);

        bf1.HasPiece(0).Should().BeTrue();
        bf1.HasPiece(50).Should().BeTrue();
        bf1.HasPiece(99).Should().BeTrue();
        bf1.CompletePieces.Should().Be(3);
    }

    [Fact]
    public void And_ShouldIntersectBitfields()
    {
        var bf1 = new Bitfield(100);
        bf1.SetPiece(0);
        bf1.SetPiece(50);

        var bf2 = new Bitfield(100);
        bf2.SetPiece(50);
        bf2.SetPiece(99);

        bf1.And(bf2);

        bf1.HasPiece(0).Should().BeFalse();
        bf1.HasPiece(50).Should().BeTrue();
        bf1.HasPiece(99).Should().BeFalse();
        bf1.CompletePieces.Should().Be(1);
    }

    [Fact]
    public void Bitfield_RoundTrip_ProtocolCompatibility()
    {
        // Simulate: we create a local bitfield, send Data to peer,
        // peer reads it using PeerHasPiece() convention.
        var local = new Bitfield(100);
        local.SetPiece(0);
        local.SetPiece(42);
        local.SetPiece(99);

        byte[] wireData = local.Data; // This is what we'd send

        // Simulate peer reading our bitfield with PeerHasPiece logic
        for (int i = 0; i < 100; i++)
        {
            int byteIndex = i / 8;
            int bitIndex = 7 - (i % 8);
            bool peerSees = (wireData[byteIndex] & (1 << bitIndex)) != 0;

            if (i == 0 || i == 42 || i == 99)
                peerSees.Should().BeTrue($"peer should see piece {i}");
            else
                peerSees.Should().BeFalse($"peer should not see piece {i}");
        }
    }
}
