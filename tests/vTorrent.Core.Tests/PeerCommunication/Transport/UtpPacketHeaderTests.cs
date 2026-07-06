using System;
using System.Buffers.Binary;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Transport;

public class UtpPacketHeaderTests
{
    [Fact]
    public void RoundTrip_AllFields_Preserved()
    {
        var header = new UtpPacketHeader(
            type: UtpPacketType.Data,
            connectionId: 12345,
            timestampMicroseconds: 1_000_000,
            timestampDifferenceMicroseconds: 500,
            windowSize: 65536,
            sequenceNumber: 100,
            ackNumber: 99);

        Span<byte> buffer = stackalloc byte[UtpPacketHeader.Size];
        header.WriteTo(buffer);

        UtpPacketHeader.TryParse(buffer, out var parsed).Should().BeTrue();
        parsed.Type.Should().Be(UtpPacketType.Data);
        parsed.ConnectionId.Should().Be(12345);
        parsed.TimestampMicroseconds.Should().Be(1_000_000);
        parsed.TimestampDifferenceMicroseconds.Should().Be(500);
        parsed.WindowSize.Should().Be(65536u);
        parsed.SequenceNumber.Should().Be(100);
        parsed.AckNumber.Should().Be(99);
        parsed.Extension.Should().Be(0);
    }

    [Theory]
    [InlineData(UtpPacketType.Data)]
    [InlineData(UtpPacketType.Fin)]
    [InlineData(UtpPacketType.State)]
    [InlineData(UtpPacketType.Reset)]
    [InlineData(UtpPacketType.Syn)]
    public void RoundTrip_AllPacketTypes(UtpPacketType type)
    {
        var header = new UtpPacketHeader(type: type, connectionId: 1,
            timestampMicroseconds: 0, timestampDifferenceMicroseconds: 0,
            windowSize: 0, sequenceNumber: 0, ackNumber: 0);

        Span<byte> buffer = stackalloc byte[UtpPacketHeader.Size];
        header.WriteTo(buffer);

        UtpPacketHeader.TryParse(buffer, out var parsed).Should().BeTrue();
        parsed.Type.Should().Be(type);
    }

    [Fact]
    public void TryParse_TooShort_ReturnsFalse()
    {
        var data = new byte[19];
        UtpPacketHeader.TryParse(data, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_InvalidVersion_ReturnsFalse()
    {
        var data = new byte[20];
        data[0] = 0x02; // version 2
        UtpPacketHeader.TryParse(data, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_InvalidType_ReturnsFalse()
    {
        var data = new byte[20];
        data[0] = 0x51; // type 5 + version 1
        UtpPacketHeader.TryParse(data, out _).Should().BeFalse();
    }

    [Fact]
    public void WriteTo_BigEndian_ByteOrder()
    {
        var header = new UtpPacketHeader(
            type: UtpPacketType.Syn,
            connectionId: 0x1234,
            timestampMicroseconds: 0xAABBCCDD,
            timestampDifferenceMicroseconds: 0x11223344,
            windowSize: 0x55667788,
            sequenceNumber: 0xABCD,
            ackNumber: 0xEF01,
            extension: 0);

        Span<byte> buffer = stackalloc byte[UtpPacketHeader.Size];
        header.WriteTo(buffer);

        buffer[0].Should().Be(0x41);
        buffer[1].Should().Be(0x00);
        BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]).Should().Be(0x1234);
        BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]).Should().Be(0xAABBCCDD);
        BinaryPrimitives.ReadUInt32BigEndian(buffer[8..]).Should().Be(0x11223344u);
        BinaryPrimitives.ReadUInt32BigEndian(buffer[12..]).Should().Be(0x55667788u);
        BinaryPrimitives.ReadUInt16BigEndian(buffer[16..]).Should().Be(0xABCD);
        BinaryPrimitives.ReadUInt16BigEndian(buffer[18..]).Should().Be(0xEF01);
    }
}
