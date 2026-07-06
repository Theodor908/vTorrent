using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core;
using vTorrent.Core.Session;
using vTorrent.Core.PeerCommunication.Events;
using vTorrent.Core.PeerCommunication.Models;
using Xunit;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.Upload;

namespace vTorrent.Tests.Unit.Core;

public class ChokingManagerTests : IDisposable
{
    private readonly Mock<ILogger<ChokingManager>> _loggerMock;
    private readonly Mock<IPeerManager> _peerManagerMock;
    private readonly Mock<ILogger<TorrentStatistics>> _statsLoggerMock;
    private readonly TorrentStatistics _statisticsTracker;
    private readonly ChokingManager _manager;

    public ChokingManagerTests()
    {
        _loggerMock = new Mock<ILogger<ChokingManager>>();
        _peerManagerMock = new Mock<IPeerManager>();
        _statsLoggerMock = new Mock<ILogger<TorrentStatistics>>();

        _statisticsTracker = new TorrentStatistics(_statsLoggerMock.Object);

        _peerManagerMock.Setup(pm => pm.ConnectedPeers).Returns(new List<IPeerConnection>());

        _manager = new ChokingManager(
            _peerManagerMock.Object,
            _statisticsTracker,
            () => false, // isSeedingFunc
            _loggerMock.Object);
    }

    public void Dispose()
    {
        _manager?.Dispose();
        _statisticsTracker?.Dispose();
    }

    #region Construction

    [Fact]
    public void Constructor_WithNullPeerManager_ShouldThrow()
    {
        var act = () => new ChokingManager(
            null!,
            _statisticsTracker,
            () => false,
            _loggerMock.Object);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullStatisticsTracker_ShouldThrow()
    {
        var act = () => new ChokingManager(
            _peerManagerMock.Object,
            null!,
            () => false,
            _loggerMock.Object);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullIsSeedingFunc_ShouldThrow()
    {
        var act = () => new ChokingManager(
            _peerManagerMock.Object,
            _statisticsTracker,
            null!,
            _loggerMock.Object);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        var act = () => new ChokingManager(
            _peerManagerMock.Object,
            _statisticsTracker,
            () => false,
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultAlgorithm()
    {
        _manager.Algorithm.Should().Be(ChokingAlgorithm.RateBased);
    }

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultSeedAlgorithm()
    {
        _manager.SeedAlgorithm.Should().Be(SeedChokingAlgorithm.FastestUpload);
    }

    [Fact]
    public void Constructor_ShouldInitializeCountsToZero()
    {
        _manager.UnchokedPeerCount.Should().Be(0);
        _manager.InterestedPeerCount.Should().Be(0);
        _manager.SnubbedPeerCount.Should().Be(0);
    }

    #endregion

    #region Configuration

    [Fact]
    public void Configure_ShouldChangeAlgorithm()
    {
        _manager.Configure(algorithm: ChokingAlgorithm.FixedSlots);

        _manager.Algorithm.Should().Be(ChokingAlgorithm.FixedSlots);
    }

    [Fact]
    public void Configure_ShouldChangeSeedAlgorithm()
    {
        _manager.Configure(seedAlgorithm: SeedChokingAlgorithm.RoundRobin);

        _manager.SeedAlgorithm.Should().Be(SeedChokingAlgorithm.RoundRobin);
    }

    [Fact]
    public void Configure_ShouldChangeAntiLeechAlgorithm()
    {
        _manager.Configure(seedAlgorithm: SeedChokingAlgorithm.AntiLeech);

        _manager.SeedAlgorithm.Should().Be(SeedChokingAlgorithm.AntiLeech);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(4, 16)]
    [InlineData(2, 8)]
    public void Configure_ShouldChangeSlotLimits(int min, int max)
    {
        _manager.Configure(minSlots: min, maxSlots: max);

        // CurrentUploadSlots should be at least min
        _manager.CurrentUploadSlots.Should().BeGreaterOrEqualTo(min);
    }

    #endregion

    #region OnPeerInterested

    [Fact]
    public void OnPeerInterested_ShouldIncrementInterestedCount()
    {
        var peerMock = CreateMockPeer();

        _manager.OnPeerInterested(peerMock.Object);

        _manager.InterestedPeerCount.Should().Be(1);
    }

    [Fact]
    public void OnPeerInterested_MultiplePeers_ShouldIncrementCorrectly()
    {
        var peer1 = CreateMockPeer();
        var peer2 = CreateMockPeer();
        var peer3 = CreateMockPeer();

        _manager.OnPeerInterested(peer1.Object);
        _manager.OnPeerInterested(peer2.Object);
        _manager.OnPeerInterested(peer3.Object);

        _manager.InterestedPeerCount.Should().Be(3);
    }

    #endregion

    #region OnPeerNotInterested

    [Fact]
    public void OnPeerNotInterested_ShouldDecrementInterestedCount()
    {
        var peerMock = CreateMockPeer();

        _manager.OnPeerInterested(peerMock.Object);
        _manager.InterestedPeerCount.Should().Be(1);

        _manager.OnPeerNotInterested(peerMock.Object);
        _manager.InterestedPeerCount.Should().Be(0);
    }

    [Fact]
    public void OnPeerNotInterested_WithoutPriorInterest_ShouldNotGoNegative()
    {
        var peerMock = CreateMockPeer();

        _manager.OnPeerNotInterested(peerMock.Object);

        _manager.InterestedPeerCount.Should().Be(0);
    }

    #endregion

    #region IsPeerUnchoked

    [Fact]
    public void IsPeerUnchoked_ForUnknownPeer_ShouldReturnFalse()
    {
        var peerMock = CreateMockPeer();

        var result = _manager.IsPeerUnchoked(peerMock.Object);

        result.Should().BeFalse();
    }

    #endregion

    #region IsPeerSnubbed

    [Fact]
    public void IsPeerSnubbed_ForUnknownPeer_ShouldReturnFalse()
    {
        var peerMock = CreateMockPeer();

        var result = _manager.IsPeerSnubbed(peerMock.Object);

        result.Should().BeFalse();
    }

    #endregion

    #region RecordDataReceived

    [Fact]
    public void RecordDataReceived_ShouldNotThrow()
    {
        var peerMock = CreateMockPeer();

        var act = () => _manager.RecordDataReceived(peerMock.Object);

        act.Should().NotThrow();
    }

    #endregion

    #region OnLocalPieceCompleted

    [Fact]
    public void OnLocalPieceCompleted_ShouldIncrementStatisticsTracker()
    {
        _manager.OnLocalPieceCompleted(0);

        _statisticsTracker.PiecesCompleted.Should().Be(1);
    }

    [Fact]
    public void OnLocalPieceCompleted_MultiplePieces_ShouldAccumulate()
    {
        _manager.OnLocalPieceCompleted(0);
        _manager.OnLocalPieceCompleted(1);
        _manager.OnLocalPieceCompleted(2);

        _statisticsTracker.PiecesCompleted.Should().Be(3);
    }

    #endregion

    #region TotalUploaded and TotalDownloaded

    [Fact]
    public void TotalUploaded_ShouldDelegateToStatisticsTracker()
    {
        _statisticsTracker.RecordUpload(null, 1000);

        _manager.TotalUploaded.Should().Be(1000);
    }

    [Fact]
    public void TotalDownloaded_ShouldDelegateToStatisticsTracker()
    {
        _statisticsTracker.RecordDownload(null, 2000);

        _manager.TotalDownloaded.Should().Be(2000);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        var manager = new ChokingManager(
            _peerManagerMock.Object,
            _statisticsTracker,
            () => false,
            _loggerMock.Object);

        var act = () => manager.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_MultipleCalls_ShouldNotThrow()
    {
        var manager = new ChokingManager(
            _peerManagerMock.Object,
            _statisticsTracker,
            () => false,
            _loggerMock.Object);

        manager.Dispose();
        var act = () => manager.Dispose();

        act.Should().NotThrow();
    }

    #endregion

    #region Algorithm Enum Values

    [Fact]
    public void ChokingAlgorithm_ShouldHaveExpectedValues()
    {
        ChokingAlgorithm.FixedSlots.Should().BeDefined();
        ChokingAlgorithm.RateBased.Should().BeDefined();
    }

    [Fact]
    public void SeedChokingAlgorithm_ShouldHaveExpectedValues()
    {
        SeedChokingAlgorithm.FastestUpload.Should().BeDefined();
        SeedChokingAlgorithm.RoundRobin.Should().BeDefined();
        SeedChokingAlgorithm.AntiLeech.Should().BeDefined();
    }

    #endregion

    #region Thread Safety

    [Fact]
    public void ConcurrentPeerInterested_ShouldNotThrow()
    {
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    var peer = CreateMockPeer();
                    _manager.OnPeerInterested(peer.Object);
                    _ = _manager.InterestedPeerCount;
                    _ = _manager.UnchokedPeerCount;
                }
            }));
        }

        var act = () => Task.WaitAll(tasks.ToArray());

        act.Should().NotThrow();
    }

    #endregion

    #region Helper Methods

    private static int _peerCounter = 0;

    private Mock<IPeerConnection> CreateMockPeer()
    {
        var mock = new Mock<IPeerConnection>();
        var ip = IPAddress.Parse($"192.168.1.{Interlocked.Increment(ref _peerCounter) % 255}");
        var peerInfo = new PeerInfo(ip, 6881 + (_peerCounter % 1000));
        mock.Setup(p => p.PeerInfo).Returns(peerInfo);
        mock.Setup(p => p.IsConnected).Returns(true);
        return mock;
    }

    #endregion
}
