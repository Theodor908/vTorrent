using System;
using System.Net;

namespace vTorrent.Abstractions.Interfaces.Transport;

public interface IUdpPacketHandler
{
    void ProcessPacket(ReadOnlyMemory<byte> data, IPEndPoint sender);
}
