using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Orchestration;
using vTorrent.Core.State;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Orchestration;

public class StateIndexDimensionalTests
{
    // ---------------------------------------------------------------------------
    // Helper
    // ---------------------------------------------------------------------------

    private static async Task<ManagedTorrent> CreateTorrentAsync(string suffix, TorrentStatus status)
    {
        var mt = new ManagedTorrent($"AABB00112233445566778899AABBCCDDEE{suffix}", $"Test_{suffix}");
        mt.UpdateStatus(status, force: true);
        await mt.StateController.DrainAsync();
        return mt;
    }

    private static TorrentStatus DownloadingActive => new TorrentStatus
    {
        Phase  = TransferPhase.Downloading,
        Intent = UserIntent.Active,
        FileOp = FileOperation.None
    };

    private static TorrentStatus SeedingActive => new TorrentStatus
    {
        Phase  = TransferPhase.Seeding,
        Intent = UserIntent.Active,
        FileOp = FileOperation.None
    };

    private static TorrentStatus ConnectingActive => new TorrentStatus
    {
        Phase  = TransferPhase.Connecting,
        Intent = UserIntent.Active,
        FileOp = FileOperation.None
    };

    private static TorrentStatus PausedIdle => new TorrentStatus
    {
        Phase  = TransferPhase.Idle,
        Intent = UserIntent.Paused,
        FileOp = FileOperation.None
    };

    private static TorrentStatus QueuedIdle => new TorrentStatus
    {
        Phase  = TransferPhase.Idle,
        Intent = UserIntent.Queued,
        FileOp = FileOperation.None
    };

    private static TorrentStatus ErrorStatus => new TorrentStatus
    {
        Phase  = TransferPhase.Idle,
        Intent = UserIntent.Active,
        Error  = new TorrentError { Message = "disk full" },
        FileOp = FileOperation.None
    };

    private static TorrentStatus AllocatingStatus => new TorrentStatus
    {
        Phase  = TransferPhase.Allocating,
        Intent = UserIntent.Active,
        FileOp = FileOperation.None
    };

    private static TorrentStatus CheckingFilesStatus => new TorrentStatus
    {
        Phase  = TransferPhase.CheckingFiles,
        Intent = UserIntent.Active,
        FileOp = FileOperation.None
    };

    private static TorrentStatus CheckingResumeDataStatus => new TorrentStatus
    {
        Phase  = TransferPhase.CheckingResumeData,
        Intent = UserIntent.Active,
        FileOp = FileOperation.None
    };

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Add_RegistersInAllDimensionalIndexes()
    {
        var index = new StateIndex();
        var torrent = await CreateTorrentAsync("01", DownloadingActive);

        index.Add(torrent);

        index.Downloading.Should().ContainSingle().Which.Should().BeSameAs(torrent);
        index.DownloadingCount.Should().Be(1);
    }

    [Fact]
    public async Task Remove_UnregistersFromAllDimensionalIndexes()
    {
        var index = new StateIndex();
        var torrent = await CreateTorrentAsync("02", DownloadingActive);

        index.Add(torrent);
        index.Remove(torrent);

        index.Downloading.Should().BeEmpty();
        index.DownloadingCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateStatus_MovesAcrossPhaseIndex()
    {
        var index = new StateIndex();
        var torrent = await CreateTorrentAsync("03", DownloadingActive);
        index.Add(torrent);

        var oldStatus = torrent.GetStatus();
        torrent.UpdateStatus(SeedingActive, force: true);
        await torrent.StateController.DrainAsync();
        index.UpdateStatus(torrent, oldStatus, SeedingActive);

        index.Downloading.Should().BeEmpty();
        index.Seeding.Should().ContainSingle().Which.Should().BeSameAs(torrent);
    }

    [Fact]
    public async Task UpdateStatus_MovesAcrossIntentIndex()
    {
        var index = new StateIndex();
        var torrent = await CreateTorrentAsync("04", QueuedIdle);
        index.Add(torrent);

        var oldStatus = torrent.GetStatus();
        torrent.UpdateStatus(PausedIdle, force: true);
        await torrent.StateController.DrainAsync();
        index.UpdateStatus(torrent, oldStatus, PausedIdle);

        index.Queued.Should().BeEmpty();
        index.Paused.Should().ContainSingle().Which.Should().BeSameAs(torrent);
    }

    [Fact]
    public async Task UpdateStatus_MovesAcrossErrorIndex()
    {
        var index = new StateIndex();
        var torrent = await CreateTorrentAsync("05", DownloadingActive);
        index.Add(torrent);

        var oldStatus = torrent.GetStatus();
        torrent.UpdateStatus(ErrorStatus, force: true);
        await torrent.StateController.DrainAsync();
        index.UpdateStatus(torrent, oldStatus, ErrorStatus);

        index.Error.Should().ContainSingle().Which.Should().BeSameAs(torrent);
    }

    [Fact]
    public async Task Checking_IncludesAllocatingCheckingFilesCheckingResumeData()
    {
        var index = new StateIndex();

        var t1 = await CreateTorrentAsync("06", AllocatingStatus);
        var t2 = await CreateTorrentAsync("07", CheckingFilesStatus);
        var t3 = await CreateTorrentAsync("08", CheckingResumeDataStatus);

        index.Add(t1);
        index.Add(t2);
        index.Add(t3);

        index.Checking.Should().HaveCount(3);
        index.CheckingCount.Should().Be(3);
    }

    [Fact]
    public async Task GetSnapshot_ReflectsDimensionalCounts()
    {
        var index = new StateIndex();

        index.Add(await CreateTorrentAsync("09", DownloadingActive));
        index.Add(await CreateTorrentAsync("10", SeedingActive));
        index.Add(await CreateTorrentAsync("11", PausedIdle));
        index.Add(await CreateTorrentAsync("12", ErrorStatus));
        index.Add(await CreateTorrentAsync("13", AllocatingStatus));

        var snap = index.GetSnapshot();

        snap.Downloading.Should().Be(1);
        snap.Seeding.Should().Be(1);
        snap.Paused.Should().Be(1);
        snap.Error.Should().Be(1);
        snap.Checking.Should().Be(1);
    }

    [Fact]
    public async Task GetActiveTorrents_ReturnsDownloadingSeedingConnecting()
    {
        var index = new StateIndex();

        var downloading = await CreateTorrentAsync("14", DownloadingActive);
        var seeding     = await CreateTorrentAsync("15", SeedingActive);
        var connecting  = await CreateTorrentAsync("16", ConnectingActive);
        var paused      = await CreateTorrentAsync("17", PausedIdle);

        index.Add(downloading);
        index.Add(seeding);
        index.Add(connecting);
        index.Add(paused);

        var active = index.GetActiveTorrents();

        active.Should().HaveCount(3);
        active.Should().Contain(downloading);
        active.Should().Contain(seeding);
        active.Should().Contain(connecting);
        active.Should().NotContain(paused);
    }

    // ---------------------------------------------------------------------------
    // Orthogonal pause semantics: pausing no longer resets Phase to Idle, so a
    // paused torrent keeps Phase=Downloading/Seeding with Intent=Paused. Aggregates
    // that mean "actively running" must therefore intersect with Intent=Active —
    // otherwise paused torrents consume queue slots, accrue durations, and get
    // DHT-announced. (libtorrent: paused is a flag, state stays downloading/seeding.)
    // ---------------------------------------------------------------------------

    private static TorrentStatus DownloadingPaused => new TorrentStatus
    {
        Phase  = TransferPhase.Downloading,
        Intent = UserIntent.Paused,
        FileOp = FileOperation.None
    };

    private static TorrentStatus SeedingPaused => new TorrentStatus
    {
        Phase  = TransferPhase.Seeding,
        Intent = UserIntent.Paused,
        FileOp = FileOperation.None
    };

    [Fact]
    public async Task PausedMidDownload_IsNotCountedAsActivelyDownloading()
    {
        var index = new StateIndex();
        var paused = await CreateTorrentAsync("30", DownloadingPaused);
        var active = await CreateTorrentAsync("31", DownloadingActive);

        index.Add(paused);
        index.Add(active);

        index.DownloadingCount.Should().Be(1);
        index.ActiveCount.Should().Be(1);
        index.GetActiveTorrents().Should().ContainSingle().Which.Should().BeSameAs(active);
        index.Paused.Should().ContainSingle().Which.Should().BeSameAs(paused);

        var snap = index.GetSnapshot();
        snap.Downloading.Should().Be(1);
        snap.Paused.Should().Be(1);
    }

    [Fact]
    public async Task PausedMidSeed_IsNotCountedAsActivelySeeding()
    {
        var index = new StateIndex();
        var paused = await CreateTorrentAsync("32", SeedingPaused);

        index.Add(paused);

        index.SeedingCount.Should().Be(0);
        index.GetSnapshot().Seeding.Should().Be(0);
        index.GetTorrentsWantingPeers().Should().BeEmpty();
    }

    [Fact]
    public async Task PausedMidDownload_IsNotReportedAsStalled()
    {
        var index = new StateIndex();
        // Paused torrent with zero rates and zero peers — paused, not stalled
        var paused = await CreateTorrentAsync("33", DownloadingPaused);

        index.Add(paused);

        index.StalledTorrents.Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_EmptiesAllIndexes()
    {
        var index = new StateIndex();

        index.Add(await CreateTorrentAsync("18", DownloadingActive));
        index.Add(await CreateTorrentAsync("19", SeedingActive));
        index.Add(await CreateTorrentAsync("20", PausedIdle));

        index.Clear();

        index.Downloading.Should().BeEmpty();
        index.Seeding.Should().BeEmpty();
        index.Paused.Should().BeEmpty();
        index.DownloadingCount.Should().Be(0);
        index.SeedingCount.Should().Be(0);
        index.PausedCount.Should().Be(0);
        index.GetActiveTorrents().Should().BeEmpty();
    }
}
