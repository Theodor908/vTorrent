namespace vTorrent.Abstractions.Settings;

/// <summary>
/// VPN kill-switch configuration.
/// Interface binding uses ConnectionSettings.OutgoingInterface.
/// </summary>
public class VpnSettings
{
    /// <summary>Enable kill-switch — blocks all traffic when VPN interface goes down.</summary>
    public bool KillSwitchEnabled { get; set; } = false;

    /// <summary>
    /// Interface name to monitor for kill-switch (e.g., "wg0", "tun0", "TAP-Windows Adapter").
    /// When empty and KillSwitchEnabled is true, defaults to ConnectionSettings.OutgoingInterface.
    /// </summary>
    public string VpnInterfaceName { get; set; } = "";

}
