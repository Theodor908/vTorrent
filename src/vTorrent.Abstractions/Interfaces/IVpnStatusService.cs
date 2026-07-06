using System;

namespace vTorrent.Abstractions.Interfaces;

/// <summary>
/// Live VPN kill-switch status for UI binding.
/// </summary>
public interface IVpnStatusService
{
    bool IsEnabled { get; }
    bool IsMonitoring { get; }
    bool IsBlocking { get; }
    string MonitoredInterface { get; }
    event Action<VpnStatusInfo>? StatusChanged;
}

/// <summary>
/// Snapshot of VPN kill-switch state for event consumers.
/// </summary>
public record VpnStatusInfo(bool IsEnabled, bool IsMonitoring, bool IsBlocking, string MonitoredInterface);
