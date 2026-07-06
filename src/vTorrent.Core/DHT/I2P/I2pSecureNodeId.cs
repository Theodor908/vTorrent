using System;
using System.Security.Cryptography;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.DHT.I2P;

/// <summary>
/// Generates and validates I2P DHT secure Node IDs per the I2P DHT spec.
/// First 4 bytes = destHash[0..4], next 2 bytes = destHash[4..6] XOR port.
/// Remaining 14 bytes are random.
/// </summary>
public static class I2pSecureNodeId
{
    public const int NodeIdLength = 20;

    public static byte[] Generate(I2pDestination destination, ushort port)
    {
        var destHash = destination.ToCompact();
        var nodeId = new byte[NodeIdLength];

        // First 4 bytes match destination hash
        nodeId[0] = destHash[0];
        nodeId[1] = destHash[1];
        nodeId[2] = destHash[2];
        nodeId[3] = destHash[3];

        // Bytes 4-5: destHash XOR port (big-endian)
        nodeId[4] = (byte)(destHash[4] ^ (port >> 8));
        nodeId[5] = (byte)(destHash[5] ^ (port & 0xFF));

        // Remaining 14 bytes: random
        RandomNumberGenerator.Fill(nodeId.AsSpan(6));

        return nodeId;
    }

    public static bool Validate(byte[] nodeId, I2pDestination destination, ushort port)
    {
        if (nodeId == null || nodeId.Length != NodeIdLength) return false;

        var destHash = destination.ToCompact();

        // Check first 4 bytes
        if (nodeId[0] != destHash[0] || nodeId[1] != destHash[1] ||
            nodeId[2] != destHash[2] || nodeId[3] != destHash[3])
            return false;

        // Check bytes 4-5 (XOR with port)
        if (nodeId[4] != (byte)(destHash[4] ^ (port >> 8))) return false;
        if (nodeId[5] != (byte)(destHash[5] ^ (port & 0xFF))) return false;

        return true;
    }
}
