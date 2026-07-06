using FluentAssertions;
using vTorrent.Abstractions.Records;
using vTorrent.Storage.Tests.Helpers;
using Xunit;

namespace vTorrent.Storage.Tests.Repositories;

public class PeerCacheRepositoryTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task InsertTorrent(string infoHash = "peer_test")
    {
        await _fixture.Database.InsertTorrentAsync(new TorrentRecord
        {
            InfoHash = infoHash, Name = "test", TotalSize = 1, PieceCount = 1,
            PieceSize = 1, SavePath = "/tmp", UserIntent = "Paused",
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    [Fact]
    public async Task SaveAndGetKnownPeers_RoundTrips()
    {
        await InsertTorrent();
        var peers = new[]
        {
            new KnownPeerRecord { Ip = "1.2.3.4", Port = 6881, Source = "tracker" },
            new KnownPeerRecord { Ip = "5.6.7.8", Port = 6882, Source = "dht" }
        };

        await _fixture.Database.SaveKnownPeersAsync("peer_test", peers);
        var retrieved = await _fixture.Database.GetKnownPeersAsync("peer_test");

        retrieved.Should().HaveCount(2);
    }

    [Fact]
    public async Task BanAndCheckPeer_Works()
    {
        await _fixture.Database.BanPeerAsync("10.0.0.1", "bad behavior");

        (await _fixture.Database.IsPeerBannedAsync("10.0.0.1")).Should().BeTrue();
        (await _fixture.Database.IsPeerBannedAsync("10.0.0.2")).Should().BeFalse();

        var banned = await _fixture.Database.GetBannedPeersAsync();
        banned.Should().HaveCount(1);
        banned[0].Reason.Should().Be("bad behavior");
    }

    [Fact]
    public async Task UnbanPeerAsync_RemovesBan()
    {
        await _fixture.Database.BanPeerAsync("10.0.0.5");
        await _fixture.Database.UnbanPeerAsync("10.0.0.5");

        (await _fixture.Database.IsPeerBannedAsync("10.0.0.5")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAndGetDhtNodes_RoundTrips()
    {
        var nodes = new[]
        {
            new DhtNodeRecord { NodeId = "aaa", Ip = "1.1.1.1", Port = 6881, RttMs = 50, LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            new DhtNodeRecord { NodeId = "bbb", Ip = "2.2.2.2", Port = 6882, RttMs = 100, LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        };

        await _fixture.Database.SaveDhtNodesAsync(nodes);
        var retrieved = await _fixture.Database.GetDhtNodesAsync();

        retrieved.Should().HaveCount(2);
        retrieved[0].RttMs.Should().BeLessThanOrEqualTo(retrieved[1].RttMs);
    }

    [Fact]
    public async Task SaveAndGetDhtState_RoundTrips()
    {
        await _fixture.Database.SaveDhtStateAsync("router_nodes", "node1,node2");
        var value = await _fixture.Database.GetDhtStateAsync("router_nodes");
        value.Should().Be("node1,node2");
    }

    [Fact]
    public async Task SaveDhtStateAsync_Upserts()
    {
        await _fixture.Database.SaveDhtStateAsync("key", "v1");
        await _fixture.Database.SaveDhtStateAsync("key", "v2");

        var value = await _fixture.Database.GetDhtStateAsync("key");
        value.Should().Be("v2");
    }
}
