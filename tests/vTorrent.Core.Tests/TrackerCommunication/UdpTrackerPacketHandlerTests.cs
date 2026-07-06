using System;
using System.Buffers.Binary;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Core.TrackerCommunication.Udp;
using Xunit;

namespace vTorrent.Core.Tests.TrackerCommunication;

public class UdpTrackerPacketHandlerTests
{
    private readonly UdpTrackerPacketHandler _handler = new();
    private readonly IPEndPoint _trackerEndpoint = new(IPAddress.Parse("93.184.216.34"), 6969);

    [Fact]
    public async Task ProcessPacket_MatchingTransactionId_CompletesTcs()
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int txnId = 0x12345678;
        _handler.RegisterPending(txnId, tcs, _trackerEndpoint);

        // Build a fake announce response: action=1, txnId=0x12345678, then 12 bytes padding
        var response = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0, 4), 1); // action=announce
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4, 4), txnId);

        _handler.ProcessPacket(response, _trackerEndpoint);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        result.Should().HaveCount(20);
        BinaryPrimitives.ReadInt32BigEndian(result.AsSpan(4, 4)).Should().Be(txnId);
    }

    [Fact]
    public void ProcessPacket_UnknownTransactionId_IsIgnored()
    {
        // No pending registered — should not throw
        var response = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4, 4), unchecked((int)0xDEADBEEF));

        var act = () => _handler.ProcessPacket(new byte[20], _trackerEndpoint);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task ProcessPacket_WrongSenderAddress_IsIgnored()
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int txnId = 42;
        _handler.RegisterPending(txnId, tcs, _trackerEndpoint);

        var response = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4, 4), txnId);

        // Send from a different IP
        var wrongSender = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 9999);
        _handler.ProcessPacket(response, wrongSender);

        // TCS should not be completed
        tcs.Task.IsCompleted.Should().BeFalse();

        // Cleanup
        _handler.UnregisterPending(txnId);
    }

    [Fact]
    public void UnregisterPending_RemovesEntry()
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int txnId = 99;
        _handler.RegisterPending(txnId, tcs, _trackerEndpoint);

        _handler.UnregisterPending(txnId);

        // Now a matching packet should not complete the TCS
        var response = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4, 4), txnId);
        _handler.ProcessPacket(response, _trackerEndpoint);

        tcs.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void ProcessPacket_TooShort_IsIgnored()
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.RegisterPending(1, tcs, _trackerEndpoint);

        // Only 4 bytes — can't extract transaction ID
        var act = () => _handler.ProcessPacket(new byte[4], _trackerEndpoint);
        act.Should().NotThrow();
        tcs.Task.IsCompleted.Should().BeFalse();

        _handler.UnregisterPending(1);
    }

    [Fact]
    public async Task ProcessPacket_CopiesData_DoesNotShareBuffer()
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int txnId = 7;
        _handler.RegisterPending(txnId, tcs, _trackerEndpoint);

        var response = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4, 4), txnId);
        response[8] = 0xAA; // marker byte

        _handler.ProcessPacket(response, _trackerEndpoint);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        // Mutate original buffer — result should be unaffected
        response[8] = 0xFF;
        result[8].Should().Be(0xAA);
    }
}
