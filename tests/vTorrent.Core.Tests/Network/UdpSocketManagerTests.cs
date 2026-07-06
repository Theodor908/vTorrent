using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Core.Network;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Network;

public class UdpSocketManagerTests
{
    [Fact]
    public void IsUtpPacket_ValidSyn_ReturnsTrue()
    {
        var data = new byte[20];
        data[0] = 0x41; // SYN: type=4, version=1
        UdpSocketManager.IsUtpPacket(data).Should().BeTrue();
    }

    [Fact]
    public void IsUtpPacket_ValidData_ReturnsTrue()
    {
        var data = new byte[20];
        data[0] = 0x01; // DATA: type=0, version=1
        UdpSocketManager.IsUtpPacket(data).Should().BeTrue();
    }

    [Fact]
    public void IsUtpPacket_DhtPacket_ReturnsFalse()
    {
        var data = new byte[20];
        data[0] = 0x64; // 'd' for bencode dict
        UdpSocketManager.IsUtpPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsUtpPacket_TooShort_ReturnsFalse()
    {
        var data = new byte[19];
        data[0] = 0x01;
        UdpSocketManager.IsUtpPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsUtpPacket_InvalidVersion_ReturnsFalse()
    {
        var data = new byte[20];
        data[0] = 0x42; // type=4, version=2
        UdpSocketManager.IsUtpPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsDhtPacket_ValidBencodedDict_ReturnsTrue()
    {
        // Minimal valid DHT message: bencode dict starting with 'd', ending with 'e'
        var data = System.Text.Encoding.UTF8.GetBytes("d1:t2:aa1:y1:qe");
        UdpSocketManager.IsDhtPacket(data).Should().BeTrue();
    }

    [Fact]
    public void IsDhtPacket_UtpSynPacket_ReturnsFalse()
    {
        // 0xC3 = type=12, version=3 — a malformed uTP-like packet
        var data = new byte[20];
        data[0] = 0xC3;
        UdpSocketManager.IsDhtPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsDhtPacket_ValidUtpPacket_ReturnsFalse()
    {
        var data = new byte[20];
        data[0] = 0x41; // SYN: type=4, version=1
        UdpSocketManager.IsDhtPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsDhtPacket_StartsWithD_DoesNotEndWithE_ReturnsFalse()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("d1:t2:aax");
        UdpSocketManager.IsDhtPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsDhtPacket_EmptyPacket_ReturnsFalse()
    {
        var data = Array.Empty<byte>();
        UdpSocketManager.IsDhtPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsDhtPacket_SingleByteD_ReturnsFalse()
    {
        // A single 'd' byte is not a valid DHT message (needs at least "de" = empty dict)
        var data = new byte[] { 0x64 };
        UdpSocketManager.IsDhtPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsDhtPacket_MinimalEmptyDict_ReturnsTrue()
    {
        // "de" is a valid (empty) bencode dictionary
        var data = System.Text.Encoding.UTF8.GetBytes("de");
        UdpSocketManager.IsDhtPacket(data).Should().BeTrue();
    }

    [Fact]
    public void IsTrackerPacket_ConnectResponse_ReturnsTrue()
    {
        // Action=0 (connect), transaction_id=0x12345678
        var data = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), 0); // action=connect
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4, 4), 0x12345678);
        UdpSocketManager.IsTrackerPacket(data).Should().BeTrue();
    }

    [Fact]
    public void IsTrackerPacket_AnnounceResponse_ReturnsTrue()
    {
        var data = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), 1); // action=announce
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4, 4), 0x12345678);
        UdpSocketManager.IsTrackerPacket(data).Should().BeTrue();
    }

    [Fact]
    public void IsTrackerPacket_ScrapeResponse_ReturnsTrue()
    {
        var data = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), 2); // action=scrape
        UdpSocketManager.IsTrackerPacket(data).Should().BeTrue();
    }

    [Fact]
    public void IsTrackerPacket_ErrorResponse_ReturnsTrue()
    {
        var data = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), 3); // action=error
        UdpSocketManager.IsTrackerPacket(data).Should().BeTrue();
    }

    [Fact]
    public void IsTrackerPacket_InvalidAction_ReturnsFalse()
    {
        var data = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), 4); // action=4 (invalid)
        UdpSocketManager.IsTrackerPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsTrackerPacket_TooShort_ReturnsFalse()
    {
        var data = new byte[7];
        UdpSocketManager.IsTrackerPacket(data).Should().BeFalse();
    }

    [Fact]
    public void IsTrackerPacket_DhtPacket_ReturnsFalse()
    {
        // DHT packet starts with 'd' (0x64) — action would be 0x64XXXXXX which is > 3
        var data = System.Text.Encoding.UTF8.GetBytes("d1:t2:aa1:y1:qe");
        UdpSocketManager.IsTrackerPacket(data).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_WritesToSocket()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var receiverPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        using var manager = new UdpSocketManager();
        await manager.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);

        var testData = new byte[] { 0x41, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        await manager.SendAsync(testData, new IPEndPoint(IPAddress.Loopback, receiverPort));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await receiver.ReceiveAsync(cts.Token);
        result.Buffer.Should().BeEquivalentTo(testData);
    }

    [Fact]
    public async Task ReceiveLoop_RoutesUtpToUtpHandler()
    {
        var utpHandler = new Mock<IUdpPacketHandler>();
        var dhtHandler = new Mock<IUdpPacketHandler>();

        using var manager = new UdpSocketManager();
        manager.SetUtpHandler(utpHandler.Object);
        manager.SetDhtHandler(dhtHandler.Object);
        await manager.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        var port = manager.LocalPort;

        using var sender = new UdpClient();
        var utpPacket = new byte[20];
        utpPacket[0] = 0x01;
        // Resend until routed: robust to UDP datagram loss and CI CPU starvation.
        await SendUntilRoutedAsync(sender, utpPacket, new IPEndPoint(IPAddress.Loopback, port), utpHandler);

        utpHandler.Verify(h => h.ProcessPacket(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IPEndPoint>()), Times.AtLeastOnce);
        dhtHandler.Verify(h => h.ProcessPacket(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IPEndPoint>()), Times.Never);
    }

    [Fact]
    public async Task ReceiveLoop_RoutesDhtToDhtHandler()
    {
        var utpHandler = new Mock<IUdpPacketHandler>();
        var dhtHandler = new Mock<IUdpPacketHandler>();

        using var manager = new UdpSocketManager();
        manager.SetUtpHandler(utpHandler.Object);
        manager.SetDhtHandler(dhtHandler.Object);
        await manager.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        var port = manager.LocalPort;

        using var sender = new UdpClient();
        var dhtPacket = System.Text.Encoding.UTF8.GetBytes("d1:ad2:id20:abcdefghij0123456789e1:q4:ping1:t2:aa1:y1:qe");
        // Resend until routed: robust to UDP datagram loss and CI CPU starvation.
        await SendUntilRoutedAsync(sender, dhtPacket, new IPEndPoint(IPAddress.Loopback, port), dhtHandler);

        dhtHandler.Verify(h => h.ProcessPacket(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IPEndPoint>()), Times.AtLeastOnce);
        utpHandler.Verify(h => h.ProcessPacket(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IPEndPoint>()), Times.Never);
    }

    [Fact]
    public async Task ReceiveLoop_UnrecognizedPacket_DropsWithoutCallingEitherHandler()
    {
        var utpHandler = new Mock<IUdpPacketHandler>();
        var dhtHandler = new Mock<IUdpPacketHandler>();

        using var manager = new UdpSocketManager();
        manager.SetUtpHandler(utpHandler.Object);
        manager.SetDhtHandler(dhtHandler.Object);
        await manager.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        var port = manager.LocalPort;

        using var sender = new UdpClient();
        // 0xC3 packet: not uTP (version=3, type=12), not DHT (doesn't start with 'd')
        var junkPacket = new byte[20];
        junkPacket[0] = 0xC3;
        await sender.SendAsync(junkPacket, new IPEndPoint(IPAddress.Loopback, port));

        await Task.Delay(200);

        utpHandler.Verify(h => h.ProcessPacket(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IPEndPoint>()), Times.Never);
        dhtHandler.Verify(h => h.ProcessPacket(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IPEndPoint>()), Times.Never);
    }

    [Fact]
    public async Task ReceiveLoop_TrackerPacket_RoutesToTrackerHandler()
    {
        var utpHandler = new Mock<IUdpPacketHandler>();
        var dhtHandler = new Mock<IUdpPacketHandler>();
        var trackerHandler = new Mock<IUdpPacketHandler>();

        using var manager = new UdpSocketManager();
        manager.SetUtpHandler(utpHandler.Object);
        manager.SetDhtHandler(dhtHandler.Object);
        manager.SetTrackerHandler(trackerHandler.Object);
        await manager.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        var port = manager.LocalPort;

        using var sender = new UdpClient();
        // BEP 15 announce response: action=1, txnId=0x12345678, interval, leechers, seeders
        var trackerPacket = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(trackerPacket.AsSpan(0, 4), 1); // action=announce
        BinaryPrimitives.WriteInt32BigEndian(trackerPacket.AsSpan(4, 4), 0x12345678);
        // Resend until routed: robust to UDP datagram loss and CI CPU starvation.
        await SendUntilRoutedAsync(sender, trackerPacket, new IPEndPoint(IPAddress.Loopback, port), trackerHandler);

        trackerHandler.Verify(h => h.ProcessPacket(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IPEndPoint>()), Times.AtLeastOnce);
        utpHandler.Verify(h => h.ProcessPacket(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IPEndPoint>()), Times.Never);
        dhtHandler.Verify(h => h.ProcessPacket(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IPEndPoint>()), Times.Never);
    }

    // Resends the datagram periodically until the handler's ProcessPacket fires
    // (or the timeout elapses). Robust to UDP packet loss and CI CPU starvation,
    // which a one-shot send + fixed delay cannot survive. The handler mocks
    // receive no other calls, so Invocations.Count > 0 means it was routed.
    private static async Task SendUntilRoutedAsync(
        UdpClient sender, byte[] packet, IPEndPoint target,
        Mock<IUdpPacketHandler> handler, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (handler.Invocations.Count == 0 && DateTime.UtcNow < deadline)
        {
            await sender.SendAsync(packet, target);
            await Task.Delay(50);
        }
    }
}
