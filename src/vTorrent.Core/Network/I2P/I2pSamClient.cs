// src/vTorrent.Core/Network/I2P/I2pSamClient.cs
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.I2P;

/// <summary>
/// Low-level SAM v3.3 protocol client. Manages a single TCP connection to the SAM bridge.
/// </summary>
public sealed class I2pSamClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string? _sessionVersion;

    public bool IsConnected => _tcp?.Connected == true;
    public NetworkStream? RawStream => _tcp?.GetStream();
    public string? SessionVersion => _sessionVersion;

    public I2pSamClient(string host, int port)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
    }

    public async Task<string> HandshakeAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _port, ct).ConfigureAwait(false);

        var stream = _tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII);
        _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

        await SendCommandAsync("HELLO VERSION MIN=3.0 MAX=3.3", ct).ConfigureAwait(false);
        var reply = await ReadReplyAsync(ct).ConfigureAwait(false);

        if (!reply.IsOk)
            throw new I2pSamException($"SAM handshake failed: {reply.Result}");

        _sessionVersion = reply.GetValue("VERSION");
        return _sessionVersion;
    }

    public async Task<(string publicKey, string privateKey)> GenerateDestinationAsync(
        int signatureType = 7, CancellationToken ct = default)
    {
        await SendCommandAsync($"DEST GENERATE SIGNATURE_TYPE={signatureType}", ct).ConfigureAwait(false);
        var reply = await ReadReplyAsync(ct).ConfigureAwait(false);

        return (reply.GetValue("PUB"), reply.GetValue("PRIV"));
    }

    public async Task<string> CreateSessionAsync(
        string sessionId, string destination, I2pTunnelConfig tunnels,
        CancellationToken ct = default)
    {
        var cmd = new StringBuilder();
        cmd.Append($"SESSION CREATE STYLE=PRIMARY ID={sessionId} DESTINATION={destination}");
        cmd.Append($" SIGNATURE_TYPE=7");
        cmd.Append($" i2cp.leaseSetEncType=4,0");
        cmd.Append($" inbound.quantity={tunnels.InboundQuantity}");
        cmd.Append($" outbound.quantity={tunnels.OutboundQuantity}");
        cmd.Append($" inbound.length={tunnels.InboundLength}");
        cmd.Append($" outbound.length={tunnels.OutboundLength}");

        await SendCommandAsync(cmd.ToString(), ct).ConfigureAwait(false);
        var reply = await ReadReplyAsync(ct).ConfigureAwait(false);

        if (!reply.IsOk)
            throw new I2pSamException($"SESSION CREATE failed: {reply.Result}");

        return reply.GetValueOrDefault("DESTINATION") ?? destination;
    }

    public async Task StreamConnectAsync(string sessionId, string destination, CancellationToken ct = default)
    {
        await SendCommandAsync($"STREAM CONNECT ID={sessionId} DESTINATION={destination} SILENT=false", ct)
            .ConfigureAwait(false);
        var reply = await ReadReplyAsync(ct).ConfigureAwait(false);

        if (!reply.IsOk)
            throw new I2pSamException($"STREAM CONNECT failed: {reply.Result}");
    }

    public async Task<string> StreamAcceptAsync(string sessionId, CancellationToken ct = default)
    {
        await SendCommandAsync($"STREAM ACCEPT ID={sessionId} SILENT=false", ct).ConfigureAwait(false);
        var reply = await ReadReplyAsync(ct).ConfigureAwait(false);

        if (!reply.IsOk)
            throw new I2pSamException($"STREAM ACCEPT failed: {reply.Result}");

        var peerLine = await _reader!.ReadLineAsync(ct).ConfigureAwait(false);
        return peerLine?.Trim() ?? throw new I2pSamException("No peer destination received on accept");
    }

    public async Task<string> NamingLookupAsync(string name, CancellationToken ct = default)
    {
        await SendCommandAsync($"NAMING LOOKUP NAME={name}", ct).ConfigureAwait(false);
        var reply = await ReadReplyAsync(ct).ConfigureAwait(false);

        if (!reply.IsOk)
            throw new I2pSamException($"NAMING LOOKUP failed for {name}: {reply.Result}");

        return reply.GetValue("VALUE");
    }

    private async Task SendCommandAsync(string command, CancellationToken ct)
    {
        if (_writer == null) throw new InvalidOperationException("Not connected to SAM bridge");
        await _writer.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);
    }

    private async Task<SamReply> ReadReplyAsync(CancellationToken ct)
    {
        if (_reader == null) throw new InvalidOperationException("Not connected to SAM bridge");
        var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new I2pSamException("SAM bridge closed connection");
        return SamReply.Parse(line);
    }

    public async ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _tcp?.Dispose();
    }
}

public sealed class I2pTunnelConfig
{
    public int InboundQuantity { get; init; } = 3;
    public int OutboundQuantity { get; init; } = 3;
    public int InboundLength { get; init; } = 3;
    public int OutboundLength { get; init; } = 3;

    public static I2pTunnelConfig FromSettings(I2pSettings settings) => new()
    {
        InboundQuantity = settings.InboundTunnelQuantity,
        OutboundQuantity = settings.OutboundTunnelQuantity,
        InboundLength = settings.InboundTunnelLength,
        OutboundLength = settings.OutboundTunnelLength
    };
}

public sealed class I2pSamException : Exception
{
    public I2pSamException(string message) : base(message) { }
    public I2pSamException(string message, Exception inner) : base(message, inner) { }
}
