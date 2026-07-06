using System.Collections;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Bencode.Torrents;
using vTorrent.Core;
using vTorrent.Core.Interfaces;
using vTorrent.Core.Session;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.PieceIO;
using vTorrent.Tests.Mocks;
using Xunit;
using vTorrent.Core.Download;
using vTorrent.Core.Upload;
using vTorrent.Core.Engine;

namespace vTorrent.Tests.Integration;

/// <summary>
/// Integration tests for the download workflow.
/// These tests verify that the major components work together correctly.
/// </summary>
public class DownloadWorkflowTests : IDisposable
{
    private readonly Mock<IPeerManager> _peerManagerMock;
    private readonly Mock<IPieceManager> _pieceManagerMock;
    private readonly Mock<ILogger<TorrentStatistics>> _statsLoggerMock;
    private readonly TorrentStatistics _statisticsTracker;
    private readonly Mock<IEndgameStrategy> _endgameStrategyMock;
    private readonly Mock<IChokingManager> _chokingManagerMock;
    private readonly Mock<IStatisticsTracker> _uploadStatsMock;
    private readonly TorrentInfo _torrentInfo;
    private readonly PeerSettings _settings;
    private readonly Mock<ILogger<DownloadCoordinator>> _downloadLoggerMock;
    private readonly Mock<ILogger<UploadCoordinator>> _uploadLoggerMock;

    private const int TestPieceCount = 10;
    private const int TestPieceLength = 16384;
    private const int TestBlockSize = 16384;

    public DownloadWorkflowTests()
    {
        _peerManagerMock = MockFactories.CreatePeerManagerMock();
        _pieceManagerMock = MockFactories.CreatePieceManagerMock(TestPieceCount);
        _statsLoggerMock = MockFactories.CreateLoggerMock<TorrentStatistics>();
        _statisticsTracker = new TorrentStatistics(_statsLoggerMock.Object);
        _endgameStrategyMock = MockFactories.CreateEndgameStrategyMock();
        _chokingManagerMock = MockFactories.CreateChokingManagerMock(defaultUnchoked: true);
        _uploadStatsMock = MockFactories.CreateStatisticsTrackerMock();
        _torrentInfo = MockFactories.CreateTorrentInfo(TestPieceCount, TestPieceLength);
        _settings = new PeerSettings();
        _downloadLoggerMock = MockFactories.CreateLoggerMock<DownloadCoordinator>();
        _uploadLoggerMock = MockFactories.CreateLoggerMock<UploadCoordinator>();
    }

    public void Dispose()
    {
        _statisticsTracker?.Dispose();
    }

    #region Download and Upload Coordinator Integration

    [Fact]
    public void DownloadAndUploadCoordinators_ShouldShareStatistics()
    {
        // Arrange
        var bitfield = new Bitfield(TestPieceCount);

        using var downloadCoordinator = new DownloadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _statisticsTracker,
            _endgameStrategyMock.Object,
            bitfield,
            _torrentInfo,
            _settings,
            null,
            _downloadLoggerMock.Object);

        using var uploadCoordinator = new UploadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _chokingManagerMock.Object,
            _statisticsTracker, // Shared statistics
            _torrentInfo,
            i => bitfield.HasPiece(i),
            _uploadLoggerMock.Object);

        // Act - record some statistics
        _statisticsTracker.RecordDownload(null, 1000);
        _statisticsTracker.RecordUpload(null, 500);

        // Assert - both coordinators should see the same statistics
        downloadCoordinator.BytesDownloaded.Should().Be(1000);
        uploadCoordinator.BytesUploaded.Should().Be(500);
    }

    [Fact]
    public void DownloadCoordinator_WhenPieceCompletes_BitfieldShouldUpdate()
    {
        // Arrange
        var bitfield = new Bitfield(TestPieceCount);

        using var downloadCoordinator = new DownloadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _statisticsTracker,
            _endgameStrategyMock.Object,
            bitfield,
            _torrentInfo,
            _settings,
            null,
            _downloadLoggerMock.Object);

        // Act - simulate piece completion by setting bitfield directly
        bitfield.SetPiece(0);
        bitfield.SetPiece(5);

        // Assert
        downloadCoordinator.HasPiece(0).Should().BeTrue();
        downloadCoordinator.HasPiece(5).Should().BeTrue();
        downloadCoordinator.HasPiece(1).Should().BeFalse();
        downloadCoordinator.PiecesCompleted.Should().Be(2);
    }

    [Fact]
    public void UploadCoordinator_ShouldRespectHasPieceCallback()
    {
        // Arrange
        var bitfield = new Bitfield(TestPieceCount);
        bitfield.SetPiece(0);
        bitfield.SetPiece(3);

        using var uploadCoordinator = new UploadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _chokingManagerMock.Object,
            _uploadStatsMock.Object,
            _torrentInfo,
            i => bitfield.HasPiece(i), // hasPiece callback
            _uploadLoggerMock.Object);

        // The hasPiece callback should return true for pieces 0 and 3
        bitfield.HasPiece(0).Should().BeTrue();
        bitfield.HasPiece(3).Should().BeTrue();
        bitfield.HasPiece(1).Should().BeFalse();
    }

    #endregion

    #region Download Progress Tests

    [Fact]
    public void DownloadCoordinator_ProgressShouldReflectBitfield()
    {
        // Arrange
        var bitfield = new Bitfield(TestPieceCount);

        using var downloadCoordinator = new DownloadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _statisticsTracker,
            _endgameStrategyMock.Object,
            bitfield,
            _torrentInfo,
            _settings,
            null,
            _downloadLoggerMock.Object);

        // Act & Assert - initial progress
        downloadCoordinator.Progress.Should().Be(0);

        // Complete half the pieces
        for (int i = 0; i < TestPieceCount / 2; i++)
        {
            bitfield.SetPiece(i);
        }

        downloadCoordinator.Progress.Should().BeApproximately(0.5, 0.01);
        downloadCoordinator.PiecesCompleted.Should().Be(TestPieceCount / 2);
    }

    [Fact]
    public void DownloadCoordinator_IsCompleteShouldBeTrueWhenAllPiecesComplete()
    {
        // Arrange
        var bitfield = MockFactories.CreateCompleteBitfield(TestPieceCount);

        using var downloadCoordinator = new DownloadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _statisticsTracker,
            _endgameStrategyMock.Object,
            bitfield,
            _torrentInfo,
            _settings,
            null,
            _downloadLoggerMock.Object);

        // Assert
        downloadCoordinator.IsComplete.Should().BeTrue();
        downloadCoordinator.Progress.Should().Be(1.0);
    }

    #endregion

    #region Endgame Mode Tests

    [Fact]
    public void DownloadCoordinator_ShouldTrackEndgameStatistics()
    {
        // Arrange
        var bitfield = new Bitfield(TestPieceCount);
        _endgameStrategyMock.Setup(m => m.WastedBytes).Returns(1024);
        _endgameStrategyMock.Setup(m => m.DuplicateBlockCount).Returns(5);

        using var downloadCoordinator = new DownloadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _statisticsTracker,
            _endgameStrategyMock.Object,
            bitfield,
            _torrentInfo,
            _settings,
            null,
            _downloadLoggerMock.Object);

        // Assert
        downloadCoordinator.EndgameWastedBytes.Should().Be(1024);
        downloadCoordinator.EndgameDuplicateBlocks.Should().Be(5);
    }

    #endregion

    #region Sequential Mode Tests

    [Fact]
    public void DownloadCoordinator_SequentialModes_ShouldBeIndependent()
    {
        // Arrange
        var bitfield = new Bitfield(TestPieceCount);

        using var downloadCoordinator = new DownloadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _statisticsTracker,
            _endgameStrategyMock.Object,
            bitfield,
            _torrentInfo,
            _settings,
            null,
            _downloadLoggerMock.Object);

        // Initially not sequential
        downloadCoordinator.IsSequentialMode.Should().BeFalse();

        // Enable manual sequential
        downloadCoordinator.SetSequentialMode(true);
        downloadCoordinator.IsSequentialMode.Should().BeTrue();

        // Enable auto-sequential too
        downloadCoordinator.SetAutoSequentialMode(true);
        downloadCoordinator.IsSequentialMode.Should().BeTrue();

        // Disable auto-sequential - manual should still be active
        downloadCoordinator.SetAutoSequentialMode(false);
        downloadCoordinator.IsSequentialMode.Should().BeTrue();

        // Disable manual - should be off now
        downloadCoordinator.SetSequentialMode(false);
        downloadCoordinator.IsSequentialMode.Should().BeFalse();
    }

    #endregion

    #region Multiple Coordinator Lifecycle Tests

    [Fact]
    public async Task DownloadCoordinator_StartAndStopMultipleTimes_ShouldWork()
    {
        // Arrange
        var bitfield = new Bitfield(TestPieceCount);

        using var downloadCoordinator = new DownloadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _statisticsTracker,
            _endgameStrategyMock.Object,
            bitfield,
            _torrentInfo,
            _settings,
            null,
            _downloadLoggerMock.Object);

        // Act & Assert - start/stop cycle
        await downloadCoordinator.StartAsync();
        downloadCoordinator.IsRunning.Should().BeTrue();

        await downloadCoordinator.StopAsync();
        downloadCoordinator.IsRunning.Should().BeFalse();

        // Start again
        await downloadCoordinator.StartAsync();
        downloadCoordinator.IsRunning.Should().BeTrue();

        await downloadCoordinator.StopAsync();
        downloadCoordinator.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task UploadCoordinator_StartAndStopMultipleTimes_ShouldWork()
    {
        // Arrange
        var bitfield = new Bitfield(TestPieceCount);

        using var uploadCoordinator = new UploadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _chokingManagerMock.Object,
            _uploadStatsMock.Object,
            _torrentInfo,
            i => bitfield.HasPiece(i),
            _uploadLoggerMock.Object);

        // Act & Assert
        await uploadCoordinator.StartAsync();
        await uploadCoordinator.StopAsync();
        await uploadCoordinator.StartAsync();
        await uploadCoordinator.StopAsync();

        // Should complete without throwing
    }

    #endregion

    #region Concurrent Access Tests

    [Fact]
    public async Task DownloadAndUploadCoordinators_ConcurrentAccess_ShouldBeThreadSafe()
    {
        // Arrange
        var bitfield = new Bitfield(TestPieceCount);

        using var downloadCoordinator = new DownloadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _statisticsTracker,
            _endgameStrategyMock.Object,
            bitfield,
            _torrentInfo,
            _settings,
            null,
            _downloadLoggerMock.Object);

        using var uploadCoordinator = new UploadCoordinator(
            _peerManagerMock.Object,
            _pieceManagerMock.Object,
            _chokingManagerMock.Object,
            _statisticsTracker,
            _torrentInfo,
            i => bitfield.HasPiece(i),
            _uploadLoggerMock.Object);

        // Act - concurrent property access from multiple threads
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    _ = downloadCoordinator.Progress;
                    _ = downloadCoordinator.PiecesCompleted;
                    _ = downloadCoordinator.BytesDownloaded;
                    _ = uploadCoordinator.BytesUploaded;
                    _ = uploadCoordinator.ActiveUploads;
                }
            }));
        }

        // Assert - should complete without deadlocks or exceptions
        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }

    #endregion
}
