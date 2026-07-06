using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Transport;

public class UtpSocketTests
{
    private readonly List<(byte[] Data, IPEndPoint Target)> _sentPackets = new();
    private readonly IPEndPoint _remoteEndPoint = new(IPAddress.Loopback, 5000);

    private ValueTask MockSend(ReadOnlyMemory<byte> data, IPEndPoint target)
    {
        _sentPackets.Add((data.ToArray(), target));
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task InitiateConnect_SendsSynPacket()
    {
        var socket = UtpSocket.CreateOutgoing(_remoteEndPoint, MockSend);
        _ = socket.ConnectAsync(CancellationToken.None);

        await Task.Delay(50);

        _sentPackets.Should().HaveCountGreaterOrEqualTo(1);
        var synData = _sentPackets[0].Data;
        UtpPacketHeader.TryParse(synData, out var header).Should().BeTrue();
        header.Type.Should().Be(UtpPacketType.Syn);
        header.SequenceNumber.Should().Be(1);
        socket.State.Should().Be(UtpConnectionState.SynSent);
    }

    [Fact]
    public async Task ReceiveSynAck_TransitionsToConnected()
    {
        var socket = UtpSocket.CreateOutgoing(_remoteEndPoint, MockSend);
        var connectTask = socket.ConnectAsync(CancellationToken.None);

        await Task.Delay(50);

        var synHeader = ParseSentHeader(0);
        var ackHeader = new UtpPacketHeader(
            type: UtpPacketType.State,
            connectionId: (ushort)(synHeader.ConnectionId + 1),
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: 0,
            windowSize: 65536,
            sequenceNumber: 100,
            ackNumber: synHeader.SequenceNumber);

        var ackBytes = new byte[UtpPacketHeader.Size];
        ackHeader.WriteTo(ackBytes);
        socket.ProcessIncomingPacket(ackBytes, _remoteEndPoint);

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        socket.State.Should().Be(UtpConnectionState.Connected);
    }

    [Fact]
    public void CreateIncoming_FromSyn_SetsStateToSynRecv()
    {
        ushort connId = 42;
        var synHeader = new UtpPacketHeader(
            type: UtpPacketType.Syn,
            connectionId: connId,
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: 0,
            windowSize: 65536,
            sequenceNumber: 1,
            ackNumber: 0);

        var socket = UtpSocket.CreateIncoming(synHeader, _remoteEndPoint, MockSend);

        socket.State.Should().Be(UtpConnectionState.SynRecv);

        _sentPackets.Should().HaveCountGreaterOrEqualTo(1);
        var response = ParseSentHeader(0);
        response.Type.Should().Be(UtpPacketType.State);
        response.AckNumber.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_WhenConnected_SegmentsIntoPackets()
    {
        var socket = await CreateConnectedSocket();
        _sentPackets.Clear();

        var data = new byte[3000];
        Random.Shared.NextBytes(data);
        await socket.SendAsync(data);

        _sentPackets.Should().HaveCountGreaterOrEqualTo(2);
        foreach (var (pktData, _) in _sentPackets)
        {
            UtpPacketHeader.TryParse(pktData, out var h).Should().BeTrue();
            h.Type.Should().Be(UtpPacketType.Data);
        }
    }

    [Fact]
    public async Task ProcessIncomingData_ReassemblesInOrder()
    {
        var socket = await CreateConnectedSocket();

        var payload1 = new byte[] { 1, 2, 3, 4, 5 };
        var payload2 = new byte[] { 6, 7, 8, 9, 10 };
        ushort remoteSeq = socket.RemoteSequenceNumber;

        SendDataPacket(socket, (ushort)(remoteSeq + 1), payload1);
        SendDataPacket(socket, (ushort)(remoteSeq + 2), payload2);

        var received = new byte[10];
        int totalRead = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        while (totalRead < 10)
        {
            totalRead += await socket.ReadAsync(received.AsMemory(totalRead), cts.Token);
        }

        received.Should().BeEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
    }

    [Fact]
    public async Task ProcessIncomingData_OutOfOrder_ReassemblesCorrectly()
    {
        var socket = await CreateConnectedSocket();

        var payload1 = new byte[] { 1, 2, 3 };
        var payload2 = new byte[] { 4, 5, 6 };
        ushort remoteSeq = socket.RemoteSequenceNumber;

        SendDataPacket(socket, (ushort)(remoteSeq + 2), payload2);
        SendDataPacket(socket, (ushort)(remoteSeq + 1), payload1);

        var received = new byte[6];
        int totalRead = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        while (totalRead < 6)
        {
            totalRead += await socket.ReadAsync(received.AsMemory(totalRead), cts.Token);
        }

        received.Should().BeEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6 });
    }

    [Fact]
    public async Task ProcessReset_TransitionsToReset()
    {
        var socket = await CreateConnectedSocket();

        var reset = new UtpPacketHeader(
            type: UtpPacketType.Reset,
            connectionId: socket.RecvConnectionId,
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: 0,
            windowSize: 0,
            sequenceNumber: 0,
            ackNumber: 0);

        var resetBytes = new byte[UtpPacketHeader.Size];
        reset.WriteTo(resetBytes);
        socket.ProcessIncomingPacket(resetBytes, _remoteEndPoint);

        socket.State.Should().Be(UtpConnectionState.Reset);
    }

    // ---- helpers ----

    private UtpPacketHeader ParseSentHeader(int index)
    {
        UtpPacketHeader.TryParse(_sentPackets[index].Data, out var h);
        return h;
    }

    private async Task<UtpSocket> CreateConnectedSocket()
    {
        var socket = UtpSocket.CreateOutgoing(_remoteEndPoint, MockSend);
        var connectTask = socket.ConnectAsync(CancellationToken.None);

        await Task.Delay(50);
        var synHeader = ParseSentHeader(0);

        var ack = new UtpPacketHeader(
            type: UtpPacketType.State,
            connectionId: (ushort)(synHeader.ConnectionId + 1),
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: 0,
            windowSize: 65536,
            sequenceNumber: 100,
            ackNumber: synHeader.SequenceNumber);

        var ackBytes = new byte[UtpPacketHeader.Size];
        ack.WriteTo(ackBytes);
        socket.ProcessIncomingPacket(ackBytes, _remoteEndPoint);

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        return socket;
    }

    private void SendDataPacket(UtpSocket socket, ushort seqNr, byte[] payload)
    {
        var header = new UtpPacketHeader(
            type: UtpPacketType.Data,
            connectionId: socket.RecvConnectionId,
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: 0,
            windowSize: 65536,
            sequenceNumber: seqNr,
            ackNumber: socket.LocalSequenceNumber);

        var packet = new byte[UtpPacketHeader.Size + payload.Length];
        header.WriteTo(packet);
        payload.CopyTo(packet.AsSpan(UtpPacketHeader.Size));
        socket.ProcessIncomingPacket(packet, _remoteEndPoint);
    }
}
