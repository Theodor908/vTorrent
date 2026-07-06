using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.Proxy;

public static class ProxyConnectorFactory
{
    public static IProxyConnector? Create(ProxySettings? settings)
    {
        if (settings == null || settings.Type == ProxyType.None)
            return null;

        return settings.Type switch
        {
            ProxyType.Socks4 => new Socks4ProxyConnector(settings),
            ProxyType.Socks5 => new Socks5ProxyConnector(settings, auth: false),
            ProxyType.Socks5Password => new Socks5ProxyConnector(settings, auth: true),
            ProxyType.Http => new HttpProxyConnector(settings, auth: false),
            ProxyType.HttpPassword => new HttpProxyConnector(settings, auth: true),
            _ => null
        };
    }
}
