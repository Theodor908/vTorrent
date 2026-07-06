using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.Network.I2P;

/// <summary>
/// Manages SAM datagram port (UDP 7655) for I2P DHT communication.
/// Sends signed datagrams (protocol 17) for queries, unsigned (protocol 18) for responses.
/// </summary>
public sealed class I2pDatagramClient : IAsyncDisposable
{
    private readonly string _samHost;
    private readonly int _datagramPort;
    private readonly string _sessionId;
    private UdpClient? _udpClient;

    public int LocalPort => _datagramPort;

    public I2pDatagramClient(string samHost, int datagramPort, string sessionId)
    {
        _samHost = samHost ?? throw new ArgumentNullException(nameof(samHost));
        _datagramPort = datagramPort;
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _udpClient = new UdpClient();
        _udpClient.Connect(_samHost, _datagramPort);
        return Task.CompletedTask;
    }

    public async Task SendDatagramAsync(string destination, byte[] payload, CancellationToken ct = default)
    {
        if (_udpClient == null) throw new InvalidOperationException("Datagram client not started");

        // SAM datagram format: "3.0 {sessionId} {destination}\n{payload}"
        var header = $"3.0 {_sessionId} {destination}\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        var packet = new byte[headerBytes.Length + payload.Length];
        Buffer.BlockCopy(headerBytes, 0, packet, 0, headerBytes.Length);
        Buffer.BlockCopy(payload, 0, packet, headerBytes.Length, payload.Length);

        await _udpClient.SendAsync(packet, ct).ConfigureAwait(false);
    }

    public async Task<(string sender, byte[] payload)> ReceiveDatagramAsync(CancellationToken ct = default)
    {
        if (_udpClient == null) throw new InvalidOperationException("Datagram client not started");

        var result = await _udpClient.ReceiveAsync(ct).ConfigureAwait(false);
        var data = result.Buffer;

        // Parse: "{sender}\n{payload}"
        var newlineIdx = Array.IndexOf(data, (byte)'\n');
        if (newlineIdx < 0) throw new FormatException("Invalid SAM datagram format");

        var sender = Encoding.ASCII.GetString(data, 0, newlineIdx);
        var payloadStart = newlineIdx + 1;
        var payload = new byte[data.Length - payloadStart];
        Buffer.BlockCopy(data, payloadStart, payload, 0, payload.Length);

        return (sender, payload);
    }

    public ValueTask DisposeAsync()
    {
        _udpClient?.Dispose();
        return ValueTask.CompletedTask;
    }
}
