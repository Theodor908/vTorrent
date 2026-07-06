using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.Proxy;

public class Socks5ProxyConnector : IProxyConnector
{
    private readonly ProxySettings _settings;
    private readonly bool _useAuth;

    public Socks5ProxyConnector(ProxySettings settings, bool auth)
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
            await NegotiateAuthAsync(stream, ct).ConfigureAwait(false);
            await SendConnectAsync(stream, hostname, port, ct).ConfigureAwait(false);
            return new ProxyTransportStream(client, new DnsEndPoint(hostname, port));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<(TcpClient controlChannel, IPEndPoint relayEndpoint)> CreateUdpAssociationAsync(
        CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_settings.Hostname, _settings.Port, ct).ConfigureAwait(false);
            var stream = client.GetStream();
            await NegotiateAuthAsync(stream, ct).ConfigureAwait(false);

            var request = new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            await stream.WriteAsync(request, ct).ConfigureAwait(false);

            var relayEndpoint = await ReadConnectResponseAsync(stream, ct).ConfigureAwait(false);
            return (client, relayEndpoint);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task NegotiateAuthAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] greeting = _useAuth
            ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
            : new byte[] { 0x05, 0x01, 0x00 };

        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);

        var methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse, ct).ConfigureAwait(false);

        if (methodResponse[0] != 0x05)
            throw new InvalidOperationException($"SOCKS5: invalid version 0x{methodResponse[0]:X2}");

        if (methodResponse[1] == 0xFF)
            throw new InvalidOperationException("SOCKS5: no acceptable auth method");

        if (methodResponse[1] == 0x02)
        {
            if (!_useAuth)
                throw new InvalidOperationException("SOCKS5: server requires auth but none configured");

            var userBytes = Encoding.UTF8.GetBytes(_settings.Username ?? "");
            var passBytes = Encoding.UTF8.GetBytes(_settings.Password ?? "");
            var authReq = new byte[3 + userBytes.Length + passBytes.Length];
            authReq[0] = 0x01;
            authReq[1] = (byte)userBytes.Length;
            userBytes.CopyTo(authReq, 2);
            authReq[2 + userBytes.Length] = (byte)passBytes.Length;
            passBytes.CopyTo(authReq, 3 + userBytes.Length);

            await stream.WriteAsync(authReq, ct).ConfigureAwait(false);

            var authResponse = new byte[2];
            await ReadExactAsync(stream, authResponse, ct).ConfigureAwait(false);

            if (authResponse[1] != 0x00)
                throw new InvalidOperationException("SOCKS5: authentication failed");
        }
    }

    private async Task SendConnectAsync(NetworkStream stream, string hostname, int port, CancellationToken ct)
    {
        byte[] request;

        if (_settings.ProxyHostnames && !IPAddress.TryParse(hostname, out _))
        {
            request = EncodeConnectDomain(hostname, port);
        }
        else
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, ct).ConfigureAwait(false);
            var ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new InvalidOperationException($"Could not resolve {hostname} to IPv4");

            request = EncodeConnectIpv4(ipv4, port);
        }

        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await ReadConnectResponseAsync(stream, ct).ConfigureAwait(false);
    }

    private async Task<IPEndPoint> ReadConnectResponseAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        if (header[0] != 0x05)
            throw new InvalidOperationException($"SOCKS5: invalid response version 0x{header[0]:X2}");
        if (header[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5: request failed with code 0x{header[1]:X2}");

        byte atyp = header[3];
        byte[] addrBytes;
        switch (atyp)
        {
            case 0x01:
                addrBytes = new byte[4];
                await ReadExactAsync(stream, addrBytes, ct).ConfigureAwait(false);
                break;
            case 0x04:
                addrBytes = new byte[16];
                await ReadExactAsync(stream, addrBytes, ct).ConfigureAwait(false);
                break;
            case 0x03:
                var lenBuf = new byte[1];
                await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
                addrBytes = new byte[lenBuf[0]];
                await ReadExactAsync(stream, addrBytes, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"SOCKS5: unknown ATYP 0x{atyp:X2}");
        }

        var portBuf = new byte[2];
        await ReadExactAsync(stream, portBuf, ct).ConfigureAwait(false);
        int boundPort = (portBuf[0] << 8) | portBuf[1];

        if (atyp == 0x01)
            return new IPEndPoint(new IPAddress(addrBytes), boundPort);
        if (atyp == 0x04)
            return new IPEndPoint(new IPAddress(addrBytes), boundPort);

        return new IPEndPoint(IPAddress.Any, boundPort);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new InvalidOperationException("SOCKS5: connection closed during handshake");
            read += n;
        }
    }

    // Test helpers
    internal static byte[] EncodeGreeting(bool withAuth)
        => withAuth ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 };

    internal static byte[] EncodeConnectDomain(string hostname, int port)
    {
        var hostBytes = Encoding.ASCII.GetBytes(hostname);
        var request = new byte[7 + hostBytes.Length];
        request[0] = 0x05; request[1] = 0x01; request[2] = 0x00; request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        request[5 + hostBytes.Length] = (byte)(port >> 8);
        request[6 + hostBytes.Length] = (byte)(port & 0xFF);
        return request;
    }

    internal static byte[] EncodeConnectIpv4(IPAddress ip, int port)
    {
        var request = new byte[10];
        request[0] = 0x05; request[1] = 0x01; request[2] = 0x00; request[3] = 0x01;
        ip.GetAddressBytes().CopyTo(request, 4);
        request[8] = (byte)(port >> 8);
        request[9] = (byte)(port & 0xFF);
        return request;
    }

    internal static byte[] EncodeSuccessResponse(IPAddress boundAddr, int boundPort)
    {
        var addrBytes = boundAddr.GetAddressBytes();
        byte atyp = (byte)(addrBytes.Length == 4 ? 0x01 : 0x04);
        var response = new byte[4 + addrBytes.Length + 2];
        response[0] = 0x05; response[1] = 0x00; response[2] = 0x00; response[3] = atyp;
        addrBytes.CopyTo(response, 4);
        response[4 + addrBytes.Length] = (byte)(boundPort >> 8);
        response[4 + addrBytes.Length + 1] = (byte)(boundPort & 0xFF);
        return response;
    }
}
