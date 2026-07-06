namespace vTorrent.Abstractions.Models;

/// <summary>
/// Network interface info DTO for UI display.
/// </summary>
public class NetworkInterfaceInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public bool IsUp { get; set; }

    /// <summary>
    /// Formatted display text for ComboBox.
    /// </summary>
    public string DisplayText => string.IsNullOrEmpty(Name)
        ? Description
        : IsUp
            ? $"{Name} — {Description} ({(string.IsNullOrEmpty(IpAddress) ? "no IP" : IpAddress)})"
            : $"{Name} — {Description} (down)";

    public override string ToString() => DisplayText;
}
