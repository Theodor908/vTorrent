using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Upload;

namespace vTorrent.Core.Tests.Upload;

public class PeerRequestQueueTests
{
    [Fact]
    public void Enqueue_UnderLimit_ReturnsTrue()
    {
        var queue = new PeerRequestQueue(maxDepth: 250);
        queue.Enqueue(new BlockRequest(0, 0, 16384)).Should().BeTrue();
        queue.Count.Should().Be(1);
    }

    [Fact]
    public void Enqueue_AtLimit_ReturnsFalse()
    {
        var queue = new PeerRequestQueue(maxDepth: 3);
        queue.Enqueue(new BlockRequest(0, 0, 16384)).Should().BeTrue();
        queue.Enqueue(new BlockRequest(1, 0, 16384)).Should().BeTrue();
        queue.Enqueue(new BlockRequest(2, 0, 16384)).Should().BeTrue();
        queue.Enqueue(new BlockRequest(3, 0, 16384)).Should().BeFalse();
    }

    [Fact]
    public void TryDequeue_WithItems_ReturnsItem()
    {
        var queue = new PeerRequestQueue(maxDepth: 250);
        queue.Enqueue(new BlockRequest(5, 32768, 16384));
        queue.TryDequeue(out var request).Should().BeTrue();
        request.PieceIndex.Should().Be(5);
        request.Begin.Should().Be(32768);
        request.Length.Should().Be(16384);
    }

    [Fact]
    public void TryDequeue_Empty_ReturnsFalse()
    {
        var queue = new PeerRequestQueue(maxDepth: 250);
        queue.TryDequeue(out _).Should().BeFalse();
    }

    [Fact]
    public void Cancel_RemovesMatchingRequest()
    {
        var queue = new PeerRequestQueue(maxDepth: 250);
        queue.Enqueue(new BlockRequest(0, 0, 16384));
        queue.Enqueue(new BlockRequest(1, 0, 16384));
        queue.Enqueue(new BlockRequest(2, 0, 16384));
        queue.Cancel(1, 0);
        var results = new List<BlockRequest>();
        while (queue.TryDequeue(out var r)) results.Add(r);
        results.Should().HaveCount(2);
        results.Should().NotContain(r => r.PieceIndex == 1 && r.Begin == 0);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var queue = new PeerRequestQueue(maxDepth: 250);
        queue.Enqueue(new BlockRequest(0, 0, 16384));
        queue.Enqueue(new BlockRequest(1, 0, 16384));
        queue.Clear();
        queue.Count.Should().Be(0);
    }
}
