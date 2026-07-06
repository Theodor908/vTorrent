using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Core;
using vTorrent.Core.Interfaces;
using vTorrent.Core.PeerCommunication.Bandwidth;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Events;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Tests.Mocks;
using Xunit;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.Engine;

namespace vTorrent.Tests.Unit.PeerCommunication;

public class PeerManagerTests : IDisposable
{
    private readonly byte[] _infoHash;
    private readonly PeerSettings _settings;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly Mock<ILogger<PeerManager>> _loggerMock;
    private readonly PeerRegistry _peerRegistry;
    private readonly Mock<IStatisticsTracker> _statisticsTrackerMock;
    private readonly Mock<ITransportConnector> _transportConnectorMock;
    private PeerManager _peerManager;

    public PeerManagerTests()
    {
        _infoHash = new byte[20];
        new Random(42).NextBytes(_infoHash);

        _settings = new PeerSettings
        {
            MaxConnections = 50,
            ListenPort = 12345
        };

        _loggerMock = new Mock<ILogger<PeerManager>>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(_loggerMock.Object);

        _peerRegistry = new PeerRegistry();
        _transportConnectorMock = new Mock<ITransportConnector>();

        _peerManager = new PeerManager(
            _infoHash,
            _settings,
            _loggerFactoryMock.Object,
            _peerRegistry,
            _transportConnectorMock.Object);
    }

    public void Dispose()
    {
        _peerManager?.Dispose();
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullInfoHash_ShouldThrow()
    {
        var act = () => new PeerManager(
            null!,
            _settings,
            _loggerFactoryMock.Object,
            _peerRegistry,
            _transportConnectorMock.Object);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithInvalidInfoHashLength_ShouldThrow()
    {
        var invalidHash = new byte[10]; // Not 20 bytes

        var act = () => new PeerManager(
            invalidHash,
            _settings,
            _loggerFactoryMock.Object,
            _peerRegistry,
            _transportConnectorMock.Object);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithNullSettings_ShouldThrow()
    {
        var act = () => new PeerManager(
            _infoHash,
            null!,
            _loggerFactoryMock.Object,
            _peerRegistry,
            _transportConnectorMock.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("settings");
    }

    [Fact]
    public void Constructor_WithNullLoggerFactory_ShouldThrow()
    {
        var act = () => new PeerManager(
            _infoHash,
            _settings,
            null!,
            _peerRegistry,
            _transportConnectorMock.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("loggerFactory");
    }

    [Fact]
    public void Constructor_WithNullPeerRegistry_ShouldThrow()
    {
        var act = () => new PeerManager(
            _infoHash,
            _settings,
            _loggerFactoryMock.Object,
            null!,
            _transportConnectorMock.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("peerRegistry");
    }

    [Fact]
    public void Constructor_WithValidParameters_ShouldInitializeCorrectly()
    {
        _peerManager.ConnectedPeerCount.Should().Be(0);
        _peerManager.MaxConnections.Should().Be(_settings.MaxConnections);
        _peerManager.InfoHash.Should().BeEquivalentTo(_infoHash);
        _peerManager.ConnectedPeers.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithOptionalParameters_ShouldAcceptNulls()
    {
        var act = () => new PeerManager(
            _infoHash,
            _settings,
            _loggerFactoryMock.Object,
            _peerRegistry,
            _transportConnectorMock.Object,
            statisticsTracker: null,
            priorityCalculator: null,
            bandwidthLimiter: null);

        act.Should().NotThrow();
    }

    #endregion

    #region Property Tests

    [Fact]
    public void InfoHash_ShouldReturnCorrectHash()
    {
        _peerManager.InfoHash.Should().BeEquivalentTo(_infoHash);
    }

    [Fact]
    public void MaxConnections_ShouldMatchSettings()
    {
        _peerManager.MaxConnections.Should().Be(50);
    }

    [Fact]
    public void ConnectedPeerCount_Initially_ShouldBeZero()
    {
        _peerManager.ConnectedPeerCount.Should().Be(0);
    }

    [Fact]
    public void ConnectedPeers_Initially_ShouldBeEmpty()
    {
        _peerManager.ConnectedPeers.Should().BeEmpty();
    }

    [Fact]
    public void TotalBytesDownloaded_Initially_ShouldBeZero()
    {
        _peerManager.TotalBytesDownloaded.Should().Be(0);
    }

    [Fact]
    public void TotalBytesUploaded_Initially_ShouldBeZero()
    {
        _peerManager.TotalBytesUploaded.Should().Be(0);
    }

    #endregion

    #region SetLocalEndpoint Tests

    [Fact]
    public void SetLocalEndpoint_WithValidEndpoint_ShouldNotThrow()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 6881);

        var act = () => _peerManager.SetLocalEndpoint(endpoint);

        act.Should().NotThrow();
    }

    [Fact]
    public void SetLocalEndpoint_WithNullEndpoint_ShouldThrow()
    {
        var act = () => _peerManager.SetLocalEndpoint(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("endpoint");
    }

    #endregion

    #region SetSeeding Tests

    [Fact]
    public void SetSeeding_Enable_ShouldNotThrow()
    {
        var act = () => _peerManager.SetSeeding(true);

        act.Should().NotThrow();
    }

    [Fact]
    public void SetSeeding_Disable_ShouldNotThrow()
    {
        _peerManager.SetSeeding(true);

        var act = () => _peerManager.SetSeeding(false);

        act.Should().NotThrow();
    }

    [Fact]
    public void SetSeeding_MultipleCalls_ShouldNotThrow()
    {
        _peerManager.SetSeeding(true);
        _peerManager.SetSeeding(true);
        _peerManager.SetSeeding(false);
        _peerManager.SetSeeding(false);

        // Should not throw
    }

    #endregion

    #region SetCloseRedundantConnections Tests

    [Fact]
    public void SetCloseRedundantConnections_Enable_ShouldNotThrow()
    {
        var act = () => _peerManager.SetCloseRedundantConnections(true);

        act.Should().NotThrow();
    }

    [Fact]
    public void SetCloseRedundantConnections_Disable_ShouldNotThrow()
    {
        var act = () => _peerManager.SetCloseRedundantConnections(false);

        act.Should().NotThrow();
    }

    #endregion

    #region StartAsync Tests

    [Fact]
    public async Task StartAsync_ShouldCompleteSuccessfully()
    {
        var act = async () => await _peerManager.StartAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_ShouldThrowInvalidOperationException()
    {
        await _peerManager.StartAsync();

        var act = async () => await _peerManager.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region StopAsync Tests

    [Fact]
    public async Task StopAsync_ShouldCompleteSuccessfully()
    {
        await _peerManager.StartAsync();

        var act = async () => await _peerManager.StopAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_ShouldNotThrow()
    {
        var act = async () => await _peerManager.StopAsync();

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region IsConnected Tests

    [Fact]
    public void IsConnected_WithUnknownPeer_ShouldReturnFalse()
    {
        var peerInfo = new PeerInfo(IPAddress.Parse("192.168.1.100"), 6881);

        _peerManager.IsConnected(peerInfo).Should().BeFalse();
    }

    #endregion

    #region GetPeer Tests

    [Fact]
    public void GetPeer_WithUnknownPeer_ShouldReturnNull()
    {
        var peerInfo = new PeerInfo(IPAddress.Parse("192.168.1.100"), 6881);

        _peerManager.GetPeer(peerInfo).Should().BeNull();
    }

    #endregion

    #region GetPeersWithPiece Tests

    [Fact]
    public void GetPeersWithPiece_WithNoConnectedPeers_ShouldReturnEmpty()
    {
        var result = _peerManager.GetPeersWithPiece(0);

        result.Should().BeEmpty();
    }

    #endregion

    #region GetAvailablePeers Tests

    [Fact]
    public void GetAvailablePeers_WithNoConnectedPeers_ShouldReturnEmpty()
    {
        var result = _peerManager.GetAvailablePeers();

        result.Should().BeEmpty();
    }

    #endregion

    #region GetInterestedPeers Tests

    [Fact]
    public void GetInterestedPeers_WithNoConnectedPeers_ShouldReturnEmpty()
    {
        var result = _peerManager.GetInterestedPeers();

        result.Should().BeEmpty();
    }

    #endregion

    #region BroadcastHaveAsync Tests

    [Fact]
    public async Task BroadcastHaveAsync_WithNoConnectedPeers_ShouldNotThrow()
    {
        var act = async () => await _peerManager.BroadcastHaveAsync(0);

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region BroadcastBitfieldAsync Tests

    [Fact]
    public async Task BroadcastBitfieldAsync_WithNoConnectedPeers_ShouldNotThrow()
    {
        var bitfield = new byte[10];

        var act = async () => await _peerManager.BroadcastBitfieldAsync(bitfield);

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region AddPeersAsync Tests

    [Fact]
    public async Task AddPeersAsync_WithEmptyList_ShouldNotThrow()
    {
        var act = async () => await _peerManager.AddPeersAsync(Enumerable.Empty<PeerInfo>());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AddPeersAsync_WithPeers_ShouldNotThrow()
    {
        var peers = new[]
        {
            new PeerInfo(IPAddress.Parse("192.168.1.100"), 6881),
            new PeerInfo(IPAddress.Parse("192.168.1.101"), 6881),
            new PeerInfo(IPAddress.Parse("192.168.1.102"), 6881)
        };

        var act = async () => await _peerManager.AddPeersAsync(peers);

        // This may fail to connect but should not throw unhandled exceptions
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region CloseRedundantSeedConnectionsAsync Tests

    [Fact]
    public async Task CloseRedundantSeedConnectionsAsync_WhenNotSeeding_ShouldNotThrow()
    {
        var act = async () => await _peerManager.CloseRedundantSeedConnectionsAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CloseRedundantSeedConnectionsAsync_WhenSeeding_ShouldNotThrow()
    {
        _peerManager.SetSeeding(true);

        var act = async () => await _peerManager.CloseRedundantSeedConnectionsAsync();

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Event Tests

    [Fact]
    public void PeerConnected_Event_ShouldBeSubscribable()
    {
        EventHandler<PeerConnectedEventArgs> handler = (s, e) => { };

        var subscribe = () => _peerManager.PeerConnected += handler;
        var unsubscribe = () => _peerManager.PeerConnected -= handler;

        subscribe.Should().NotThrow();
        unsubscribe.Should().NotThrow();
    }

    [Fact]
    public void PeerDisconnected_Event_ShouldBeSubscribable()
    {
        EventHandler<PeerDisconnectedEventArgs> handler = (s, e) => { };

        var subscribe = () => _peerManager.PeerDisconnected += handler;
        var unsubscribe = () => _peerManager.PeerDisconnected -= handler;

        subscribe.Should().NotThrow();
        unsubscribe.Should().NotThrow();
    }

    [Fact]
    public void MessageReceived_Event_ShouldBeSubscribable()
    {
        EventHandler<PeerMessageEventArgs> handler = (s, e) => { };

        var subscribe = () => _peerManager.MessageReceived += handler;
        var unsubscribe = () => _peerManager.MessageReceived -= handler;

        subscribe.Should().NotThrow();
        unsubscribe.Should().NotThrow();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        var peerManager = new PeerManager(
            _infoHash,
            _settings,
            _loggerFactoryMock.Object,
            new PeerRegistry(),
            _transportConnectorMock.Object);

        var act = () => peerManager.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_MultipleCalls_ShouldNotThrow()
    {
        var peerManager = new PeerManager(
            _infoHash,
            _settings,
            _loggerFactoryMock.Object,
            new PeerRegistry(),
            _transportConnectorMock.Object);

        peerManager.Dispose();
        var act = () => peerManager.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_AfterStart_ShouldNotThrow()
    {
        var peerManager = new PeerManager(
            _infoHash,
            _settings,
            _loggerFactoryMock.Object,
            new PeerRegistry(),
            _transportConnectorMock.Object);

        await peerManager.StartAsync();

        var act = () => peerManager.Dispose();

        act.Should().NotThrow();
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentPropertyAccess_ShouldNotThrow()
    {
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    _ = _peerManager.ConnectedPeerCount;
                    _ = _peerManager.MaxConnections;
                    _ = _peerManager.ConnectedPeers;
                    _ = _peerManager.InfoHash;
                }
            }));
        }

        var act = async () => await Task.WhenAll(tasks);

        await act.Should().NotThrowAsync();
    }

    #endregion
}
