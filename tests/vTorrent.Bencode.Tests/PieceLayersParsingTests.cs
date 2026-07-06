using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class PieceLayersParsingTests
{
    [Fact]
    public void ParseTorrent_WithPieceLayers_PopulatesProperty()
    {
        var piecesRoot = new byte[32]; piecesRoot[0] = 0xAA;
        var layerData = new byte[64]; // 2 * 32 bytes = 2 piece hashes
        layerData[0] = 0xBB;

        var dict = new BDictionary
        {
            ["announce"] = new BString("http://tracker.example.com/announce"),
            ["info"] = new BDictionary
            {
                ["name"] = new BString("v2file"),
                ["piece length"] = new BNumber(16384),
                ["meta version"] = new BNumber(2),
                ["file tree"] = new BDictionary
                {
                    ["data.bin"] = new BDictionary
                    {
                        [""] = new BDictionary
                        {
                            ["length"] = new BNumber(32768),
                            ["pieces root"] = new BString(piecesRoot)
                        }
                    }
                }
            },
            ["piece layers"] = new BDictionary
            {
                [new BString(piecesRoot)] = new BString(layerData)
            }
        };

        var torrent = Torrent.FromBDictionary(dict);

        torrent.PieceLayers.Should().NotBeNull();
        torrent.PieceLayers.Should().HaveCount(1);
    }

    [Fact]
    public void ParseTorrent_NoPieceLayers_PropertyIsNull()
    {
        var dict = new BDictionary
        {
            ["announce"] = new BString("http://tracker.example.com/announce"),
            ["info"] = new BDictionary
            {
                ["name"] = new BString("v1file"),
                ["piece length"] = new BNumber(262144),
                ["pieces"] = new BString(new byte[20]),
                ["length"] = new BNumber(100)
            }
        };

        var torrent = Torrent.FromBDictionary(dict);
        torrent.PieceLayers.Should().BeNull();
    }
}
