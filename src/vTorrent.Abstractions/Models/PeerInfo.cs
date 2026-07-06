using System;
using System.Net;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Abstractions.Models;

public class PeerInfo
{
    public IPAddress IpAddress { get; }

    public int Port { get; }
    public byte[] PeerId { get; set; }
    public IPEndPoint EndPoint => new IPEndPoint(IpAddress, Port);
    public string Source { get; set; }
    public DateTime DiscoveredAt { get; }
    public bool IsSeed { get; set; }

    /// <summary>MSE encryption support state (session-only, not persisted).</summary>
    public MsePeerEncryptionSupport EncryptionSupport { get; set; }

    /// <summary>
    /// Whether this peer supports holepunch NAT traversal (BEP 55).
    /// Populated from PexFlags.Holepunch (0x08) when discovered via PEX.
    /// </summary>
    public bool SupportsHolepunch { get; set; }

    public I2pDestination? Destination { get; }
    public bool IsI2p => Destination != null;

    public PeerInfo(IPAddress ipAddress, int port, byte[] peerId = null, string source = null)
    {
        IpAddress = ipAddress;
        Port = port;
        PeerId = peerId;
        Source = source ?? "unknown";
        DiscoveredAt = DateTime.UtcNow;
    }

    private PeerInfo(I2pDestination destination, string source)
    {
        IpAddress = IPAddress.None;
        Port = 0;
        Destination = destination;
        Source = source ?? "unknown";
        DiscoveredAt = DateTime.UtcNow;
    }

    public static PeerInfo FromEndPoint(IPEndPoint endPoint, byte[] peerId = null, string source = null)
    {
        return new PeerInfo(endPoint.Address, endPoint.Port, peerId, source);
    }

    public static PeerInfo Incoming(EndPoint endPoint)
    {
        var ipep = (IPEndPoint)endPoint;
        return new PeerInfo(ipep.Address, ipep.Port, source: "incoming");
    }

    public static PeerInfo FromI2p(I2pDestination destination, string source = "i2p")
    {
        return new PeerInfo(destination, source);
    }

    public static PeerInfo IncomingI2p(I2pEndPoint endPoint)
    {
        return new PeerInfo(endPoint.Destination, source: "incoming");
    }

    public static PeerInfo FromCompactFormat(byte[] data, int offset = 0, string source = "tracker")
    {
        if(data == null || data.Length < offset * 6)
        {
            throw new ArgumentException("Invalid compact peer data");
        }

        byte[] ipBytes = new byte[4];
        Buffer.BlockCopy(data, offset, ipBytes, 0, 4);
        IPAddress ip = new IPAddress(ipBytes);

        int port = (data[offset + 4] << 8 | data[offset + 5]);

        return new PeerInfo(ip, port, source: source);
    }

    public static PeerInfo[] FromCompactPeerList(byte[] data, int offset = 0, string source = "tracker")
    {
        if(data == null || data.Length % 6 != 0)
        {
            throw new ArgumentException("Invalid compact peer list");
        }

        int peerCount = data.Length / 6;
        PeerInfo[] peers = new PeerInfo[peerCount];

        for (int i = 0; i < peerCount; i++)
        {
            peers[i] = FromCompactFormat(data, i * 6, source);
        }

        return peers;
    }

    /// <summary>
    /// Parses a peer from IPv6 compact format (18 bytes).
    /// Following BEP 7 specification: 16 bytes IP + 2 bytes port (big-endian).
    /// </summary>
    public static PeerInfo FromCompactFormatIPv6(byte[] data, int offset = 0, string source = "tracker")
    {
        if (data == null || data.Length < offset + 18)
        {
            throw new ArgumentException("Invalid compact IPv6 peer data");
        }

        byte[] ipBytes = new byte[16];
        Buffer.BlockCopy(data, offset, ipBytes, 0, 16);
        IPAddress ip = new IPAddress(ipBytes);

        int port = (data[offset + 16] << 8) | data[offset + 17];

        return new PeerInfo(ip, port, source: source);
    }

    /// <summary>
    /// Parses a list of peers from IPv6 compact format.
    /// Each peer is 18 bytes: 16 bytes IP + 2 bytes port.
    /// </summary>
    public static PeerInfo[] FromCompactPeerListIPv6(byte[] data, int offset = 0, string source = "tracker")
    {
        if (data == null || data.Length % 18 != 0)
        {
            throw new ArgumentException("Invalid compact IPv6 peer list");
        }

        int peerCount = data.Length / 18;
        PeerInfo[] peers = new PeerInfo[peerCount];

        for (int i = 0; i < peerCount; i++)
        {
            peers[i] = FromCompactFormatIPv6(data, i * 18, source);
        }

        return peers;
    }

    public byte[] ToCompactFormatIPv4()
    {
        byte[] compact = new byte[6];

        byte[] ipBytes = IpAddress.GetAddressBytes();

        if (ipBytes.Length != 4)
            throw new InvalidOperationException("Invalid IPv4 address");

        Buffer.BlockCopy(ipBytes,0, compact, 0, 4);

        compact[4] = (byte)(Port >> 8);
        compact[5] = (byte)(Port & 0xff);

        return compact;
    }

    /// <summary>
    /// Converts this peer info to IPv6 compact format (18 bytes).
    /// Following BEP 7 specification: 16 bytes IP + 2 bytes port (big-endian).
    /// </summary>
    public byte[] ToCompactFormatIPv6()
    {
        byte[] compact = new byte[18];

        byte[] ipBytes = IpAddress.GetAddressBytes();

        if (ipBytes.Length != 16)
            throw new InvalidOperationException("Invalid IPv6 address");

        Buffer.BlockCopy(ipBytes, 0, compact, 0, 16);

        compact[16] = (byte)(Port >> 8);
        compact[17] = (byte)(Port & 0xff);

        return compact;
    }

    public byte[] ToCompactFormatI2p()
    {
        if (!IsI2p) throw new InvalidOperationException("Not an I2P peer");
        return Destination!.ToCompact();
    }

    public static PeerInfo FromCompactFormatI2p(byte[] data, int offset = 0, string source = "tracker")
    {
        if (data == null || data.Length < offset + 32)
            throw new ArgumentException("Invalid I2P compact peer data");

        var hashBytes = new byte[32];
        Buffer.BlockCopy(data, offset, hashBytes, 0, 32);
        return FromI2p(I2pDestination.FromHash(hashBytes), source);
    }

    public static PeerInfo[] FromCompactPeerListI2p(byte[] data, int offset = 0, string source = "tracker")
    {
        if (data == null || (data.Length - offset) % 32 != 0)
            throw new ArgumentException("Invalid I2P compact peer list");

        int peerCount = (data.Length - offset) / 32;
        var peers = new PeerInfo[peerCount];
        for (int i = 0; i < peerCount; i++)
            peers[i] = FromCompactFormatI2p(data, offset + i * 32, source);
        return peers;
    }

    public EndPoint NetworkEndPoint => IsI2p
        ? new I2pEndPoint(Destination!)
        : new IPEndPoint(IpAddress, Port);

    public string DisplayAddress => IsI2p
        ? Destination!.ToBase32()[..12] + "..."
        : $"{IpAddress}:{Port}";

    public override bool Equals(object obj)
    {
        if (obj is PeerInfo other)
        {
            if (IsI2p != other.IsI2p) return false;
            if (IsI2p) return Destination!.Equals(other.Destination);
            return IpAddress.Equals(other.IpAddress) && Port == other.Port;
        }
        return false;
    }

    public override int GetHashCode() => IsI2p
        ? Destination!.GetHashCode()
        : HashCode.Combine(IpAddress, Port);

    public override string ToString()
    {
        if (IsI2p)
        {
            string seedStr = IsSeed ? " [SEED]" : "";
            return $"i2p:{Destination}{seedStr} (from {Source})";
        }
        string peerIdStr = PeerId != null ? $" [{System.Text.Encoding.ASCII.GetString(PeerId)}]" : "";
        string seedStr2 = IsSeed ? " [SEED]" : "";
        return $"{IpAddress}:{Port}{peerIdStr}{seedStr2} (from {Source})";
    }
}
