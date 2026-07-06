using FluentAssertions;
using Xunit;
using vTorrent.Core.Orchestration;
using vTorrent.Bencode.Torrents;
using System.Collections.Generic;

namespace vTorrent.Core.Tests.Orchestration;

public class PrivateTorrentEnforcementTests
{
    [Fact]
    public void IsPrivate_ReturnsTrue_WhenTorrentInfoIsPrivate()
    {
        var managed = new ManagedTorrent("AABBCCDD00112233AABBCCDD00112233AABBCCDD", "TestPrivate");
        managed.Torrent = CreateTorrentWithPrivateFlag(isPrivate: true);

        managed.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void IsPrivate_ReturnsFalse_WhenTorrentInfoIsNotPrivate()
    {
        var managed = new ManagedTorrent("AABBCCDD00112233AABBCCDD00112233AABBCCDD", "TestPublic");
        managed.Torrent = CreateTorrentWithPrivateFlag(isPrivate: false);

        managed.IsPrivate.Should().BeFalse();
    }

    [Fact]
    public void IsPrivate_ReturnsFalse_WhenNoMetadata()
    {
        var managed = new ManagedTorrent("AABBCCDD00112233AABBCCDD00112233AABBCCDD", "TestMagnet");
        // No Torrent set (magnet link before metadata)

        managed.IsPrivate.Should().BeFalse();
    }

    private static Torrent CreateTorrentWithPrivateFlag(bool isPrivate)
    {
        var pieceHashes = new PieceHashes(new byte[20]); // 1 piece
        var files = new List<TorrentFile>
        {
            new TorrentFile { Path = new[] { "test.dat" }, Length = 16384 }
        };

        var info = new TorrentInfo
        {
            Name = "test",
            PieceLength = 16384,
            Pieces = pieceHashes,
            IsPrivate = isPrivate,
            Files = files
        };

        return new Torrent
        {
            Info = info,
            Announce = "http://tracker.example.com/announce"
        };
    }
}
