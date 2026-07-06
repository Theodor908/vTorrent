using System;
using System.Net;
using System.Net.Sockets;

namespace vTorrent.Core.Network;

/// <summary>
/// Seam over the raw UDP send so the proxy-vs-direct policy in
/// <see cref="UdpSocketManager"/> is testable without a bound socket.
/// </summary>
internal interface IUdpSendSink
{
    void Send(ReadOnlySpan<byte> data, IPEndPoint target);
}

/// <summary>Production sink: writes straight to the OS socket.</summary>
internal sealed class SocketUdpSendSink : IUdpSendSink
{
    private readonly Socket _socket;
    public SocketUdpSendSink(Socket socket) => _socket = socket;
    public void Send(ReadOnlySpan<byte> data, IPEndPoint target)
        => _socket.SendTo(data, SocketFlags.None, target);
}
