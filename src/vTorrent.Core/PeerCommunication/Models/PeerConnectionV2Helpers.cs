using System;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>
/// BEP 52 reserved byte helpers for v2 handshake negotiation.
/// Bit 4 of reserved byte 7 (the 4th most significant bit of the last byte).
/// </summary>
public static class PeerConnectionV2Helpers
{
    private const byte V2SupportBit = 0x10; // bit 4

    public static bool SupportsV2(ReadOnlySpan<byte> reservedBytes)
    {
        if (reservedBytes.Length < 8) return false;
        return (reservedBytes[7] & V2SupportBit) != 0;
    }

    public static void SetV2Support(Span<byte> reservedBytes)
    {
        if (reservedBytes.Length < 8)
            throw new ArgumentException("Reserved bytes must be 8 bytes");
        reservedBytes[7] |= V2SupportBit;
    }
}
