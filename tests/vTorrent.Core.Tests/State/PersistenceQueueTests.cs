using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Core.State;
using Xunit;

namespace vTorrent.Tests.Unit.Core.State;

public class PersistenceQueueTests
{
    [Fact]
    public async Task FlushAsync_SavesDirtyItems()
    {
        var saved = new List<string>();
        var queue = new PersistenceQueue(hash => { saved.Add(hash); return Task.CompletedTask; });
        queue.MarkDirty("hash1");
        queue.MarkDirty("hash2");
        await queue.FlushAsync();
        Assert.Contains("hash1", saved);
        Assert.Contains("hash2", saved);
    }

    [Fact]
    public async Task FlushAsync_ClearsDirtyFlags()
    {
        var saved = new List<string>();
        var queue = new PersistenceQueue(hash => { saved.Add(hash); return Task.CompletedTask; });
        queue.MarkDirty("hash1");
        await queue.FlushAsync();
        saved.Clear();
        await queue.FlushAsync();
        Assert.Empty(saved);
    }

    [Fact]
    public async Task FlushAsync_DeduplicatesMultipleMarks()
    {
        var saved = new List<string>();
        var queue = new PersistenceQueue(hash => { saved.Add(hash); return Task.CompletedTask; });
        queue.MarkDirty("hash1");
        queue.MarkDirty("hash1");
        queue.MarkDirty("hash1");
        await queue.FlushAsync();
        Assert.Single(saved);
    }

    [Fact]
    public async Task FlushAsync_ReenqueuesFailedItems()
    {
        int callCount = 0;
        var saved = new List<string>();
        var queue = new PersistenceQueue(hash =>
        {
            callCount++;
            if (callCount == 1) throw new System.IO.IOException("disk full");
            saved.Add(hash);
            return Task.CompletedTask;
        });
        queue.MarkDirty("hash1");
        await queue.FlushAsync();
        Assert.Empty(saved);
        await queue.FlushAsync();
        Assert.Single(saved);
        Assert.Equal("hash1", saved[0]);
    }

    [Fact]
    public async Task FlushAsync_NothingDirty_DoesNothing()
    {
        var callCount = 0;
        var queue = new PersistenceQueue(_ => { callCount++; return Task.CompletedTask; });
        await queue.FlushAsync();
        Assert.Equal(0, callCount);
    }
}
