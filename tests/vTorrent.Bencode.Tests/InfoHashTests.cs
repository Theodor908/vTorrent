using FluentAssertions;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class InfoHashTests
{
    private static SHA1Hash MakeSha1(byte fill = 0xAA)
    {
        var b = new byte[20]; Array.Fill(b, fill);
        return new SHA1Hash(b);
    }

    private static SHA256Hash MakeSha256(byte fill = 0xBB)
    {
        var b = new byte[32]; Array.Fill(b, fill);
        return new SHA256Hash(b);
    }

    [Fact]
    public void V1Only_HasCorrectFlags()
    {
        var ih = new InfoHash { V1 = MakeSha1() };
        ih.HasV1.Should().BeTrue();
        ih.HasV2.Should().BeFalse();
        ih.IsHybrid.Should().BeFalse();
        ih.Version.Should().Be(TorrentVersion.V1);
    }

    [Fact]
    public void V2Only_HasCorrectFlags()
    {
        var ih = new InfoHash { V2 = MakeSha256() };
        ih.HasV1.Should().BeFalse();
        ih.HasV2.Should().BeTrue();
        ih.IsHybrid.Should().BeFalse();
        ih.Version.Should().Be(TorrentVersion.V2);
    }

    [Fact]
    public void Hybrid_HasBothFlags()
    {
        var ih = new InfoHash { V1 = MakeSha1(), V2 = MakeSha256() };
        ih.HasV1.Should().BeTrue();
        ih.HasV2.Should().BeTrue();
        ih.IsHybrid.Should().BeTrue();
        ih.Version.Should().Be(TorrentVersion.Hybrid);
    }

    [Fact]
    public void PrimaryHex_V1Only_Returns40CharHex()
    {
        var ih = new InfoHash { V1 = MakeSha1(0xCC) };
        ih.PrimaryHex.Should().HaveLength(40);
        ih.PrimaryHex.Should().Be(new string('C', 40));
    }

    [Fact]
    public void PrimaryHex_V2Only_ReturnsTruncated40CharHex()
    {
        var ih = new InfoHash { V2 = MakeSha256(0xDD) };
        ih.PrimaryHex.Should().HaveLength(40);
        ih.PrimaryHex.Should().Be(new string('D', 40));
    }

    [Fact]
    public void PrimaryHex_Hybrid_PrefersV1()
    {
        var ih = new InfoHash { V1 = MakeSha1(0xAA), V2 = MakeSha256(0xBB) };
        ih.PrimaryHex.Should().Be(new string('A', 40));
    }

    [Fact]
    public void Equality_SameHashes_AreEqual()
    {
        var a = new InfoHash { V1 = MakeSha1(), V2 = MakeSha256() };
        var b = new InfoHash { V1 = MakeSha1(), V2 = MakeSha256() };
        a.Should().Be(b);
    }

    [Fact]
    public void Empty_HasNoHashes()
    {
        var ih = new InfoHash();
        ih.HasV1.Should().BeFalse();
        ih.HasV2.Should().BeFalse();
    }
}
