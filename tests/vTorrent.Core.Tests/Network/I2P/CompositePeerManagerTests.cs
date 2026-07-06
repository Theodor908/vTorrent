using FluentAssertions;
using Moq;
using System.Net;
using Xunit;
using vTorrent.Abstractions.Models;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Tests.Network.I2P;

public class CompositePeerManagerTests
{
    private readonly Mock<IPeerManager> _clearnet = new();
    private readonly Mock<IPeerManager> _i2p = new();

    [Fact]
    public void Ctor_NullClearnet_Throws()
    {
        var act = () => new CompositePeerManager(null!, _i2p.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullI2p_Throws()
    {
        var act = () => new CompositePeerManager(_clearnet.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConnectedPeerCount_SumsBothPools()
    {
        _clearnet.Setup(m => m.ConnectedPeerCount).Returns(5);
        _i2p.Setup(m => m.ConnectedPeerCount).Returns(3);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.ConnectedPeerCount.Should().Be(8);
    }

    [Fact]
    public void MaxConnections_SumsBothPools()
    {
        _clearnet.Setup(m => m.MaxConnections).Returns(50);
        _i2p.Setup(m => m.MaxConnections).Returns(20);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.MaxConnections.Should().Be(70);
    }

    [Fact]
    public void ConnectedPeers_ConcatenatesBothPools()
    {
        var clearnetPeers = new List<IPeerConnection> { Mock.Of<IPeerConnection>() };
        var i2pPeers = new List<IPeerConnection> { Mock.Of<IPeerConnection>(), Mock.Of<IPeerConnection>() };

        _clearnet.Setup(m => m.ConnectedPeers).Returns(clearnetPeers);
        _i2p.Setup(m => m.ConnectedPeers).Returns(i2pPeers);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.ConnectedPeers.Should().HaveCount(3);
    }

    [Fact]
    public void InfoHash_DelegatesToClearnet()
    {
        var hash = new byte[] { 1, 2, 3 };
        _clearnet.Setup(m => m.InfoHash).Returns(hash);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.InfoHash.Should().BeSameAs(hash);
    }

    [Fact]
    public void TotalBytesDownloaded_SumsBothPools()
    {
        _clearnet.Setup(m => m.TotalBytesDownloaded).Returns(1000L);
        _i2p.Setup(m => m.TotalBytesDownloaded).Returns(500L);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.TotalBytesDownloaded.Should().Be(1500L);
    }

    [Fact]
    public void TotalBytesUploaded_SumsBothPools()
    {
        _clearnet.Setup(m => m.TotalBytesUploaded).Returns(2000L);
        _i2p.Setup(m => m.TotalBytesUploaded).Returns(800L);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.TotalBytesUploaded.Should().Be(2800L);
    }

    [Fact]
    public void SuperSeedingActive_SetsBoth()
    {
        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.SuperSeedingActive = true;

        _clearnet.VerifySet(m => m.SuperSeedingActive = true, Times.Once);
        _i2p.VerifySet(m => m.SuperSeedingActive = true, Times.Once);
    }

    [Fact]
    public async Task AddPeerAsync_I2pPeer_RoutesToI2pManager()
    {
        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        var hash = new byte[32];
        var i2pPeer = PeerInfo.FromI2p(I2pDestination.FromHash(hash));

        _i2p.Setup(m => m.AddPeerAsync(It.IsAny<PeerInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await composite.AddPeerAsync(i2pPeer);

        _i2p.Verify(m => m.AddPeerAsync(i2pPeer, It.IsAny<CancellationToken>()), Times.Once);
        _clearnet.Verify(m => m.AddPeerAsync(It.IsAny<PeerInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddPeerAsync_ClearnetPeer_RoutesToClearnetManager()
    {
        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        var peer = new PeerInfo(IPAddress.Loopback, 6881);

        _clearnet.Setup(m => m.AddPeerAsync(It.IsAny<PeerInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await composite.AddPeerAsync(peer);

        _clearnet.Verify(m => m.AddPeerAsync(peer, It.IsAny<CancellationToken>()), Times.Once);
        _i2p.Verify(m => m.AddPeerAsync(It.IsAny<PeerInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddPeersAsync_GroupsByType()
    {
        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        var clearnetPeer = new PeerInfo(IPAddress.Loopback, 6881);
        var i2pPeer = PeerInfo.FromI2p(I2pDestination.FromHash(new byte[32]));

        _clearnet.Setup(m => m.AddPeersAsync(It.IsAny<IEnumerable<PeerInfo>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _i2p.Setup(m => m.AddPeersAsync(It.IsAny<IEnumerable<PeerInfo>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await composite.AddPeersAsync(new[] { clearnetPeer, i2pPeer });

        _clearnet.Verify(m => m.AddPeersAsync(It.IsAny<IEnumerable<PeerInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
        _i2p.Verify(m => m.AddPeersAsync(It.IsAny<IEnumerable<PeerInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemovePeerAsync_RoutesBasedOnPeerInfo()
    {
        var i2pPeerInfo = PeerInfo.FromI2p(I2pDestination.FromHash(new byte[32]));
        var mockPeer = new Mock<IPeerConnection>();
        mockPeer.Setup(p => p.PeerInfo).Returns(i2pPeerInfo);

        _i2p.Setup(m => m.RemovePeerAsync(It.IsAny<IPeerConnection>())).Returns(Task.CompletedTask);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        await composite.RemovePeerAsync(mockPeer.Object);

        _i2p.Verify(m => m.RemovePeerAsync(mockPeer.Object), Times.Once);
        _clearnet.Verify(m => m.RemovePeerAsync(It.IsAny<IPeerConnection>()), Times.Never);
    }

    [Fact]
    public void GetPeer_RoutesBasedOnPeerInfo()
    {
        var clearnetPeer = new PeerInfo(IPAddress.Loopback, 6881);
        var mockConn = Mock.Of<IPeerConnection>();
        _clearnet.Setup(m => m.GetPeer(clearnetPeer)).Returns(mockConn);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.GetPeer(clearnetPeer).Should().BeSameAs(mockConn);
        _i2p.Verify(m => m.GetPeer(It.IsAny<PeerInfo>()), Times.Never);
    }

    [Fact]
    public void IsConnected_RoutesBasedOnPeerInfo()
    {
        var i2pPeer = PeerInfo.FromI2p(I2pDestination.FromHash(new byte[32]));
        _i2p.Setup(m => m.IsConnected(i2pPeer)).Returns(true);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.IsConnected(i2pPeer).Should().BeTrue();
        _clearnet.Verify(m => m.IsConnected(It.IsAny<PeerInfo>()), Times.Never);
    }

    [Fact]
    public void GetPeersWithPiece_MergesBothPools()
    {
        var p1 = Mock.Of<IPeerConnection>();
        var p2 = Mock.Of<IPeerConnection>();
        _clearnet.Setup(m => m.GetPeersWithPiece(5)).Returns(new[] { p1 });
        _i2p.Setup(m => m.GetPeersWithPiece(5)).Returns(new[] { p2 });

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.GetPeersWithPiece(5).Should().HaveCount(2);
    }

    [Fact]
    public void GetAvailablePeers_MergesBothPools()
    {
        _clearnet.Setup(m => m.GetAvailablePeers()).Returns(new[] { Mock.Of<IPeerConnection>() });
        _i2p.Setup(m => m.GetAvailablePeers()).Returns(new[] { Mock.Of<IPeerConnection>() });

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.GetAvailablePeers().Should().HaveCount(2);
    }

    [Fact]
    public void GetInterestedPeers_MergesBothPools()
    {
        _clearnet.Setup(m => m.GetInterestedPeers()).Returns(new[] { Mock.Of<IPeerConnection>() });
        _i2p.Setup(m => m.GetInterestedPeers()).Returns(new[] { Mock.Of<IPeerConnection>(), Mock.Of<IPeerConnection>() });

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.GetInterestedPeers().Should().HaveCount(3);
    }

    [Fact]
    public async Task StartAsync_StartsBoth()
    {
        _clearnet.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _i2p.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        await composite.StartAsync();

        _clearnet.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        _i2p.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopAsync_StopsBoth()
    {
        _clearnet.Setup(m => m.StopAsync()).Returns(Task.CompletedTask);
        _i2p.Setup(m => m.StopAsync()).Returns(Task.CompletedTask);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        await composite.StopAsync();

        _clearnet.Verify(m => m.StopAsync(), Times.Once);
        _i2p.Verify(m => m.StopAsync(), Times.Once);
    }

    [Fact]
    public async Task BroadcastHaveAsync_BroadcastsToBoth()
    {
        _clearnet.Setup(m => m.BroadcastHaveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _i2p.Setup(m => m.BroadcastHaveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        await composite.BroadcastHaveAsync(42);

        _clearnet.Verify(m => m.BroadcastHaveAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        _i2p.Verify(m => m.BroadcastHaveAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastBitfieldAsync_BroadcastsToBoth()
    {
        var bf = new byte[] { 0xFF };
        _clearnet.Setup(m => m.BroadcastBitfieldAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _i2p.Setup(m => m.BroadcastBitfieldAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        await composite.BroadcastBitfieldAsync(bf);

        _clearnet.Verify(m => m.BroadcastBitfieldAsync(bf, It.IsAny<CancellationToken>()), Times.Once);
        _i2p.Verify(m => m.BroadcastBitfieldAsync(bf, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SetLocalBitfieldProvider_SetsBoth()
    {
        Func<byte[]?> provider = () => null;
        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.SetLocalBitfieldProvider(provider);

        _clearnet.Verify(m => m.SetLocalBitfieldProvider(provider), Times.Once);
        _i2p.Verify(m => m.SetLocalBitfieldProvider(provider), Times.Once);
    }

    [Fact]
    public void Dispose_DisposesBoth()
    {
        var composite = new CompositePeerManager(_clearnet.Object, _i2p.Object);
        composite.Dispose();

        _clearnet.Verify(m => m.Dispose(), Times.Once);
        _i2p.Verify(m => m.Dispose(), Times.Once);
    }
}
