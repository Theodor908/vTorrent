using System.Collections.Concurrent;
using FluentAssertions;
using Xunit;
using vTorrent.Abstractions.Models;
using vTorrent.Core;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class PieceBlockTrackerTests
{
    private const int PieceSize = 65536;  // 64 KB
    private const int BlockSize = 16384;  // 16 KB

    [Fact]
    public void GetNextBlock_ReturnsFirstFreeBlock()
    {
        var tracker = new PieceBlockTracker(0, PieceSize, BlockSize);
        var block = tracker.GetNextBlock("peerA");

        block.Should().NotBeNull();
        block!.Value.PieceIndex.Should().Be(0);
        block.Value.Begin.Should().Be(0);
        block.Value.Length.Should().Be(BlockSize);
    }

    [Fact]
    public void GetNextBlock_SkipsRequestedBlocks()
    {
        var tracker = new PieceBlockTracker(0, PieceSize, BlockSize);
        tracker.GetNextBlock("peerA"); // takes block 0

        var block = tracker.GetNextBlock("peerB");
        block.Should().NotBeNull();
        block!.Value.Begin.Should().Be(BlockSize, "block 0 is requested, returns block 1");
    }

    [Fact]
    public void MarkBlockReceived_TransitionsRequestedToReceived()
    {
        var tracker = new PieceBlockTracker(0, PieceSize, BlockSize);
        tracker.GetNextBlock("peerA"); // request block 0

        bool accepted = tracker.MarkBlockReceived(0);
        accepted.Should().BeTrue();
        tracker.IsBlockReceived(0).Should().BeTrue();
    }

    [Fact]
    public void MarkBlockReceived_AcceptsFreeBlock()
    {
        var tracker = new PieceBlockTracker(0, PieceSize, BlockSize);
        // Block 0 never requested — still free(0)
        // Should accept: endgame duplicates arrive after orphan repair resets

        bool accepted = tracker.MarkBlockReceived(0);
        accepted.Should().BeTrue("free blocks accepted for endgame/orphan repair compatibility");
        tracker.IsBlockReceived(0).Should().BeTrue();
    }

    [Fact]
    public void MarkBlockReceived_RejectsDuplicateReceive()
    {
        var tracker = new PieceBlockTracker(0, PieceSize, BlockSize);
        tracker.GetNextBlock("peerA");
        tracker.MarkBlockReceived(0);

        bool accepted = tracker.MarkBlockReceived(0);
        accepted.Should().BeFalse("block already received");
    }

    [Fact]
    public void IsComplete_AllBlocksReceived()
    {
        var tracker = new PieceBlockTracker(0, PieceSize, BlockSize);
        int blockCount = (int)Math.Ceiling((double)PieceSize / BlockSize);

        for (int i = 0; i < blockCount; i++)
        {
            tracker.GetNextBlock("peer");
            tracker.MarkBlockReceived(i * BlockSize);
            tracker.IncrementBlocksWritten(); // Simulates DiskWriteCache.AddBlock
        }

        tracker.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void ResetBlocksForPeer_FreesOnlyThatPeersBlocks()
    {
        var tracker = new PieceBlockTracker(0, PieceSize, BlockSize);
        tracker.GetNextBlock("peerA"); // block 0
        tracker.GetNextBlock("peerB"); // block 1

        int reset = tracker.ResetBlocksForPeer("peerA");
        reset.Should().Be(1);

        // Block 0 should be free again
        var block = tracker.GetNextBlock("peerC");
        block!.Value.Begin.Should().Be(0);
    }

    [Fact]
    public void ConcurrentGetNextBlock_NoDuplicates()
    {
        const int largePieceSize = 256 * 1024;
        var tracker = new PieceBlockTracker(0, largePieceSize, BlockSize);
        int blockCount = largePieceSize / BlockSize;

        var results = new ConcurrentBag<BlockRequest>();
        var barrier = new Barrier(blockCount);

        var tasks = Enumerable.Range(0, blockCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            var block = tracker.GetNextBlock("peer");
            if (block.HasValue) results.Add(block.Value);
        }));

        Task.WaitAll(tasks.ToArray());

        var uniqueBegins = results.Select(b => b.Begin).Distinct().Count();
        uniqueBegins.Should().Be(results.Count, "no duplicate blocks");
        results.Count.Should().Be(blockCount);
    }

    [Fact]
    public void LastBlock_SmallerThanBlockSize()
    {
        const int pieceSize = 33768; // 2 full + 1 partial (1000 bytes)
        var tracker = new PieceBlockTracker(0, pieceSize, BlockSize);

        tracker.GetNextBlock("a"); // 0: 16384
        tracker.GetNextBlock("a"); // 1: 16384
        var last = tracker.GetNextBlock("a"); // 2: 1000

        last.Should().NotBeNull();
        last!.Value.Length.Should().Be(1000);
    }

    [Fact]
    public void PieceIndex_ReturnsConstructorValue()
    {
        var tracker = new PieceBlockTracker(42, PieceSize, BlockSize);
        tracker.PieceIndex.Should().Be(42);
    }
}
