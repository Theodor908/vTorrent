namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Proxy configuration. Supports SOCKS4, SOCKS5 (with optional auth), and HTTP CONNECT (with optional auth).
/// </summary>
public class ProxySettings
{
    public ProxyType Type { get; set; } = ProxyType.None;
    public string Hostname { get; set; } = "";
    public int Port { get; set; } = 0;
    public string Username { get; set; } = "";  // Stored plaintext in global.json (OS credential store deferred)
    public string Password { get; set; } = "";  // Stored plaintext in global.json
    public bool ProxyPeerConnections { get; set; } = true;
    public bool ProxyTrackerConnections { get; set; } = true;
    public bool ProxyDht { get; set; } = false;
    public bool ProxyHostnames { get; set; } = true;
}

public enum ProxyType
{
    None,
    Socks4,
    Socks5,
    Socks5Password,
    Http,
    HttpPassword
}
