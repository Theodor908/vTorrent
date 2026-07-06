namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Protocol feature settings
/// </summary>
public class ProtocolSettings
{
    /// <summary>
    /// Enable DHT (Distributed Hash Table) for peer discovery
    /// </summary>
    public bool EnableDht { get; set; } = true;

    /// <summary>
    /// Enable LSD (Local Service Discovery) for LAN peers
    /// </summary>
    public bool EnableLsd { get; set; } = true;

    /// <summary>
    /// Enable PEX (Peer Exchange) protocol
    /// </summary>
    public bool EnablePex { get; set; } = true;

    /// <summary>
    /// DHT bootstrap nodes
    /// </summary>
    public string[] DhtBootstrapNodes { get; set; } =
    {
        "router.bittorrent.com:6881",
        "router.utorrent.com:6881",
        "dht.transmissionbt.com:6881"
    };

    /// <summary>
    /// Encryption settings
    /// </summary>
    public EncryptionSettings Encryption { get; set; } = new();

    /// <summary>
    /// Client user agent string
    /// </summary>
    public string UserAgent { get; set; } = "vTorrent/1.0";

    /// <summary>
    /// Peer ID prefix (Azureus-style)
    /// </summary>
    public string PeerIdPrefix { get; set; } = "-VT0100-";

    /// <summary>
    /// Enable holepunch NAT traversal (BEP 55). Allows connecting to peers behind NAT
    /// via relay peers. Requires uTP to be available.
    /// </summary>
    public bool EnableHolepunch { get; set; } = true;
}
