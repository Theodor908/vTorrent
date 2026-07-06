using FluentAssertions;
using vTorrent.Abstractions.Events;
using vTorrent.Core.Orchestration;
using vTorrent.Core.Session;
using Xunit;

namespace vTorrent.Tests.Unit.Core;

public class ProgressEventStatisticsTests
{
    private static TorrentProgressEventArgs MakeProgressEvent(
        int piecesCompleted, long bytesVerified, long totalBytes) =>
        new(
            piecesCompleted: piecesCompleted,
            totalPieces: 64,
            bytesDownloaded: bytesVerified,
            bytesVerified: bytesVerified,
            bytesInProgress: 0,
            bytesUploaded: 0,
            totalBytes: totalBytes,
            downloadRate: 1024,
            uploadRate: 512,
            connectedPeers: 3,
            connectedSeeds: 2,
            unchokedPeers: 1,
            seeders: 10,
            leechers: 5,
            pendingRequests: 0,
            inProgressPieces: 1);

    [Fact]
    public void ApplyProgressEventStatistics_AfterFastResume_DoesNotRegressPossessionState()
    {
        // Regression: progress bar oscillated after fast resume. TotalDone/TotalWantedDone
        // (possession state, restored from resume data and synced from the bitfield-backed
        // FileProgressTracker by BackgroundTaskManager) were clobbered on every piece
        // completion with e.BytesVerified — a session-scoped counter that restarts at 0
        // each engine start. libtorrent derives total_done/total_wanted_done from the
        // piece picker (torrent::bytes_done()), never from a session transfer counter.
        var stats = new TorrentStatistics
        {
            TotalDone = 600_000,
            TotalWantedDone = 600_000,
            TotalWanted = 1_000_000,
        };

        // Resumed at 60%; first piece of the new session completes (16 KiB verified)
        var e = MakeProgressEvent(piecesCompleted: 38, bytesVerified: 16_384, totalBytes: 1_000_000);

        TorrentOrchestrator.ApplyProgressEventStatistics(stats, e);

        stats.TotalDone.Should().Be(600_000);
        stats.TotalWantedDone.Should().Be(600_000);
    }

    [Fact]
    public void ApplyProgressEventStatistics_StillAppliesEventSourcedFields()
    {
        var stats = new TorrentStatistics();

        var e = MakeProgressEvent(piecesCompleted: 38, bytesVerified: 16_384, totalBytes: 1_000_000);

        TorrentOrchestrator.ApplyProgressEventStatistics(stats, e);

        stats.PiecesCompleted.Should().Be(38);
        stats.DownloadRate.Should().Be(1024);
        stats.UploadRate.Should().Be(512);
        stats.ConnectedPeers.Should().Be(3);
        stats.ConnectedSeeds.Should().Be(2);
    }
}
