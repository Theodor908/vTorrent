using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Abstractions.Settings;
using vTorrent.Bencode.Torrents;
using vTorrent.Core;
using vTorrent.Core.Download;
using vTorrent.Core.Engine;
using vTorrent.Core.Interfaces;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.Session;
using vTorrent.Tests.Mocks;
using Xunit;

namespace vTorrent.Tests.Unit.Core;

/// <summary>
/// Verifies that the download loop forwards endgame transitions to the peer
/// prober (CLAUDE Rule 3). The prober's own endgame behavior is covered by
/// PeerReplacerEndgameTests; this locks in the wiring that drives it.
/// </summary>
public class DownloadCoordinatorEndgameWiringTests
{
    private const int TestPieceCount = 100;
    private const int TestPieceLength = 16384;

    private static DownloadCoordinator CreateCoordinator(out Mock<IPeerProber> proberMock, bool wireProber = true)
    {
        var peerManager = MockFactories.CreatePeerManagerMock();
        var pieceManager = MockFactories.CreatePieceManagerMock(TestPieceCount);
        var stats = new TorrentStatistics(MockFactories.CreateLoggerMock<TorrentStatistics>().Object);
        var endgame = MockFactories.CreateEndgameStrategyMock();
        var bitfield = new Bitfield(TestPieceCount);
        var torrentInfo = MockFactories.CreateTorrentInfo(TestPieceCount, TestPieceLength);
        var settings = new PeerSettings();
        var peerRegistry = new Mock<IPeerRegistry>();
        var logger = MockFactories.CreateLoggerMock<DownloadCoordinator>();

        var coordinator = new DownloadCoordinator(
            peerManager.Object, pieceManager.Object, stats, endgame.Object,
            bitfield, torrentInfo, settings, peerRegistry.Object, logger.Object);

        proberMock = new Mock<IPeerProber>();
        if (wireProber)
            coordinator.PeerProber = proberMock.Object;
        return coordinator;
    }

    [Fact]
    public void RisingEdge_EntersEndgameOnce_AndIsIdempotent()
    {
        using var coordinator = CreateCoordinator(out var prober);

        coordinator.NotifyProberEndgameTransition(true);
        coordinator.NotifyProberEndgameTransition(true); // still endgame — must not re-fire

        prober.Verify(p => p.EnterEndgameMode(), Times.Once);
        prober.Verify(p => p.ExitEndgameMode(), Times.Never);
    }

    [Fact]
    public void FallingEdge_ExitsEndgameOnce()
    {
        using var coordinator = CreateCoordinator(out var prober);

        coordinator.NotifyProberEndgameTransition(true);
        coordinator.NotifyProberEndgameTransition(false);
        coordinator.NotifyProberEndgameTransition(false); // still not endgame — must not re-fire

        prober.Verify(p => p.EnterEndgameMode(), Times.Once);
        prober.Verify(p => p.ExitEndgameMode(), Times.Once);
    }

    [Fact]
    public void NoTransition_WhenStateMatchesInitial()
    {
        using var coordinator = CreateCoordinator(out var prober);

        // Initial state is "not endgame"; notifying false is a no-op.
        coordinator.NotifyProberEndgameTransition(false);

        prober.Verify(p => p.EnterEndgameMode(), Times.Never);
        prober.Verify(p => p.ExitEndgameMode(), Times.Never);
    }

    [Fact]
    public void NullProber_IsNoOp()
    {
        using var coordinator = CreateCoordinator(out _, wireProber: false);

        var act = () => coordinator.NotifyProberEndgameTransition(true);

        act.Should().NotThrow();
    }
}
