using System;
using FluentAssertions;
using Moq;
using vTorrent.Abstractions.Models;
using vTorrent.Core;
using vTorrent.Core.PeerCommunication.Models;
using Xunit;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class SmartBanTrackerTests
{
    [Fact]
    public void RecordBlock_ShouldTrackPeerAndHash()
    {
        var tracker = new SmartBanTracker();
        var peer = CreateMockPeer("1.2.3.4:6881");
        var blockData = new byte[] { 1, 2, 3, 4, 5 };

        tracker.RecordBlock(0, 0, blockData, peer);

        tracker.HasRecords(0).Should().BeTrue();
    }

    [Fact]
    public void OnPieceVerified_GoodPiece_ShouldIdentifyNoBadPeers()
    {
        var tracker = new SmartBanTracker();
        var peer = CreateMockPeer("1.2.3.4:6881");
        var blockData = new byte[16384];
        Array.Fill(blockData, (byte)0xAA);

        tracker.RecordBlock(0, 0, blockData, peer);

        // Same data = correct block
        var badPeers = tracker.OnPieceVerified(0, new[] { (0, blockData) });
        badPeers.Should().BeEmpty();
    }

    [Fact]
    public void OnPieceVerified_CorruptBlock_ShouldIdentifyBadPeer()
    {
        var tracker = new SmartBanTracker();
        var badPeer = CreateMockPeer("6.6.6.6:6881");
        var corruptData = new byte[16384];
        Array.Fill(corruptData, (byte)0xFF);
        var correctData = new byte[16384];
        Array.Fill(correctData, (byte)0xAA);

        // Bad peer sent corrupt data
        tracker.RecordBlock(0, 0, corruptData, badPeer);

        // Piece later succeeds with correct data
        var badPeers = tracker.OnPieceVerified(0, new[] { (0, correctData) });
        badPeers.Should().Contain(badPeer);
    }

    [Fact]
    public void OnPieceFailed_ShouldRetainRecords()
    {
        var tracker = new SmartBanTracker();
        var peer = CreateMockPeer("1.2.3.4:6881");

        tracker.RecordBlock(0, 0, new byte[16384], peer);
        tracker.OnPieceFailed(0);

        // Records should be kept for comparison when piece eventually succeeds
        tracker.HasRecords(0).Should().BeTrue();
    }

    [Fact]
    public void RecordBlock_SamePeerSameBlockDifferentData_ShouldBanImmediately()
    {
        var tracker = new SmartBanTracker();
        var badPeer = CreateMockPeer("6.6.6.6:6881");

        var data1 = new byte[] { 1, 2, 3 };
        var data2 = new byte[] { 4, 5, 6 };

        tracker.RecordBlock(0, 0, data1, badPeer);
        var result = tracker.RecordBlock(0, 0, data2, badPeer);

        // Same peer, same block, different data = immediate ban
        result.ShouldBanPeer.Should().BeTrue();
    }

    [Fact]
    public void OnPieceVerified_ShouldClearRecords()
    {
        var tracker = new SmartBanTracker();
        var peer = CreateMockPeer("1.2.3.4:6881");

        tracker.RecordBlock(0, 0, new byte[16384], peer);
        tracker.OnPieceVerified(0, new[] { (0, new byte[16384]) });

        tracker.HasRecords(0).Should().BeFalse();
    }

    private static IPeerConnection CreateMockPeer(string endpoint)
    {
        var mock = new Mock<IPeerConnection>();
        var parts = endpoint.Split(':');
        var peerInfo = new PeerInfo(
            System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1]));
        mock.Setup(p => p.PeerInfo).Returns(peerInfo);
        mock.Setup(p => p.IsConnected).Returns(true);
        return mock.Object;
    }
}
