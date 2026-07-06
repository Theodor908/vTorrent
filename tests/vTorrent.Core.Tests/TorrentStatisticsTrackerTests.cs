using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Core;
using vTorrent.Core.Session;
using vTorrent.Core.PeerCommunication.Models;
using Xunit;
using vTorrent.Core.Engine;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class TorrentStatisticsTrackerTests
{
    private readonly Mock<ILogger<TorrentStatistics>> _loggerMock;
    private readonly TorrentStatistics _tracker;
    private IStatisticsTracker Stats => _tracker;

    public TorrentStatisticsTrackerTests()
    {
        _loggerMock = new Mock<ILogger<TorrentStatistics>>();
        _tracker = new TorrentStatistics(_loggerMock.Object);
    }

    #region Construction

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        var act = () => new TorrentStatistics((ILogger)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldInitializeWithZeroValues()
    {
        Stats.TotalDownloaded.Should().Be(0);
        Stats.TotalUploaded.Should().Be(0);
        Stats.SessionDownloaded.Should().Be(0);
        Stats.SessionUploaded.Should().Be(0);
        Stats.PayloadDownloaded.Should().Be(0);
        Stats.PayloadUploaded.Should().Be(0);
        Stats.VerifiedDownloaded.Should().Be(0);
        Stats.PiecesCompleted.Should().Be(0);
        Stats.PiecesUploaded.Should().Be(0);
        Stats.EndgameWastedBytes.Should().Be(0);
        Stats.EndgameDuplicateBlocks.Should().Be(0);
        Stats.FailedBytes.Should().Be(0);
        Stats.TrackedPeerCount.Should().Be(0);
    }

    #endregion

    #region RecordDownload

    [Fact]
    public void RecordDownload_ShouldIncrementTotalDownloaded()
    {
        _tracker.RecordDownload(null, 1000);

        Stats.TotalDownloaded.Should().Be(1000);
    }

    [Fact]
    public void RecordDownload_ShouldIncrementSessionDownloaded()
    {
        _tracker.RecordDownload(null, 1000);

        Stats.SessionDownloaded.Should().Be(1000);
    }

    [Fact]
    public void RecordDownload_MultipleCallsShouldAccumulate()
    {
        _tracker.RecordDownload(null, 1000);
        _tracker.RecordDownload(null, 2000);
        _tracker.RecordDownload(null, 3000);

        Stats.TotalDownloaded.Should().Be(6000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void RecordDownload_WithNonPositiveBytes_ShouldNotIncrement(int bytes)
    {
        _tracker.RecordDownload(null, bytes);

        Stats.TotalDownloaded.Should().Be(0);
    }

    #endregion

    #region RecordUpload

    [Fact]
    public void RecordUpload_ShouldIncrementTotalUploaded()
    {
        _tracker.RecordUpload(null, 1000);

        Stats.TotalUploaded.Should().Be(1000);
    }

    [Fact]
    public void RecordUpload_ShouldIncrementSessionUploaded()
    {
        _tracker.RecordUpload(null, 1000);

        Stats.SessionUploaded.Should().Be(1000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecordUpload_WithNonPositiveBytes_ShouldNotIncrement(int bytes)
    {
        _tracker.RecordUpload(null, bytes);

        Stats.TotalUploaded.Should().Be(0);
    }

    #endregion

    #region RecordPayloadDownload

    [Fact]
    public void RecordPayloadDownload_ShouldIncrementPayloadDownloaded()
    {
        _tracker.RecordPayloadDownload(null, 1000);

        Stats.PayloadDownloaded.Should().Be(1000);
    }

    [Fact]
    public void RecordPayloadDownload_WithNonPositiveBytes_ShouldNotIncrement()
    {
        _tracker.RecordPayloadDownload(null, 0);
        _tracker.RecordPayloadDownload(null, -100);

        Stats.PayloadDownloaded.Should().Be(0);
    }

    #endregion

    #region RecordPayloadUpload

    [Fact]
    public void RecordPayloadUpload_ShouldIncrementPayloadUploaded()
    {
        _tracker.RecordPayloadUpload(null, 1000);

        Stats.PayloadUploaded.Should().Be(1000);
    }

    #endregion

    #region RecordVerifiedDownload

    [Fact]
    public void RecordVerifiedDownload_ShouldIncrementVerifiedDownloaded()
    {
        _tracker.RecordVerifiedDownload(1000);

        Stats.VerifiedDownloaded.Should().Be(1000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecordVerifiedDownload_WithNonPositiveBytes_ShouldNotIncrement(int bytes)
    {
        _tracker.RecordVerifiedDownload(bytes);

        Stats.VerifiedDownloaded.Should().Be(0);
    }

    #endregion

    #region RecordPieceCompleted

    [Fact]
    public void RecordPieceCompleted_ShouldIncrementPiecesCompleted()
    {
        _tracker.RecordPieceCompleted();

        Stats.PiecesCompleted.Should().Be(1);
    }

    [Fact]
    public void RecordPieceCompleted_MultipleCallsShouldAccumulate()
    {
        _tracker.RecordPieceCompleted();
        _tracker.RecordPieceCompleted();
        _tracker.RecordPieceCompleted();

        Stats.PiecesCompleted.Should().Be(3);
    }

    #endregion

    #region RecordPieceUploaded

    [Fact]
    public void RecordPieceUploaded_ShouldIncrementPiecesUploaded()
    {
        _tracker.RecordPieceUploaded();

        Stats.PiecesUploaded.Should().Be(1);
    }

    #endregion

    #region RecordFailedBytes

    [Fact]
    public void RecordFailedBytes_ShouldIncrementFailedBytes()
    {
        _tracker.RecordFailedBytes(1000);

        Stats.FailedBytes.Should().Be(1000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecordFailedBytes_WithNonPositiveBytes_ShouldNotIncrement(long bytes)
    {
        _tracker.RecordFailedBytes(bytes);

        Stats.FailedBytes.Should().Be(0);
    }

    #endregion

    #region RecordEndgameWaste

    [Fact]
    public void RecordEndgameWaste_ShouldIncrementWastedBytesAndDuplicateBlocks()
    {
        _tracker.RecordEndgameWaste(1000);

        Stats.EndgameWastedBytes.Should().Be(1000);
        Stats.EndgameDuplicateBlocks.Should().Be(1);
    }

    [Fact]
    public void RecordEndgameWaste_MultipleCallsShouldAccumulate()
    {
        _tracker.RecordEndgameWaste(1000);
        _tracker.RecordEndgameWaste(2000);

        Stats.EndgameWastedBytes.Should().Be(3000);
        Stats.EndgameDuplicateBlocks.Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecordEndgameWaste_WithNonPositiveBytes_ShouldNotIncrement(int bytes)
    {
        _tracker.RecordEndgameWaste(bytes);

        Stats.EndgameWastedBytes.Should().Be(0);
        Stats.EndgameDuplicateBlocks.Should().Be(0);
    }

    #endregion

    #region InitializeFromExisting

    [Fact]
    public void InitializeFromExisting_ShouldSetTotals()
    {
        _tracker.InitializeFromExisting(1000, 2000, 10);

        Stats.TotalDownloaded.Should().Be(1000);
        Stats.TotalUploaded.Should().Be(2000);
        Stats.PiecesCompleted.Should().Be(10);
    }

    [Fact]
    public void InitializeFromExisting_ShouldOverwriteExistingValues()
    {
        _tracker.RecordDownload(null, 500);
        _tracker.RecordUpload(null, 500);

        _tracker.InitializeFromExisting(1000, 2000, 10);

        Stats.TotalDownloaded.Should().Be(1000);
        Stats.TotalUploaded.Should().Be(2000);
    }

    #endregion

    #region ResetSession

    [Fact]
    public void ResetSession_ShouldClearSessionCounters()
    {
        _tracker.RecordDownload(null, 1000);
        _tracker.RecordUpload(null, 2000);

        _tracker.ResetSession();

        Stats.SessionDownloaded.Should().Be(0);
        Stats.SessionUploaded.Should().Be(0);
    }

    [Fact]
    public void ResetSession_ShouldKeepTotalCounters()
    {
        _tracker.RecordDownload(null, 1000);
        _tracker.RecordUpload(null, 2000);

        _tracker.ResetSession();

        Stats.TotalDownloaded.Should().Be(1000);
        Stats.TotalUploaded.Should().Be(2000);
    }

    #endregion

    #region SetPaused

    [Fact]
    public void SetPaused_WhenTrue_ShouldSetRatesToZero()
    {
        _tracker.RecordDownload(null, 10000);
        _tracker.RecordUpload(null, 5000);

        _tracker.SetPaused(true);

        Stats.DownloadRate.Should().Be(0);
        Stats.UploadRate.Should().Be(0);
    }

    [Fact]
    public void SetPaused_WhenFalse_ShouldAllowRateCalculation()
    {
        _tracker.SetPaused(true);
        _tracker.SetPaused(false);
        _tracker.RecordDownload(null, 10000);

        Stats.DownloadRate.Should().BeGreaterThan(0);
    }

    #endregion

    #region ResetRates

    [Fact]
    public void ResetRates_ShouldClearAllRateCalculators()
    {
        _tracker.RecordDownload(null, 10000);
        _tracker.RecordUpload(null, 5000);
        _tracker.RecordPayloadDownload(null, 3000);

        _tracker.ResetRates();

        Stats.DownloadRate.Should().Be(0);
        Stats.UploadRate.Should().Be(0);
        Stats.PayloadDownloadRate.Should().Be(0);
    }

    #endregion

    #region Peer Tracking

    [Fact]
    public void RegisterPeer_WithNull_ShouldNotThrow()
    {
        var act = () => _tracker.RegisterPeer(null!);
        act.Should().NotThrow();
    }

    [Fact]
    public void UnregisterPeer_WithNull_ShouldNotThrow()
    {
        var act = () => _tracker.UnregisterPeer(null!);
        act.Should().NotThrow();
    }

    [Fact]
    public void GetPeerDownloadRate_WithNull_ShouldReturnZero()
    {
        var rate = _tracker.GetPeerDownloadRate(null!);
        rate.Should().Be(0);
    }

    [Fact]
    public void GetPeerUploadRate_WithNull_ShouldReturnZero()
    {
        var rate = _tracker.GetPeerUploadRate(null!);
        rate.Should().Be(0);
    }

    [Fact]
    public void GetPeerDownloaded_WithNull_ShouldReturnZero()
    {
        var bytes = _tracker.GetPeerDownloaded(null!);
        bytes.Should().Be(0);
    }

    [Fact]
    public void GetPeerUploaded_WithNull_ShouldReturnZero()
    {
        var bytes = _tracker.GetPeerUploaded(null!);
        bytes.Should().Be(0);
    }

    [Fact]
    public void GetAllPeerStats_ShouldReturnDictionary()
    {
        var stats = _tracker.GetAllPeerStats();
        stats.Should().NotBeNull();
        stats.Should().BeEmpty();
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_ShouldClearPeerStats()
    {
        _tracker.Dispose();

        Stats.TrackedPeerCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        var act = () => _tracker.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_MultipleCalls_ShouldNotThrow()
    {
        _tracker.Dispose();
        var act = () => _tracker.Dispose();
        act.Should().NotThrow();
    }

    #endregion

    #region Thread Safety

    [Fact]
    public void ConcurrentOperations_ShouldNotThrow()
    {
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    _tracker.RecordDownload(null, 100);
                    _tracker.RecordUpload(null, 50);
                    _tracker.RecordPayloadDownload(null, 80);
                    _tracker.RecordVerifiedDownload(80);
                    _tracker.RecordPieceCompleted();
                    _ = Stats.TotalDownloaded;
                    _ = Stats.DownloadRate;
                }
            }));
        }

        var act = () => Task.WaitAll(tasks.ToArray());

        act.Should().NotThrow();
    }

    [Fact]
    public void ConcurrentOperations_ShouldProduceConsistentResults()
    {
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    _tracker.RecordDownload(null, 100);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // 10 threads x 100 iterations x 100 bytes = 100,000 bytes
        Stats.TotalDownloaded.Should().Be(100000);
    }

    #endregion
}

public class PeerTransferStatsTests
{
    #region Construction

    [Fact]
    public void Constructor_ShouldInitializeWithZeroValues()
    {
        var stats = new PeerTransferStats();

        stats.Downloaded.Should().Be(0);
        stats.Uploaded.Should().Be(0);
        stats.PayloadDownloaded.Should().Be(0);
        stats.PayloadUploaded.Should().Be(0);
        stats.DownloadRate.Should().Be(0);
        stats.UploadRate.Should().Be(0);
    }

    #endregion

    #region AddDownload

    [Fact]
    public void AddDownload_ShouldIncrementDownloaded()
    {
        var stats = new PeerTransferStats();

        stats.AddDownload(1000);

        stats.Downloaded.Should().Be(1000);
    }

    [Fact]
    public void AddDownload_MultipleCalls_ShouldAccumulate()
    {
        var stats = new PeerTransferStats();

        stats.AddDownload(1000);
        stats.AddDownload(2000);

        stats.Downloaded.Should().Be(3000);
    }

    #endregion

    #region AddUpload

    [Fact]
    public void AddUpload_ShouldIncrementUploaded()
    {
        var stats = new PeerTransferStats();

        stats.AddUpload(1000);

        stats.Uploaded.Should().Be(1000);
    }

    #endregion

    #region AddPayloadDownload

    [Fact]
    public void AddPayloadDownload_ShouldIncrementPayloadDownloaded()
    {
        var stats = new PeerTransferStats();

        stats.AddPayloadDownload(1000);

        stats.PayloadDownloaded.Should().Be(1000);
    }

    #endregion

    #region AddPayloadUpload

    [Fact]
    public void AddPayloadUpload_ShouldIncrementPayloadUploaded()
    {
        var stats = new PeerTransferStats();

        stats.AddPayloadUpload(1000);

        stats.PayloadUploaded.Should().Be(1000);
    }

    #endregion

    #region Reset

    [Fact]
    public void Reset_ShouldClearAllCounters()
    {
        var stats = new PeerTransferStats();
        stats.AddDownload(1000);
        stats.AddUpload(2000);
        stats.AddPayloadDownload(500);
        stats.AddPayloadUpload(500);

        stats.Reset();

        stats.Downloaded.Should().Be(0);
        stats.Uploaded.Should().Be(0);
        stats.PayloadDownloaded.Should().Be(0);
        stats.PayloadUploaded.Should().Be(0);
    }

    #endregion
}
