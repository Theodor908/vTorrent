using FluentAssertions;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Transport;

public class PacketBufferTests
{
    [Fact]
    public void Insert_And_TryGet_ReturnsEntry()
    {
        var buffer = new PacketBuffer(1024);
        var data = new byte[] { 1, 2, 3 };
        buffer.Insert(42, data, 3, 100_000);

        buffer.TryGet(42, out var entry).Should().BeTrue();
        entry.Data.Should().BeEquivalentTo(data);
        entry.PayloadLength.Should().Be(3);
        entry.SentTimestampUs.Should().Be(100_000);
        entry.SendCount.Should().Be(1);
        entry.Acked.Should().BeFalse();
    }

    [Fact]
    public void TryGet_Missing_ReturnsFalse()
    {
        var buffer = new PacketBuffer(1024);
        buffer.TryGet(0, out _).Should().BeFalse();
    }

    [Fact]
    public void MarkAcked_SetsFlag()
    {
        var buffer = new PacketBuffer(1024);
        buffer.Insert(10, new byte[5], 5, 0);
        buffer.MarkAcked(10);

        buffer.TryGet(10, out var entry).Should().BeTrue();
        entry.Acked.Should().BeTrue();
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var buffer = new PacketBuffer(1024);
        buffer.Insert(10, new byte[5], 5, 0);
        buffer.Remove(10);
        buffer.TryGet(10, out _).Should().BeFalse();
    }

    [Fact]
    public void IncrementSendCount_TracksRetransmissions()
    {
        var buffer = new PacketBuffer(1024);
        buffer.Insert(10, new byte[5], 5, 0);
        buffer.IncrementSendCount(10, 200_000);

        buffer.TryGet(10, out var entry).Should().BeTrue();
        entry.SendCount.Should().Be(2);
        entry.SentTimestampUs.Should().Be(200_000);
    }

    [Fact]
    public void IsLessWrap_HandlesWraparound()
    {
        PacketBuffer.IsLessWrap(1, 2).Should().BeTrue();
        PacketBuffer.IsLessWrap(2, 1).Should().BeFalse();
        PacketBuffer.IsLessWrap(5, 5).Should().BeFalse();
        PacketBuffer.IsLessWrap(65535, 0).Should().BeTrue();
        PacketBuffer.IsLessWrap(65534, 1).Should().BeTrue();
        PacketBuffer.IsLessWrap(0, 65535).Should().BeFalse();
    }

    [Fact]
    public void Count_TracksActiveEntries()
    {
        var buffer = new PacketBuffer(1024);
        buffer.Count.Should().Be(0);

        buffer.Insert(1, new byte[5], 5, 0);
        buffer.Insert(2, new byte[5], 5, 0);
        buffer.Count.Should().Be(2);

        buffer.Remove(1);
        buffer.Count.Should().Be(1);
    }
}
