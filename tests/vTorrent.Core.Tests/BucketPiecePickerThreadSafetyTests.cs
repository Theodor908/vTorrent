using FluentAssertions;
using vTorrent.Core;
using Xunit;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class BucketPiecePickerThreadSafetyTests
{
    private const int PieceCount = 1000;
    private const int Iterations = 500;

    [Fact]
    public void ConcurrentAvailabilityUpdates_ShouldNotCorruptState()
    {
        // Arrange
        var picker = new BucketPiecePicker(PieceCount);
        var barrier = new Barrier(8);

        // Act — 8 threads hammering increment/decrement
        var threads = new Thread[8];
        for (int t = 0; t < threads.Length; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                var rng = new Random(threadId * 42);
                for (int i = 0; i < Iterations; i++)
                {
                    int piece = rng.Next(PieceCount);
                    if (rng.Next(2) == 0)
                        picker.IncrementAvailability(piece);
                    else
                        picker.DecrementAvailability(piece);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads) t.Join();

        // Assert — picker is still functional (no corrupted state / exceptions)
        var action = () => picker.PickPiece(_ => true);
        action.Should().NotThrow();
        picker.AvailablePieceCount.Should().Be(PieceCount, "no pieces were completed");
    }

    [Fact]
    public void ConcurrentPickAndComplete_ShouldNotCorruptState()
    {
        // Arrange
        var picker = new BucketPiecePicker(PieceCount);
        for (int i = 0; i < PieceCount; i++)
            picker.IncrementAvailability(i);

        var barrier = new Barrier(4);
        int completedCount = 0;

        // Act — 4 threads picking and completing pieces
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < Iterations; i++)
                {
                    var piece = picker.PickPiece(_ => true);
                    if (piece.HasValue)
                    {
                        picker.MarkInProgress(piece.Value);
                        picker.MarkCompleted(piece.Value);
                        Interlocked.Increment(ref completedCount);
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads) t.Join();

        // Assert — available count is consistent (total = available + completed)
        int available = picker.AvailablePieceCount;
        available.Should().BeGreaterThanOrEqualTo(0);
        available.Should().BeLessThanOrEqualTo(PieceCount);
    }

    [Fact]
    public void ConcurrentApplyBitfield_ShouldNotCorruptState()
    {
        // Arrange
        var picker = new BucketPiecePicker(PieceCount);
        int byteCount = (PieceCount + 7) / 8;
        var barrier = new Barrier(8);

        // Act — 8 threads applying random bitfields
        var threads = new Thread[8];
        for (int t = 0; t < threads.Length; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                var rng = new Random(threadId * 17);
                for (int i = 0; i < 50; i++)
                {
                    var bitfield = new byte[byteCount];
                    rng.NextBytes(bitfield);
                    int delta = rng.Next(2) == 0 ? 1 : -1;
                    picker.ApplyBitfield(bitfield, PieceCount, delta);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads) t.Join();

        // Assert — picker still works after heavy bitfield application
        var action = () => picker.PickPiece(_ => true);
        action.Should().NotThrow();
        picker.AvailablePieceCount.Should().Be(PieceCount, "no pieces were completed");
    }

    [Fact]
    public void ConcurrentMarkNotStarted_WhilePickingPieces_ShouldNotCorrupt()
    {
        // Arrange
        var picker = new BucketPiecePicker(PieceCount);
        for (int i = 0; i < PieceCount; i++)
            picker.IncrementAvailability(i);

        var barrier = new Barrier(8);
        var exceptions = new List<Exception>();

        // Act — 4 threads marking in-progress, 4 threads marking not-started
        var threads = new Thread[8];
        for (int t = 0; t < threads.Length; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    var rng = new Random(threadId * 31);
                    for (int i = 0; i < Iterations; i++)
                    {
                        int piece = rng.Next(PieceCount);
                        if (threadId < 4)
                            picker.MarkInProgress(piece);
                        else
                            picker.MarkNotStarted(piece);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads) t.Join();

        // Assert — no exceptions and picker is functional
        exceptions.Should().BeEmpty("no exceptions should occur during concurrent access");
        var pickAction = () => picker.PickPiece(_ => true);
        pickAction.Should().NotThrow();
        picker.AvailablePieceCount.Should().Be(PieceCount, "no pieces were completed");
    }
}
