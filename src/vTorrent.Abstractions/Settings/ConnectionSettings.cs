namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Connection limits and network settings
/// </summary>
public class ConnectionSettings
{
    /// <summary>
    /// Maximum global connections across all torrents
    /// </summary>
    public int MaxGlobalConnections { get; set; } = 200;

    /// <summary>
    /// Maximum connections per torrent (default)
    /// </summary>
    public int MaxConnectionsPerTorrent { get; set; } = 200;

    /// <summary>
    /// Maximum upload slots per torrent (default)
    /// </summary>
    public int MaxUploadsPerTorrent { get; set; } = 4;

    /// <summary>
    /// Maximum half-open (connecting) connections
    /// </summary>
    public int MaxHalfOpenConnections { get; set; } = 10;

    /// <summary>
    /// Primary listen port for incoming connections
    /// </summary>
    public int ListenPort { get; set; } = 6881;

    /// <summary>
    /// Range of ports to try if primary is unavailable [start, end]
    /// </summary>
    public int[] ListenPortRange { get; set; } = { 6881, 6999 };

    /// <summary>
    /// Network interfaces to listen on (empty = all)
    /// </summary>
    public string[] ListenInterfaces { get; set; } = { "0.0.0.0", "[::]" };

    /// <summary>
    /// Enable UPnP for automatic port forwarding
    /// </summary>
    public bool EnableUpnp { get; set; } = true;

    /// <summary>
    /// Enable NAT-PMP for automatic port forwarding
    /// </summary>
    public bool EnableNatPmp { get; set; } = true;

    /// <summary>NAT-PMP/PCP lease duration in seconds. Default 3600 (1 hour).</summary>
    public int NatPmpLeaseSeconds { get; set; } = 3600;

    /// <summary>UPnP lease duration in seconds. 0 = permanent. Default 3600 (1 hour). libtorrent: upnp_lease_duration.</summary>
    public int UpnpLeaseSeconds { get; set; } = 3600;

    /// <summary>
    /// Ignore UPnP responses from devices not on the local subnet.
    /// Prevents talking to other people's routers on shared networks.
    /// libtorrent: upnp_ignore_nonrouters. Default: false.
    /// </summary>
    public bool UpnpIgnoreNonRouters { get; set; } = false;

    /// <summary>Path to IP filter blocklist file (.dat, .p2p, or .gz). Empty = no file filter.</summary>
    public string IpFilterFilePath { get; set; } = "";

    /// <summary>
    /// Outgoing network interface (empty = default)
    /// </summary>
    public string OutgoingInterface { get; set; } = "";

    /// <summary>Accept multiple connections from the same IP address. libtorrent default: false.</summary>
    public bool AllowMultipleConnectionsPerIp { get; set; } = false;

    /// <summary>Reject peer connections on privileged ports (below 1024). libtorrent default: false.</summary>
    public bool NoConnectPrivilegedPorts { get; set; } = false;

    /// <summary>Distribute connection attempts evenly over time instead of bursts. libtorrent default: true.</summary>
    public bool SmoothConnects { get; set; } = true;

    /// <summary>Custom port to report to trackers (0 = use listen port). libtorrent default: 0.</summary>
    public int AnnouncePort { get; set; } = 0;

    /// <summary>Block internationalized domain names (IDNA) in tracker/peer URLs. libtorrent default: false.</summary>
    public bool AllowIdna { get; set; } = false;

    /// <summary>
    /// Local Service Discovery (LSD) announce interval in seconds.
    /// libtorrent default: 300 (5 minutes).
    /// </summary>
    public int LsdAnnounceInterval { get; set; } = 300;

    /// <summary>Enable outgoing uTP peer connections. libtorrent: enable_outgoing_utp. Default: true.</summary>
    public bool EnableOutgoingUtp { get; set; } = true;

    /// <summary>Accept incoming uTP peer connections. libtorrent: enable_incoming_utp. Default: true.</summary>
    public bool EnableIncomingUtp { get; set; } = true;

    /// <summary>Enable outgoing TCP peer connections. libtorrent: enable_outgoing_tcp. Default: true.</summary>
    public bool EnableOutgoingTcp { get; set; } = true;

    /// <summary>Accept incoming TCP peer connections. libtorrent: enable_incoming_tcp. Default: true.</summary>
    public bool EnableIncomingTcp { get; set; } = true;

    /// <summary>Fall back to OS-chosen port (port 0) when the configured listen port fails to bind. libtorrent: listen_system_port_fallback. Default: true.</summary>
    public bool ListenSystemPortFallback { get; set; } = true;

    /// <summary>Monitor system network changes and re-evaluate listener bindings. libtorrent: enable_ip_notifier. Default: true.</summary>
    public bool EnableIpNotifier { get; set; } = true;

    /// <summary>Maximum connection attempts per second per torrent. libtorrent: connection_speed. Default: 30.</summary>
    public int ConnectionSpeed { get; set; } = 30;

}
