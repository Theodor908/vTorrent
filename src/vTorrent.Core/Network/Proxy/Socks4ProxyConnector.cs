using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.Proxy;

public class Socks4ProxyConnector : IProxyConnector
{
    private readonly ProxySettings _settings;

    public Socks4ProxyConnector(ProxySettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<ITransportStream> ConnectThroughProxyAsync(
        string hostname, int port, CancellationToken ct = default)
    {
        var addresses = await Dns.GetHostAddressesAsync(hostname, ct).ConfigureAwait(false);
        var ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"Could not resolve {hostname} to IPv4 address");

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_settings.Hostname, _settings.Port, ct).ConfigureAwait(false);
            var stream = client.GetStream();

            var request = EncodeConnectRequest(ipv4, port, _settings.Username ?? "");
            await stream.WriteAsync(request, ct).ConfigureAwait(false);

            var response = new byte[8];
            int read = 0;
            while (read < 8)
            {
                int n = await stream.ReadAsync(response.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0) throw new InvalidOperationException("SOCKS4 proxy closed connection during handshake");
                read += n;
            }

            if (response[0] != 0x00)
                throw new InvalidOperationException($"SOCKS4 response: invalid VN byte 0x{response[0]:X2} (expected 0x00)");
            if (response[1] != 0x5A)
                throw new InvalidOperationException($"SOCKS4 request rejected: CD=0x{response[1]:X2}");

            return new ProxyTransportStream(client, new IPEndPoint(ipv4, port));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static byte[] EncodeConnectRequest(IPAddress ip, int port, string userId = "")
    {
        var userBytes = Encoding.ASCII.GetBytes(userId);
        var request = new byte[9 + userBytes.Length];
        request[0] = 0x04;
        request[1] = 0x01;
        request[2] = (byte)(port >> 8);
        request[3] = (byte)(port & 0xFF);
        ip.GetAddressBytes().CopyTo(request, 4);
        userBytes.CopyTo(request, 8);
        request[8 + userBytes.Length] = 0x00;
        return request;
    }
}
