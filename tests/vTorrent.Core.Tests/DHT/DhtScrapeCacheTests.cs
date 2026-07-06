using FluentAssertions;
using vTorrent.Core.DHT;
using Xunit;

namespace vTorrent.Core.Tests.DHT;

public class DhtScrapeCacheTests
{
    [Fact]
    public void DhtScrapeResult_UnionResponse_CombinesFilters()
    {
        var result = new DhtScrapeResult(new byte[20]);
        var bfsd = new byte[256]; bfsd[0] = 0xFF;
        var bfpe = new byte[256]; bfpe[1] = 0xAA;
        result.UnionResponse(bfsd, bfpe);
        result.SeedFilter.Data[0].Should().Be(0xFF);
        result.PeerFilter.Data[1].Should().Be(0xAA);
        result.LastUpdated.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void DhtScrapeResult_UnionResponse_IgnoresWrongSizeFilters()
    {
        var result = new DhtScrapeResult(new byte[20]);
        result.UnionResponse(new byte[100], new byte[300]);
        result.EstimatedSeeds.Should().Be(0);
        result.EstimatedPeers.Should().Be(0);
    }

    [Fact]
    public void DhtScrapeResult_EstimatedCounts_NonNegative()
    {
        var result = new DhtScrapeResult(new byte[20]);
        result.EstimatedSeeds.Should().BeGreaterThanOrEqualTo(0);
        result.EstimatedPeers.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void DhtScrapeResult_UnionResponse_DiscardsSaturatedFilters()
    {
        var result = new DhtScrapeResult(new byte[20]);
        var saturated = new byte[256];
        Array.Fill(saturated, (byte)0xFF);
        var normal = new byte[256]; normal[0] = 0x01;
        result.UnionResponse(saturated, normal);
        // Saturated seed filter should be discarded
        result.SeedFilter.Data[0].Should().Be(0);
        // Normal peer filter should be accepted
        result.PeerFilter.Data[0].Should().Be(0x01);
    }

    [Fact]
    public async Task DhtScrapeCache_GetScrapeResult_ReturnsNull_WhenEmpty()
    {
        await using var cache = new DhtScrapeCache(_ => Task.FromResult<DhtScrapeResult?>(null));
        cache.GetScrapeResult(new byte[20]).Should().BeNull();
    }

    [Fact]
    public void DhtScrapeCache_UpdateFromResponse_MakesCacheable()
    {
        var cache = new DhtScrapeCache(_ => Task.FromResult<DhtScrapeResult?>(null));
        var bfsd = new byte[256]; bfsd[0] = 0x01;
        var bfpe = new byte[256]; bfpe[0] = 0x03;
        var infoHash = new byte[20]; infoHash[0] = 0xAB;
        cache.UpdateFromResponse(infoHash, bfsd, bfpe);
        var result = cache.GetScrapeResult(infoHash);
        result.Should().NotBeNull();
        result!.EstimatedSeeds.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void DhtScrapeCache_UpdateFromResponse_FiresScrapeCompleted()
    {
        var cache = new DhtScrapeCache(_ => Task.FromResult<DhtScrapeResult?>(null));
        byte[]? firedHash = null;
        cache.ScrapeCompleted += (hash, _) => firedHash = hash;
        var infoHash = new byte[20]; infoHash[0] = 0xCD;
        cache.UpdateFromResponse(infoHash, new byte[256], new byte[256]);
        firedHash.Should().NotBeNull();
        firedHash![0].Should().Be(0xCD);
    }
}
