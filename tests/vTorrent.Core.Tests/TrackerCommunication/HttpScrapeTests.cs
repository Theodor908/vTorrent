using System;
using FluentAssertions;
using Xunit;

namespace vTorrent.Core.Tests.TrackerCommunication;

public class HttpScrapeTests
{
    [Fact]
    public void ScrapeUrl_SimpleAnnounce_ReplacesCorrectly()
    {
        var url = "http://tracker.example.com/announce";
        var result = ReplaceAnnounceWithScrape(url);
        result.Should().Be("http://tracker.example.com/scrape");
    }

    [Fact]
    public void ScrapeUrl_AnnounceWithPath_ReplacesLastOccurrence()
    {
        var url = "http://tracker.example.com/announce/sub/announce";
        var result = ReplaceAnnounceWithScrape(url);
        result.Should().Be("http://tracker.example.com/announce/sub/scrape");
    }

    [Fact]
    public void ScrapeUrl_AnnouncePhp_PreservesExtension()
    {
        var url = "http://tracker.example.com/announce.php?passkey=abc";
        var result = ReplaceAnnounceWithScrape(url);
        result.Should().Be("http://tracker.example.com/scrape.php?passkey=abc");
    }

    [Fact]
    public void ScrapeUrl_NoAnnounce_ReturnsNull()
    {
        var url = "http://tracker.example.com/api/v1/track";
        var result = ReplaceAnnounceWithScrape(url);
        result.Should().BeNull();
    }

    private static string? ReplaceAnnounceWithScrape(string trackerUrl)
    {
        int idx = trackerUrl.LastIndexOf("/announce");
        if (idx < 0) return null;
        return string.Concat(
            trackerUrl.AsSpan(0, idx),
            "/scrape",
            trackerUrl.AsSpan(idx + "/announce".Length));
    }
}
