using System;

namespace vTorrent.Core.Network;

/// <summary>
/// Thrown when a connection attempt is blocked by the VPN kill-switch.
/// </summary>
public class VpnDisconnectedException : Exception
{
    public VpnDisconnectedException()
        : base("VPN interface is down — connections blocked by kill-switch") { }
}
