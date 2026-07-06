using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using vTorrent.Bencode.Torrents;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>
/// BEP 52 Hashes message (type 22).
/// Payload: same header as HashRequest + (length + proofLayers) * 32B hash data.
/// </summary>
public readonly record struct HashesMessage
{
    public SHA256Hash PiecesRoot { get; init; }
    public int BaseLayer { get; init; }
    public int Index { get; init; }
    public int Length { get; init; }
    public int ProofLayers { get; init; }
    public IReadOnlyList<SHA256Hash> Hashes { get; init; }

    public int SerializedSize => SHA256Hash.Size + 4 * 4 + Hashes.Count * SHA256Hash.Size;

    public void WriteTo(Span<byte> buffer)
    {
        PiecesRoot.AsSpan().CopyTo(buffer);
        BinaryPrimitives.WriteInt32BigEndian(buffer[32..], BaseLayer);
        BinaryPrimitives.WriteInt32BigEndian(buffer[36..], Index);
        BinaryPrimitives.WriteInt32BigEndian(buffer[40..], Length);
        BinaryPrimitives.WriteInt32BigEndian(buffer[44..], ProofLayers);

        var offset = 48;
        foreach (var hash in Hashes)
        {
            hash.AsSpan().CopyTo(buffer[offset..]);
            offset += SHA256Hash.Size;
        }
    }

    public static HashesMessage Parse(ReadOnlySpan<byte> payload)
    {
        var root = new SHA256Hash(payload[..32]);
        var baseLayer = BinaryPrimitives.ReadInt32BigEndian(payload[32..]);
        var index = BinaryPrimitives.ReadInt32BigEndian(payload[36..]);
        var length = BinaryPrimitives.ReadInt32BigEndian(payload[40..]);
        var proofLayers = BinaryPrimitives.ReadInt32BigEndian(payload[44..]);

        var hashCount = (payload.Length - 48) / SHA256Hash.Size;
        var hashes = new SHA256Hash[hashCount];
        for (int i = 0; i < hashCount; i++)
            hashes[i] = new SHA256Hash(payload.Slice(48 + i * SHA256Hash.Size, SHA256Hash.Size));

        return new HashesMessage
        {
            PiecesRoot = root,
            BaseLayer = baseLayer,
            Index = index,
            Length = length,
            ProofLayers = proofLayers,
            Hashes = hashes,
        };
    }
}
