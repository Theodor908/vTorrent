using FluentAssertions;
using Moq;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PeerCommunication.Models;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication;

public class WriteBatcherTests
{
    [Fact]
    public async Task FlushAllAsync_WithNoMessages_DoesNothing()
    {
        var batcher = new WriteBatcher();
        await batcher.FlushAllAsync(CancellationToken.None);
        batcher.PendingPeerCount.Should().Be(0);
    }

    [Fact]
    public async Task QueueMessage_ThenFlush_SendsBatch()
    {
        var batcher = new WriteBatcher();
        var peerMock = new Mock<IPeerConnection>();
        peerMock.Setup(p => p.IsConnected).Returns(true);

        var msg1 = PeerMessage.CreateHave(1);
        var msg2 = PeerMessage.CreateHave(2);

        batcher.QueueMessage(peerMock.Object, msg1);
        batcher.QueueMessage(peerMock.Object, msg2);
        batcher.PendingPeerCount.Should().Be(1);

        await batcher.FlushAllAsync(CancellationToken.None);

        peerMock.Verify(p => p.SendMessagesAsync(
            It.Is<IReadOnlyList<PeerMessage>>(msgs => msgs.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
        batcher.PendingPeerCount.Should().Be(0);
    }

    [Fact]
    public async Task FlushAllAsync_MultiplePeers_SendsSeparateBatches()
    {
        var batcher = new WriteBatcher();
        var peer1 = new Mock<IPeerConnection>();
        var peer2 = new Mock<IPeerConnection>();
        peer1.Setup(p => p.IsConnected).Returns(true);
        peer2.Setup(p => p.IsConnected).Returns(true);

        batcher.QueueMessage(peer1.Object, PeerMessage.CreateHave(1));
        batcher.QueueMessage(peer2.Object, PeerMessage.CreateHave(2));

        await batcher.FlushAllAsync(CancellationToken.None);

        peer1.Verify(p => p.SendMessagesAsync(It.IsAny<IReadOnlyList<PeerMessage>>(), It.IsAny<CancellationToken>()), Times.Once);
        peer2.Verify(p => p.SendMessagesAsync(It.IsAny<IReadOnlyList<PeerMessage>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FlushAllAsync_SkipsDisconnectedPeers()
    {
        var batcher = new WriteBatcher();
        var peerMock = new Mock<IPeerConnection>();
        peerMock.Setup(p => p.IsConnected).Returns(false);

        batcher.QueueMessage(peerMock.Object, PeerMessage.CreateHave(1));
        await batcher.FlushAllAsync(CancellationToken.None);

        peerMock.Verify(p => p.SendMessagesAsync(It.IsAny<IReadOnlyList<PeerMessage>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
