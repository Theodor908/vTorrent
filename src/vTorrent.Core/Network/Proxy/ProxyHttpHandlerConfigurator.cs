using System.Net;
using System.Net.Http;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.Proxy;

/// <summary>
/// Applies vTorrent's proxy settings to a <see cref="SocketsHttpHandler"/> so that outbound
/// HTTP(S) traffic (trackers, web seeds / HTTP seeds) is routed through the configured proxy
/// instead of leaking out over the real connection.
///
/// HTTP proxies use the native <see cref="WebProxy"/> path; SOCKS proxies use a
/// <see cref="SocketsHttpHandler.ConnectCallback"/> that tunnels through
/// <see cref="ProxyConnectorFactory"/> (mirrors <c>TrackerHttpClientFactory.CreateHandler</c>).
///
/// Settings are snapshotted at handler-creation time, matching the tracker factory.
/// </summary>
public static class ProxyHttpHandlerConfigurator
{
    /// <summary>
    /// Configures <paramref name="handler"/> for the given proxy settings.
    /// When <paramref name="enabledForConnectionType"/> is false, the proxy type is
    /// <see cref="ProxyType.None"/>, or <paramref name="settings"/> is null, the handler is left
    /// direct (<c>UseProxy = false</c>, no connect callback).
    /// </summary>
    /// <param name="handler">The handler to mutate.</param>
    /// <param name="settings">Snapshot of the current proxy settings (may be null).</param>
    /// <param name="enabledForConnectionType">
    /// Whether this connection category should be proxied (e.g. <c>ProxyPeerConnections</c> for
    /// web seeds, <c>ProxyTrackerConnections</c> for trackers).
    /// </param>
    public static void Configure(
        SocketsHttpHandler handler, ProxySettings? settings, bool enabledForConnectionType)
    {
        if (settings == null || settings.Type == ProxyType.None || !enabledForConnectionType)
        {
            handler.UseProxy = false;
            return;
        }

        if (settings.Type == ProxyType.Http || settings.Type == ProxyType.HttpPassword)
        {
            // Native HTTP proxy support via SocketsHttpHandler.
            var webProxy = new WebProxy(settings.Hostname, settings.Port);
            if (settings.Type == ProxyType.HttpPassword)
                webProxy.Credentials = new NetworkCredential(settings.Username, settings.Password);
            handler.Proxy = webProxy;
            handler.UseProxy = true;
            return;
        }

        // SOCKS proxy — tunnel each connection through the proxy connector (.NET 7+ callback).
        handler.UseProxy = false;
        var proxyConnector = ProxyConnectorFactory.Create(settings);
        if (proxyConnector != null)
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                var stream = await proxyConnector.ConnectThroughProxyAsync(
                    context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
                return ((ProxyTransportStream)stream).AsNetworkStream();
            };
        }
    }
}
