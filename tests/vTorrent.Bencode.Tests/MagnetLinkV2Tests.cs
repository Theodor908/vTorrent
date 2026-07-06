using FluentAssertions;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class MagnetLinkV2Tests
{
    [Fact]
    public void Parse_V1Magnet_ReturnsV1InfoHash()
    {
        var hex = new string('A', 40);
        var uri = $"magnet:?xt=urn:btih:{hex}";

        var magnet = MagnetLink.Parse(uri);

        magnet.IsV2.Should().BeFalse();
        magnet.GetInfoHash().HasV1.Should().BeTrue();
        magnet.GetInfoHash().HasV2.Should().BeFalse();
    }

    [Fact]
    public void Parse_V2Magnet_ReturnsV2InfoHash()
    {
        var hex = new string('B', 64);
        var uri = $"magnet:?xt=urn:btmh:1220{hex}";

        var magnet = MagnetLink.Parse(uri);

        magnet.IsV2.Should().BeTrue();
        magnet.GetInfoHash().HasV2.Should().BeTrue();
        magnet.GetInfoHash().HasV1.Should().BeFalse();
    }

    [Fact]
    public void Parse_HybridMagnet_ReturnsBothHashes()
    {
        var v1Hex = new string('A', 40);
        var v2Hex = new string('B', 64);
        var uri = $"magnet:?xt=urn:btih:{v1Hex}&xt=urn:btmh:1220{v2Hex}";

        var magnet = MagnetLink.Parse(uri);

        magnet.GetInfoHash().HasV1.Should().BeTrue();
        magnet.GetInfoHash().HasV2.Should().BeTrue();
        magnet.GetInfoHash().IsHybrid.Should().BeTrue();
    }

    [Fact]
    public void ToUri_V2_EmitsbtmhScheme()
    {
        var hex = new string('B', 64);
        var uri = $"magnet:?xt=urn:btmh:1220{hex}";

        var magnet = MagnetLink.Parse(uri);
        var rebuilt = magnet.ToUri();

        rebuilt.Should().Contain("btmh:1220");
    }
}
