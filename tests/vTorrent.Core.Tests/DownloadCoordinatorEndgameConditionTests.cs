using System.Collections.Generic;
using FluentAssertions;
using vTorrent.Core.Download;
using Xunit;

namespace vTorrent.Tests.Unit.Core;

/// <summary>
/// Pins the block-level endgame-entry condition (libtorrent request_a_block model):
/// endgame engages ONLY when no un-requested wanted block remains anywhere, never on a
/// piece-count heuristic. Regression guard for the premature-endgame bug where a fresh
/// single-peer torrent tripped strict endgame at ~0% because the picker opened one block
/// in many pieces.
/// </summary>
public class DownloadCoordinatorEndgameConditionTests
{
    private const int BlockSize = 16384;

    // A piece with `blocks` blocks; the first `requested` of them marked requested (state 1),
    // the rest left free (state 0).
    private static PieceBlockTracker Piece(int index, int blocks, int requested)
    {
        var t = new PieceBlockTracker(index, (long)blocks * BlockSize, BlockSize);
        for (int i = 0; i < requested; i++)
            t.GetNextBlock().Should().NotBeNull();
        return t;
    }

    private static PieceBlockTracker FullyRequested(int index, int blocks) => Piece(index, blocks, blocks);

    [Fact]
    public void FreshMultiPieceTorrent_OneBlockPerPiece_IsNotEndgame()
    {
        // 48 pieces, none completed, every piece "in progress" with a single block requested
        // (15 free blocks each). Old heuristic: inProgress(48) >= remaining(48) => endgame at 0%.
        var inProgress = new List<PieceBlockTracker>();
        for (int i = 0; i < 48; i++)
            inProgress.Add(Piece(i, blocks: 16, requested: 1));

        DownloadCoordinator.ComputeEndgame(inProgress, piecesCompleted: 0, totalPieces: 48)
            .Should().BeFalse();
    }

    [Fact]
    public void UntouchedPiecesRemain_IsNotEndgame()
    {
        // Only 5 pieces touched (all fully requested) but 43 wanted pieces untouched.
        var inProgress = new List<PieceBlockTracker>();
        for (int i = 0; i < 5; i++)
            inProgress.Add(FullyRequested(i, 16));

        DownloadCoordinator.ComputeEndgame(inProgress, piecesCompleted: 0, totalPieces: 48)
            .Should().BeFalse();
    }

    [Fact]
    public void AllPiecesTouched_ButFreeBlockRemains_IsNotEndgame()
    {
        // Every not-completed piece is in progress, but one still has a free block.
        var inProgress = new List<PieceBlockTracker>
        {
            FullyRequested(0, 16),
            Piece(1, blocks: 16, requested: 15), // one free block left
        };

        DownloadCoordinator.ComputeEndgame(inProgress, piecesCompleted: 0, totalPieces: 2)
            .Should().BeFalse();
    }

    [Fact]
    public void NearCompletion_AllRemainingBlocksRequested_IsEndgame()
    {
        // 47/48 done; the last piece is fully requested (all blocks in flight, none free).
        var inProgress = new List<PieceBlockTracker> { FullyRequested(47, 16) };

        DownloadCoordinator.ComputeEndgame(inProgress, piecesCompleted: 47, totalPieces: 48)
            .Should().BeTrue();
    }

    [Fact]
    public void NoInProgressPieces_IsNotEndgame()
    {
        DownloadCoordinator.ComputeEndgame(new List<PieceBlockTracker>(), piecesCompleted: 10, totalPieces: 48)
            .Should().BeFalse();
    }

    [Fact]
    public void HasUnrequestedBlocks_TracksFreeState()
    {
        var t = new PieceBlockTracker(0, 2L * BlockSize, BlockSize);
        t.HasUnrequestedBlocks().Should().BeTrue();

        t.GetNextBlock();
        t.HasUnrequestedBlocks().Should().BeTrue(); // one still free

        t.GetNextBlock();
        t.HasUnrequestedBlocks().Should().BeFalse(); // all requested
    }
}
