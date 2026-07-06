using System;
using System.Collections.Generic;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// I2P Peer Exchange extension (i2p_pex). Uses 32-byte destination hashes
/// instead of 6/18-byte IP+port compact format. Only the seed flag (0x01) is meaningful.
/// </summary>
public sealed class I2pPexExtension
{
    public const string Name = "i2p_pex";
    public const int CompactPeerSize = 32;
    public const byte FlagSeed = 0x01;

    public string ExtensionName => Name;

    /// <summary>
    /// Encodes I2P peers into compact 32-byte-per-peer format.
    /// </summary>
    public static byte[] EncodeI2pPeers(IReadOnlyList<PeerInfo> peers)
    {
        var result = new byte[peers.Count * CompactPeerSize];
        for (int i = 0; i < peers.Count; i++)
        {
            var compact = peers[i].Destination!.ToCompact();
            Buffer.BlockCopy(compact, 0, result, i * CompactPeerSize, CompactPeerSize);
        }
        return result;
    }

    /// <summary>
    /// Decodes compact 32-byte-per-peer I2P peer data.
    /// </summary>
    public static PeerInfo[] DecodeI2pPeers(byte[] data)
    {
        if (data == null || data.Length == 0)
            return Array.Empty<PeerInfo>();

        if (data.Length % CompactPeerSize != 0)
            throw new ArgumentException($"I2P PEX data length {data.Length} not divisible by {CompactPeerSize}");

        return PeerInfo.FromCompactPeerListI2p(data, source: "pex");
    }

    /// <summary>
    /// Encodes PEX flags for I2P peers. Only seed flag (0x01) is used.
    /// </summary>
    public static byte[] EncodeFlags(IReadOnlyList<PeerInfo> peers)
    {
        var flags = new byte[peers.Count];
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].IsSeed)
                flags[i] |= FlagSeed;
        }
        return flags;
    }
}
