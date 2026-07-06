using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Transport;

public class UtpSocketManagerTests
{
    private readonly List<(byte[] Data, IPEndPoint Target)> _sentPackets = new();

    private ValueTask MockSend(ReadOnlyMemory<byte> data, IPEndPoint target)
    {
        _sentPackets.Add((data.ToArray(), target));
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ConnectAsync_CreatesSynAndReturnsSocket()
    {
        var manager = new UtpSocketManager(MockSend);
        var remote = new IPEndPoint(IPAddress.Loopback, 5000);

        var connectTask = manager.ConnectAsync(remote, CancellationToken.None);

        await Task.Delay(100);
        _sentPackets.Should().HaveCountGreaterOrEqualTo(1);
        UtpPacketHeader.TryParse(_sentPackets[0].Data, out var h).Should().BeTrue();
        h.Type.Should().Be(UtpPacketType.Syn);

        manager.Dispose();
    }

    [Fact]
    public async Task ProcessIncomingPacket_Syn_QueuesForAccept()
    {
        var manager = new UtpSocketManager(MockSend);
        var remote = new IPEndPoint(IPAddress.Loopback, 5000);

        var syn = new UtpPacketHeader(
            type: UtpPacketType.Syn,
            connectionId: 100,
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: 0,
            windowSize: 65536,
            sequenceNumber: 1,
            ackNumber: 0);

        var synBytes = new byte[UtpPacketHeader.Size];
        syn.WriteTo(synBytes);
        manager.ProcessIncomingPacket(synBytes, remote);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var socket = await manager.AcceptAsync(cts.Token);

        socket.Should().NotBeNull();
        socket.State.Should().Be(UtpConnectionState.SynRecv);

        manager.Dispose();
    }

    [Fact]
    public void ProcessIncomingPacket_UnknownConnId_SendsReset()
    {
        var manager = new UtpSocketManager(MockSend);
        var remote = new IPEndPoint(IPAddress.Loopback, 5000);

        var data = new UtpPacketHeader(
            type: UtpPacketType.Data,
            connectionId: 9999,
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: 0,
            windowSize: 65536,
            sequenceNumber: 5,
            ackNumber: 0);

        var dataBytes = new byte[UtpPacketHeader.Size + 10];
        data.WriteTo(dataBytes);
        manager.ProcessIncomingPacket(dataBytes, remote);

        _sentPackets.Should().ContainSingle();
        UtpPacketHeader.TryParse(_sentPackets[0].Data, out var reset).Should().BeTrue();
        reset.Type.Should().Be(UtpPacketType.Reset);

        manager.Dispose();
    }
}
