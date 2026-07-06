using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Represents a PEX (Peer Exchange) message.
/// PEX messages are bencoded dictionaries containing added/dropped peers.
/// </summary>
public class PexMessage
{
    /// <summary>
    /// Maximum number of peers per PEX message (from libtorrent).
    /// </summary>
    public const int MaxPeerEntries = 100;

    /// <summary>
    /// IPv4 peers that have been added since the last PEX message.
    /// </summary>
    public List<IPEndPoint> Added { get; set; } = new();

    /// <summary>
    /// Flags for each added IPv4 peer.
    /// </summary>
    public List<PexFlags> AddedFlags { get; set; } = new();

    /// <summary>
    /// IPv4 peers that have been dropped since the last PEX message.
    /// </summary>
    public List<IPEndPoint> Dropped { get; set; } = new();

    /// <summary>
    /// IPv6 peers that have been added since the last PEX message.
    /// </summary>
    public List<IPEndPoint> Added6 { get; set; } = new();

    /// <summary>
    /// Flags for each added IPv6 peer.
    /// </summary>
    public List<PexFlags> Added6Flags { get; set; } = new();

    /// <summary>
    /// IPv6 peers that have been dropped since the last PEX message.
    /// </summary>
    public List<IPEndPoint> Dropped6 { get; set; } = new();

    /// <summary>
    /// Encodes this PEX message to bencoded bytes.
    /// </summary>
    public byte[] Encode()
    {
        var dict = new BDictionary();

        // IPv4 peers
        if (Added.Count > 0)
        {
            dict.AddBytes("added", EncodeCompactPeersIPv4(Added));
            dict.AddBytes("added.f", EncodeFlags(AddedFlags, Added.Count));
        }

        if (Dropped.Count > 0)
        {
            dict.AddBytes("dropped", EncodeCompactPeersIPv4(Dropped));
        }

        // IPv6 peers
        if (Added6.Count > 0)
        {
            dict.AddBytes("added6", EncodeCompactPeersIPv6(Added6));
            dict.AddBytes("added6.f", EncodeFlags(Added6Flags, Added6.Count));
        }

        if (Dropped6.Count > 0)
        {
            dict.AddBytes("dropped6", EncodeCompactPeersIPv6(Dropped6));
        }

        // Encode to bytes
        var size = dict.GetSizeInBytes();
        var buffer = new byte[size];
        dict.EncodeTo(buffer);
        return buffer;
    }

    /// <summary>
    /// Parses a PEX message from bencoded data.
    /// </summary>
    public static PexMessage Parse(ReadOnlySpan<byte> data)
    {
        var parser = new BencodeParser();
        var obj = parser.Parse(data, out _);

        if (obj is not BDictionary dict)
            throw new InvalidDataException("PEX message must be a bencoded dictionary");

        var message = new PexMessage();

        // Parse IPv4 added peers
        if (dict.TryGetValue("added", out var addedObj) && addedObj is BString addedStr)
        {
            message.Added = DecodeCompactPeersIPv4(addedStr.Value.ToArray());
        }

        // Parse IPv4 added flags
        if (dict.TryGetValue("added.f", out var addedFlagsObj) && addedFlagsObj is BString addedFlagsStr)
        {
            message.AddedFlags = DecodeFlags(addedFlagsStr.Value.ToArray());
        }

        // Ensure flags list matches peers list
        while (message.AddedFlags.Count < message.Added.Count)
            message.AddedFlags.Add(PexFlags.None);

        // Parse IPv4 dropped peers
        if (dict.TryGetValue("dropped", out var droppedObj) && droppedObj is BString droppedStr)
        {
            message.Dropped = DecodeCompactPeersIPv4(droppedStr.Value.ToArray());
        }

        // Parse IPv6 added peers
        if (dict.TryGetValue("added6", out var added6Obj) && added6Obj is BString added6Str)
        {
            message.Added6 = DecodeCompactPeersIPv6(added6Str.Value.ToArray());
        }

        // Parse IPv6 added flags
        if (dict.TryGetValue("added6.f", out var added6FlagsObj) && added6FlagsObj is BString added6FlagsStr)
        {
            message.Added6Flags = DecodeFlags(added6FlagsStr.Value.ToArray());
        }

        // Ensure flags list matches peers list
        while (message.Added6Flags.Count < message.Added6.Count)
            message.Added6Flags.Add(PexFlags.None);

        // Parse IPv6 dropped peers
        if (dict.TryGetValue("dropped6", out var dropped6Obj) && dropped6Obj is BString dropped6Str)
        {
            message.Dropped6 = DecodeCompactPeersIPv6(dropped6Str.Value.ToArray());
        }

        return message;
    }

    /// <summary>
    /// Encodes IPv4 peers in compact format (6 bytes per peer: 4 IP + 2 port).
    /// </summary>
    private static byte[] EncodeCompactPeersIPv4(List<IPEndPoint> peers)
    {
        var result = new byte[peers.Count * 6];
        for (int i = 0; i < peers.Count; i++)
        {
            var peer = peers[i];
            var ip = peer.Address.MapToIPv4().GetAddressBytes();

            if (ip.Length != 4)
                throw new InvalidOperationException($"Expected IPv4 address, got {peer.Address}");

            Buffer.BlockCopy(ip, 0, result, i * 6, 4);
            result[i * 6 + 4] = (byte)(peer.Port >> 8);
            result[i * 6 + 5] = (byte)(peer.Port & 0xFF);
        }
        return result;
    }

    /// <summary>
    /// Encodes IPv6 peers in compact format (18 bytes per peer: 16 IP + 2 port).
    /// </summary>
    private static byte[] EncodeCompactPeersIPv6(List<IPEndPoint> peers)
    {
        var result = new byte[peers.Count * 18];
        for (int i = 0; i < peers.Count; i++)
        {
            var peer = peers[i];
            var ip = peer.Address.GetAddressBytes();

            if (ip.Length != 16)
                throw new InvalidOperationException($"Expected IPv6 address, got {peer.Address}");

            Buffer.BlockCopy(ip, 0, result, i * 18, 16);
            result[i * 18 + 16] = (byte)(peer.Port >> 8);
            result[i * 18 + 17] = (byte)(peer.Port & 0xFF);
        }
        return result;
    }

    /// <summary>
    /// Decodes IPv4 peers from compact format.
    /// </summary>
    private static List<IPEndPoint> DecodeCompactPeersIPv4(byte[] data)
    {
        var peers = new List<IPEndPoint>();
        for (int i = 0; i + 6 <= data.Length; i += 6)
        {
            var ipBytes = new byte[4];
            Buffer.BlockCopy(data, i, ipBytes, 0, 4);
            var ip = new IPAddress(ipBytes);
            var port = (data[i + 4] << 8) | data[i + 5];
            peers.Add(new IPEndPoint(ip, port));
        }
        return peers;
    }

    /// <summary>
    /// Decodes IPv6 peers from compact format.
    /// </summary>
    private static List<IPEndPoint> DecodeCompactPeersIPv6(byte[] data)
    {
        var peers = new List<IPEndPoint>();
        for (int i = 0; i + 18 <= data.Length; i += 18)
        {
            var ipBytes = new byte[16];
            Buffer.BlockCopy(data, i, ipBytes, 0, 16);
            var ip = new IPAddress(ipBytes);
            var port = (data[i + 16] << 8) | data[i + 17];
            peers.Add(new IPEndPoint(ip, port));
        }
        return peers;
    }

    /// <summary>
    /// Encodes PEX flags to bytes.
    /// </summary>
    private static byte[] EncodeFlags(List<PexFlags> flags, int count)
    {
        var result = new byte[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = i < flags.Count ? (byte)flags[i] : (byte)PexFlags.None;
        }
        return result;
    }

    /// <summary>
    /// Decodes PEX flags from bytes.
    /// </summary>
    private static List<PexFlags> DecodeFlags(byte[] data)
    {
        var flags = new List<PexFlags>(data.Length);
        foreach (var b in data)
        {
            flags.Add((PexFlags)b);
        }
        return flags;
    }

    /// <summary>
    /// Returns total peer count in this message.
    /// </summary>
    public int TotalPeerCount => Added.Count + Dropped.Count + Added6.Count + Dropped6.Count;

    public override string ToString()
    {
        return $"PexMessage[Added: {Added.Count}, Dropped: {Dropped.Count}, Added6: {Added6.Count}, Dropped6: {Dropped6.Count}]";
    }
}

/// <summary>
/// Represents a peer entry discovered via PEX.
/// </summary>
public readonly record struct PexPeerEntry(IPEndPoint EndPoint, PexFlags Flags)
{
    public bool IsSeed => (Flags & PexFlags.Seed) != 0;
    public bool SupportsEncryption => (Flags & PexFlags.Encryption) != 0;
    public bool SupportsUtp => (Flags & PexFlags.Utp) != 0;
    public bool SupportsHolepunch => (Flags & PexFlags.Holepunch) != 0;
}
