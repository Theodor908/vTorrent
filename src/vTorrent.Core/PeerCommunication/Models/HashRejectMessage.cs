using System;
using System.Buffers.Binary;
using vTorrent.Bencode.Torrents;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>
/// BEP 52 Hash Reject message (type 23). Same payload layout as HashRequest.
/// </summary>
public readonly record struct HashRejectMessage
{
    public SHA256Hash PiecesRoot { get; init; }
    public int BaseLayer { get; init; }
    public int Index { get; init; }
    public int Length { get; init; }
    public int ProofLayers { get; init; }

    public int SerializedSize => SHA256Hash.Size + 4 * 4; // 48

    public void WriteTo(Span<byte> buffer)
    {
        PiecesRoot.AsSpan().CopyTo(buffer);
        BinaryPrimitives.WriteInt32BigEndian(buffer[32..], BaseLayer);
        BinaryPrimitives.WriteInt32BigEndian(buffer[36..], Index);
        BinaryPrimitives.WriteInt32BigEndian(buffer[40..], Length);
        BinaryPrimitives.WriteInt32BigEndian(buffer[44..], ProofLayers);
    }

    public static HashRejectMessage Parse(ReadOnlySpan<byte> payload)
    {
        return new HashRejectMessage
        {
            PiecesRoot = new SHA256Hash(payload[..32]),
            BaseLayer = BinaryPrimitives.ReadInt32BigEndian(payload[32..]),
            Index = BinaryPrimitives.ReadInt32BigEndian(payload[36..]),
            Length = BinaryPrimitives.ReadInt32BigEndian(payload[40..]),
            ProofLayers = BinaryPrimitives.ReadInt32BigEndian(payload[44..]),
        };
    }
}
