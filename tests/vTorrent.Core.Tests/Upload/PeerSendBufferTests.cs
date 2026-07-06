using System.Buffers;
using System.Collections.Concurrent;
using FluentAssertions;
using vTorrent.Core.Upload;
using Xunit;

namespace vTorrent.Tests.Upload;

public class PeerSendBufferTests
{
    private const int BlockSize = 16384;

    [Fact]
    public void BufferEmpty_TryDequeue_ReturnsMiss()
    {
        var buffer = new PeerSendBuffer(lowWatermarkBytes: BlockSize, factor: 50);
        buffer.TryDequeue(0, 0, BlockSize, out _).Should().BeFalse();
    }

    [Fact]
    public void EnqueueBlock_TryDequeue_MatchingRequest_ReturnsHit()
    {
        var buffer = new PeerSendBuffer(lowWatermarkBytes: BlockSize, factor: 50);
        var data = ArrayPool<byte>.Shared.Rent(BlockSize);
        buffer.Enqueue(new SendBufferEntry(PieceIndex: 0, Begin: 0, Data: data, Length: BlockSize));

        buffer.TryDequeue(0, 0, BlockSize, out var entry).Should().BeTrue();
        entry.PieceIndex.Should().Be(0);
        entry.Begin.Should().Be(0);
        ArrayPool<byte>.Shared.Return(entry.Data);
    }

    [Fact]
    public void EnqueueBlock_TryDequeue_NonMatchingRequest_ReturnsMiss()
    {
        var buffer = new PeerSendBuffer(lowWatermarkBytes: BlockSize, factor: 50);
        var data = ArrayPool<byte>.Shared.Rent(BlockSize);
        buffer.Enqueue(new SendBufferEntry(PieceIndex: 0, Begin: 0, Data: data, Length: BlockSize));

        // Request for different block
        buffer.TryDequeue(0, BlockSize, BlockSize, out _).Should().BeFalse();

        // Clean up
        buffer.Dispose();
    }

    [Fact]
    public void Dispose_ReturnsAllArrayPoolRentals()
    {
        var buffer = new PeerSendBuffer(lowWatermarkBytes: BlockSize, factor: 50);
        for (int i = 0; i < 5; i++)
        {
            var data = ArrayPool<byte>.Shared.Rent(BlockSize);
            buffer.Enqueue(new SendBufferEntry(PieceIndex: 0, Begin: i * BlockSize, Data: data, Length: BlockSize));
        }

        buffer.BufferedBytes.Should().Be(5 * BlockSize);
        buffer.Dispose();
        buffer.BufferedBytes.Should().Be(0);
    }

    [Fact]
    public void GuidedSizing_SlowPeer_GetsMinimumBlocks()
    {
        // 50 KB/s peer, factor=50 -> 50000*0.5/16384 ~ 1.5 -> floor to 1
        var buffer = new PeerSendBuffer(lowWatermarkBytes: BlockSize, factor: 50);
        var target = buffer.CalculateTargetBlocks(uploadRateBytesPerSec: 50_000, maxWatermarkBytes: 1024 * 1024);
        target.Should().Be(1);
    }

    [Fact]
    public void GuidedSizing_FastPeer_GetsProportionalBlocks()
    {
        // 2 MB/s peer, factor=50 -> 2000000*0.5/16384 ~ 61
        var buffer = new PeerSendBuffer(lowWatermarkBytes: BlockSize, factor: 50);
        var target = buffer.CalculateTargetBlocks(uploadRateBytesPerSec: 2_000_000, maxWatermarkBytes: 10 * 1024 * 1024);
        target.Should().BeInRange(55, 65);
    }

    [Fact]
    public void GuidedSizing_ClampedToMaxWatermark()
    {
        // Very fast peer but small watermark
        var buffer = new PeerSendBuffer(lowWatermarkBytes: BlockSize, factor: 50);
        var target = buffer.CalculateTargetBlocks(uploadRateBytesPerSec: 100_000_000, maxWatermarkBytes: 5 * BlockSize);
        target.Should().BeLessThanOrEqualTo(5); // clamped to max
    }

    [Fact]
    public void Invalidate_DrainsBlocksBehindPosition_KeepsAhead()
    {
        var buffer = new PeerSendBuffer(lowWatermarkBytes: BlockSize, factor: 50);

        // Enqueue blocks 0-4 of piece 0
        for (int i = 0; i < 5; i++)
        {
            var data = ArrayPool<byte>.Shared.Rent(BlockSize);
            buffer.Enqueue(new SendBufferEntry(0, i * BlockSize, data, BlockSize));
        }

        // Invalidate at block 3 — should drain blocks 0, 1, 2 and keep 3, 4
        int drained = buffer.Invalidate(pieceIndex: 0, begin: 3 * BlockSize);
        drained.Should().Be(3);
        buffer.BlockCount.Should().Be(2);

        buffer.Dispose();
    }
}
