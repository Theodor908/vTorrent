using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Abstractions.Interfaces.Engine;
using vTorrent.Core;
using vTorrent.Core.Session;
using Xunit;
using vTorrent.Core.Download;
using vTorrent.Core.Engine;

namespace vTorrent.Tests.Unit.Core;

public class TransferAccumulatorTests
{
    [Fact]
    public void TorrentStatistics_ShouldImplementITransferAccumulator()
    {
        var logger = new Mock<ILogger<TorrentStatistics>>();
        var stats = new TorrentStatistics(logger.Object);

        stats.Should().BeAssignableTo<ITransferAccumulator>();
    }

    [Fact]
    public void RecordDownload_WithAccumulator_ShouldForwardToAccumulator()
    {
        var accumulator = new Mock<ITransferAccumulator>();
        var logger = new Mock<ILogger<TorrentStatistics>>();
        var stats = new TorrentStatistics(logger.Object, accumulator.Object);

        stats.RecordDownload(null, 1000);

        accumulator.Verify(a => a.AddDownload(1000), Times.Once);
    }

    [Fact]
    public void RecordUpload_WithAccumulator_ShouldForwardToAccumulator()
    {
        var accumulator = new Mock<ITransferAccumulator>();
        var logger = new Mock<ILogger<TorrentStatistics>>();
        var stats = new TorrentStatistics(logger.Object, accumulator.Object);

        stats.RecordUpload(null, 500);

        accumulator.Verify(a => a.AddUpload(500), Times.Once);
    }

    [Fact]
    public void RecordPayloadDownload_WithAccumulator_ShouldForwardToAccumulator()
    {
        var accumulator = new Mock<ITransferAccumulator>();
        var logger = new Mock<ILogger<TorrentStatistics>>();
        var stats = new TorrentStatistics(logger.Object, accumulator.Object);

        stats.RecordPayloadDownload(null, 2000);

        accumulator.Verify(a => a.AddPayloadDownload(2000), Times.Once);
    }

    [Fact]
    public void RecordPayloadUpload_WithAccumulator_ShouldForwardToAccumulator()
    {
        var accumulator = new Mock<ITransferAccumulator>();
        var logger = new Mock<ILogger<TorrentStatistics>>();
        var stats = new TorrentStatistics(logger.Object, accumulator.Object);

        stats.RecordPayloadUpload(null, 3000);

        accumulator.Verify(a => a.AddPayloadUpload(3000), Times.Once);
    }

    [Fact]
    public void RecordDownload_WithoutAccumulator_ShouldStillWork()
    {
        var logger = new Mock<ILogger<TorrentStatistics>>();
        var stats = new TorrentStatistics(logger.Object);

        stats.RecordDownload(null, 1000);

        ((IStatisticsTracker)stats).TotalDownloaded.Should().Be(1000);
    }

    [Fact]
    public void EndToEnd_EngineRecordDownload_ShouldAccumulateToManagedStats()
    {
        // Simulate ManagedTorrent.Statistics (the persistent instance)
        var managedStats = new TorrentStatistics();
        managedStats.AllTimeUploaded = 5000;    // Pre-seeded from database (payload)
        managedStats.AllTimeDownloaded = 10000; // Pre-seeded from database (payload)

        // Simulate engine's session TorrentStatistics with accumulator
        var engineLogger = new Mock<ILogger<TorrentStatistics>>();
        var engineStats = new TorrentStatistics(engineLogger.Object, (ITransferAccumulator)managedStats);

        // Engine records transfer (simulating PeerConnection callbacks)
        engineStats.RecordDownload(null, 1500);   // wire bytes (protocol overhead, not counted in AllTimeDownloaded)
        engineStats.RecordUpload(null, 800);       // wire bytes (protocol overhead, not counted in AllTimeUploaded)
        engineStats.RecordPayloadDownload(null, 1200);  // payload only
        engineStats.RecordPayloadUpload(null, 600);     // payload only

        // Engine's session counters should reflect session-only values
        engineStats.SessionDownloaded.Should().Be(1500);
        engineStats.SessionUploaded.Should().Be(800);

        // ManagedTorrent's all-time counters should include both pre-seeded + session.
        // Both directions track payload only (libtorrent: m_total_downloaded/m_total_uploaded
        // are "all time totals of uploaded and downloaded payload", torrent.hpp).
        managedStats.AllTimeUploaded.Should().Be(5600);        // 5000 + 600 (payload only)
        managedStats.AllTimeDownloaded.Should().Be(11200);     // 10000 + 1200 (payload only)
        managedStats.AllTimePayloadDownloaded.Should().Be(11200); // same counter as AllTimeDownloaded
    }

    [Fact]
    public void EngineProtocolTraffic_ShouldNotInflateAllTimeDownloaded()
    {
        // Regression: reopening the app showed "downloaded" bytes growing with zero
        // piece data — handshakes/bitfields/PEX were accumulating into the persisted
        // AllTimeDownloaded stat because it aliased the total-traffic counter.
        var managedStats = new TorrentStatistics();

        var logger = new Mock<ILogger<TorrentStatistics>>();
        var engineStats = new TorrentStatistics(logger.Object, (ITransferAccumulator)managedStats);

        // Peer connects: handshake + bitfield + extension messages, no piece data
        engineStats.RecordDownload(null, 4096);

        managedStats.AllTimeDownloaded.Should().Be(0);
    }

    [Fact]
    public void EndToEnd_MultipleEngineRestarts_ShouldNotLoseData()
    {
        // Simulate: load from DB -> run session 1 -> stop -> run session 2
        var managedStats = new TorrentStatistics();
        managedStats.AllTimeUploaded = 1000;  // From DB (payload)

        // Session 1
        var logger1 = new Mock<ILogger<TorrentStatistics>>();
        var engine1Stats = new TorrentStatistics(logger1.Object, (ITransferAccumulator)managedStats);
        engine1Stats.RecordPayloadUpload(null, 500);
        // Engine 1 stops — no += needed, managedStats already has 1500

        managedStats.AllTimeUploaded.Should().Be(1500);

        // Session 2 (fresh engine, same managed stats)
        var logger2 = new Mock<ILogger<TorrentStatistics>>();
        var engine2Stats = new TorrentStatistics(logger2.Object, (ITransferAccumulator)managedStats);
        engine2Stats.RecordPayloadUpload(null, 300);

        // Engine 2 session counter is 300 (session-scoped payload)
        engine2Stats.SessionPayloadUploaded.Should().Be(300);
        // All-time is cumulative: 1000 + 500 + 300 = 1800
        managedStats.AllTimeUploaded.Should().Be(1800);
    }

    [Fact]
    public void Accumulator_ZeroAndNegativeBytes_ShouldBeIgnored()
    {
        var managedStats = new TorrentStatistics();
        managedStats.AllTimeUploaded = 100;

        var logger = new Mock<ILogger<TorrentStatistics>>();
        var engineStats = new TorrentStatistics(logger.Object, (ITransferAccumulator)managedStats);

        engineStats.RecordUpload(null, 0);
        engineStats.RecordUpload(null, -50);
        engineStats.RecordDownload(null, 0);
        engineStats.RecordDownload(null, -10);

        managedStats.AllTimeUploaded.Should().Be(100);
        managedStats.AllTimeDownloaded.Should().Be(0);
    }
}
