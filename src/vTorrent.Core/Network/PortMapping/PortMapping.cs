using System;
using System.Net;

namespace vTorrent.Core.Network.PortMapping;

public enum PortMapProtocol : byte { Tcp = 6, Udp = 17 }
public enum PortMapTransport : byte { NatPmp, Upnp }

public sealed class PortMapping
{
    public int Id { get; init; }
    public PortMapProtocol Protocol { get; init; }
    public PortMapTransport Transport { get; init; } = PortMapTransport.NatPmp;
    public int InternalPort { get; init; }
    public int ExternalPort { get; set; }
    public IPAddress? ExternalAddress { get; set; }
    public DateTime Expiry { get; set; }
}
