using System;

namespace vTorrent.Abstractions.Interfaces;

/// <summary>
/// VPN status observable — Desktop binds to this for status indicator.
/// </summary>
public interface IVpnStatus
{
    bool IsBlocking { get; }
    bool IsVpnInterfaceUp { get; }
    event Action<bool>? BlockingStateChanged;
}
