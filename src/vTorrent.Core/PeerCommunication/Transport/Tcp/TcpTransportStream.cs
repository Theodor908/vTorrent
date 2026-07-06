using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Core.PeerCommunication;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.PeerCommunication.Transport.Tcp;

public sealed class TcpTransportStream : ITransportStream
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private bool _disposed;

    public TransportType TransportType => TransportType.Tcp;
    public bool IsConnected => !_disposed && _client.Connected;
    public EndPoint? RemoteEndPoint => _client.Client?.RemoteEndPoint;

    public TcpTransportStream(TcpClient client, PeerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);

        _client = client;
        _stream = client.GetStream();
        ConfigureSocket(client, settings);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _stream.ReadAsync(buffer, ct);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _stream.WriteAsync(buffer, ct);
    }

    private static void ConfigureSocket(TcpClient client, PeerSettings settings)
    {
        var socket = client.Client;

        socket.NoDelay = PeerConstants.TcpNoDelay;
        socket.ReceiveTimeout = settings.ConnectTimeout * 1000;
        socket.SendTimeout = settings.ConnectTimeout * 1000;

        if (PeerConstants.SendBufferSize > 0)
        {
            try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, PeerConstants.SendBufferSize); }
            catch (SocketException) { }
        }

        if (PeerConstants.ReceiveBufferSize > 0)
        {
            try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, PeerConstants.ReceiveBufferSize); }
            catch (SocketException) { }
        }

        if (PeerConstants.TcpKeepAlive)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                if (OperatingSystem.IsWindows())
                {
                    var keepAliveValues = new byte[12];
                    BitConverter.GetBytes(1).CopyTo(keepAliveValues, 0);
                    BitConverter.GetBytes(PeerConstants.TcpKeepAliveTimeSeconds * 1000).CopyTo(keepAliveValues, 4);
                    BitConverter.GetBytes(PeerConstants.TcpKeepAliveIntervalSeconds * 1000).CopyTo(keepAliveValues, 8);
                    socket.IOControl(unchecked((int)0x98000004), keepAliveValues, null);
                }
                else if (OperatingSystem.IsLinux())
                {
                    socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)4, PeerConstants.TcpKeepAliveTimeSeconds);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)5, PeerConstants.TcpKeepAliveIntervalSeconds);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)0x10, PeerConstants.TcpKeepAliveTimeSeconds);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)0x101, PeerConstants.TcpKeepAliveIntervalSeconds);
                }
            }
            catch (SocketException) { }
        }

        socket.LingerState = new LingerOption(PeerConstants.LingerOnClose, PeerConstants.LingerTimeoutSeconds);
    }

    /// <summary>Set the DSCP/ToS value on the underlying socket.</summary>
    public void SetDscp(int dscpValue)
    {
        try
        {
            // DSCP is 6-bit, shift left by 2 for ToS byte position
            int tosValue = (dscpValue & 0x3F) << 2;
            _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, tosValue);
        }
        catch (SocketException)
        {
            // Some platforms don't support ToS — silently ignore
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        _client.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
