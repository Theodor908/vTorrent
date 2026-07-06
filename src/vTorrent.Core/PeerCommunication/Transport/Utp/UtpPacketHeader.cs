using System;
using System.Buffers.Binary;

namespace vTorrent.Core.PeerCommunication.Transport.Utp;

/// <summary>
/// BEP 29 uTP packet header (20 bytes, big-endian, network byte order).
/// Immutable value type — zero allocation for parse/serialize.
/// </summary>
public readonly struct UtpPacketHeader
{
    public const int Size = 20;
    public const byte ProtocolVersion = 1;

    public UtpPacketType Type { get; }
    public byte Extension { get; }
    public ushort ConnectionId { get; }
    public uint TimestampMicroseconds { get; }
    public uint TimestampDifferenceMicroseconds { get; }
    public uint WindowSize { get; }
    public ushort SequenceNumber { get; }
    public ushort AckNumber { get; }

    public UtpPacketHeader(
        UtpPacketType type,
        ushort connectionId,
        uint timestampMicroseconds,
        uint timestampDifferenceMicroseconds,
        uint windowSize,
        ushort sequenceNumber,
        ushort ackNumber,
        byte extension = 0)
    {
        Type = type;
        Extension = extension;
        ConnectionId = connectionId;
        TimestampMicroseconds = timestampMicroseconds;
        TimestampDifferenceMicroseconds = timestampDifferenceMicroseconds;
        WindowSize = windowSize;
        SequenceNumber = sequenceNumber;
        AckNumber = ackNumber;
    }

    public void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes", nameof(buffer));

        buffer[0] = (byte)(((byte)Type << 4) | ProtocolVersion);
        buffer[1] = Extension;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], ConnectionId);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], TimestampMicroseconds);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..], TimestampDifferenceMicroseconds);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[12..], WindowSize);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..], SequenceNumber);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[18..], AckNumber);
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out UtpPacketHeader header)
    {
        header = default;

        if (data.Length < Size)
            return false;

        byte versionAndType = data[0];
        byte version = (byte)(versionAndType & 0x0F);
        byte type = (byte)(versionAndType >> 4);

        if (version != ProtocolVersion || type > (byte)UtpPacketType.Syn)
            return false;

        header = new UtpPacketHeader(
            type: (UtpPacketType)type,
            connectionId: BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
            timestampMicroseconds: BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            timestampDifferenceMicroseconds: BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
            windowSize: BinaryPrimitives.ReadUInt32BigEndian(data[12..]),
            sequenceNumber: BinaryPrimitives.ReadUInt16BigEndian(data[16..]),
            ackNumber: BinaryPrimitives.ReadUInt16BigEndian(data[18..]),
            extension: data[1]);

        return true;
    }
}
