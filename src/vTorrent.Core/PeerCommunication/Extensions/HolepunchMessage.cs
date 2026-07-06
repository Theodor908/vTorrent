using System.Net;

namespace vTorrent.Core.PeerCommunication.Extensions;

public enum HolepunchMessageType : byte
{
    Rendezvous = 0x00,
    Connect = 0x01,
    Error = 0x02
}

public enum AddressType : byte
{
    IPv4 = 0x00,
    IPv6 = 0x01
}

public enum HolepunchError : int
{
    None = 0,
    NoSuchPeer = 1,
    NotConnected = 2,
    NoSupport = 3,
    NoSelf = 4
}

/// <summary>
/// Parsed BEP 55 holepunch message.
/// Wire format: msg_type(1) + addr_type(1) + addr(4|16) + port(2) + err_code(4)
/// Total: 12 bytes (IPv4) or 24 bytes (IPv6).
/// </summary>
public record HolepunchMessage(
    HolepunchMessageType Type,
    AddressType AddrType,
    IPEndPoint Endpoint,
    HolepunchError ErrorCode
);
