using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.Proxy;

public class HttpProxyConnector : IProxyConnector
{
    private readonly ProxySettings _settings;
    private readonly bool _useAuth;

    public HttpProxyConnector(ProxySettings settings, bool auth)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _useAuth = auth;
    }

    public async Task<ITransportStream> ConnectThroughProxyAsync(
        string hostname, int port, CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_settings.Hostname, _settings.Port, ct).ConfigureAwait(false);
            var stream = client.GetStream();

            var request = BuildConnectRequest(hostname, port);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct).ConfigureAwait(false);

            var responseBuf = new byte[4096];
            int totalRead = 0;
            while (totalRead < responseBuf.Length)
            {
                int n = await stream.ReadAsync(responseBuf.AsMemory(totalRead), ct).ConfigureAwait(false);
                if (n == 0) throw new InvalidOperationException("HTTP proxy closed connection during handshake");
                totalRead += n;

                var responseStr = Encoding.ASCII.GetString(responseBuf, 0, totalRead);
                if (responseStr.Contains("\r\n\r\n") || responseStr.Contains("\n\n"))
                {
                    var statusLine = responseStr.Split('\n')[0].Trim();
                    if (!statusLine.Contains(" 200 "))
                        throw new InvalidOperationException($"HTTP CONNECT failed: {statusLine}");
                    return new ProxyTransportStream(client, new System.Net.DnsEndPoint(hostname, port));
                }
            }

            throw new InvalidOperationException("HTTP proxy response too large or malformed");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal string BuildConnectRequest(string hostname, int port)
    {
        var sb = new StringBuilder();
        sb.Append($"CONNECT {hostname}:{port} HTTP/1.1\r\n");
        sb.Append($"Host: {hostname}:{port}\r\n");

        if (_useAuth && !string.IsNullOrEmpty(_settings.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}"));
            sb.Append($"Proxy-Authorization: Basic {credentials}\r\n");
        }

        sb.Append("\r\n");
        return sb.ToString();
    }
}
