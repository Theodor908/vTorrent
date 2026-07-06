using System.Net;

namespace vTorrent.Core.Network.PortMapping;

/// <summary>
/// Discovered UPnP IGD device with its control endpoint.
/// </summary>
internal sealed class UpnpDevice
{
    public required string Url { get; init; }
    public required string ControlUrl { get; set; }
    public required string ServiceType { get; init; }
    public required string Hostname { get; init; }
    public required int Port { get; init; }
    public required string Path { get; set; }
    public string? Model { get; init; }
    public bool UseLeaseDuration { get; set; } = true;
    public bool SupportsSpecificExternal { get; set; } = true;
    public IPAddress? ExternalIp { get; set; }
    public bool Disabled { get; set; }
}
