using FluentAssertions;
using vTorrent.Bencode.Torrents;
using vTorrent.Abstractions.Models;
using vTorrent.Desktop.ViewModels;
using Xunit;

namespace vTorrent.Tests.Unit.ViewModels;

public class TorrentViewModelV2Tests
{
    [Fact]
    public void V1Torrent_InfoHashV2Display_ShowsNA()
    {
        var vm = new TorrentViewModel(new TorrentSnapshot
        {
            InfoHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        });

        vm.InfoHashV2Display.Should().Be("N/A");
    }

    [Fact]
    public void V2Torrent_InfoHashV1Display_ShowsNA()
    {
        var vm = new TorrentViewModel(new TorrentSnapshot
        {
            InfoHashV2 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
        });

        vm.InfoHashV1Display.Should().Be("N/A");
    }

    [Fact]
    public void HybridTorrent_BothDisplaysShowValues()
    {
        var vm = new TorrentViewModel(new TorrentSnapshot
        {
            InfoHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            InfoHashV2 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
        });

        vm.InfoHashV1Display.Should().Be("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        vm.InfoHashV2Display.Should().Be("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
    }

    [Fact]
    public void ProtocolVersion_V1()
    {
        var vm = new TorrentViewModel(new TorrentSnapshot
        {
            TorrentVersionValue = TorrentVersion.V1,
        });
        vm.ProtocolVersion.Should().Be("v1");
    }

    [Fact]
    public void ProtocolVersion_V2()
    {
        var vm = new TorrentViewModel(new TorrentSnapshot
        {
            TorrentVersionValue = TorrentVersion.V2,
        });
        vm.ProtocolVersion.Should().Be("v2");
    }

    [Fact]
    public void ProtocolVersion_Hybrid()
    {
        var vm = new TorrentViewModel(new TorrentSnapshot
        {
            TorrentVersionValue = TorrentVersion.Hybrid,
        });
        vm.ProtocolVersion.Should().Be("Hybrid v1+v2");
    }
}
