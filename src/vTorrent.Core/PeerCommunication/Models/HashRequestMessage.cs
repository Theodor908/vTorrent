using System;
using System.Buffers.Binary;
using vTorrent.Bencode.Torrents;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>
/// BEP 52 Hash Request message (type 21).
/// Payload: pieces_root (32B) + base_layer (4B) + index (4B) + length (4B) + proof_layers (4B).
/// </summary>
public readonly record struct HashRequestMessage
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

    public static HashRequestMessage Parse(ReadOnlySpan<byte> payload)
    {
        return new HashRequestMessage
        {
            PiecesRoot = new SHA256Hash(payload[..32]),
            BaseLayer = BinaryPrimitives.ReadInt32BigEndian(payload[32..]),
            Index = BinaryPrimitives.ReadInt32BigEndian(payload[36..]),
            Length = BinaryPrimitives.ReadInt32BigEndian(payload[40..]),
            ProofLayers = BinaryPrimitives.ReadInt32BigEndian(payload[44..]),
        };
    }
}
