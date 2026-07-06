using FluentAssertions;
using Xunit;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Server.Services;

namespace vTorrent.Server.Tests.Services;

public class ServerTorrentServiceTests
{
    [Fact]
    public void FilterByPhase_ReturnsMatchingTorrents()
    {
        var snapshots = new List<TorrentSnapshot>
        {
            new() { InfoHash = "a", Status = new TorrentStatus { Phase = TransferPhase.Downloading, Intent = UserIntent.Active } },
            new() { InfoHash = "b", Status = new TorrentStatus { Phase = TransferPhase.Seeding, Intent = UserIntent.Active } },
            new() { InfoHash = "c", Status = new TorrentStatus { Phase = TransferPhase.Downloading, Intent = UserIntent.Active } },
        };

        var result = ServerTorrentService.ApplyFilters(snapshots, phase: "downloading");
        result.Should().HaveCount(2);
        result.Select(s => s.InfoHash).Should().BeEquivalentTo("a", "c");
    }

    [Fact]
    public void SortByName_Ascending()
    {
        var snapshots = new List<TorrentSnapshot>
        {
            new() { InfoHash = "a", Name = "Zebra" },
            new() { InfoHash = "b", Name = "Alpha" },
        };

        var result = ServerTorrentService.ApplySort(snapshots, "name:asc");
        result.First().Name.Should().Be("Alpha");
    }

    [Fact]
    public void Pagination_AppliesLimitAndOffset()
    {
        var snapshots = Enumerable.Range(0, 20)
            .Select(i => new TorrentSnapshot { InfoHash = i.ToString() })
            .ToList();

        var result = ServerTorrentService.ApplyPagination(snapshots, limit: 5, offset: 10);
        result.Should().HaveCount(5);
        result.First().InfoHash.Should().Be("10");
    }
}
