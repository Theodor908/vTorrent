using System;
using FluentAssertions;
using vTorrent.Abstractions.Models;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;
using vTorrent.Core.TrackerCommunication.I2P;
using vTorrent.Core.TrackerCommunication.Models;
using Xunit;

namespace vTorrent.Core.Tests.TrackerCommunication.I2P;

public class I2pTrackerResponseParsingTests
{
    [Fact]
    public void TrackerPeer_FromI2pCompact_ParsesSinglePeer()
    {
        var destHash = new byte[32];
        Random.Shared.NextBytes(destHash);

        var peer = TrackerPeer.FromI2pCompact(destHash, 0);

        peer.I2pDestination.Should().NotBeNull();
        peer.I2pDestination!.ToCompact().Should().BeEquivalentTo(destHash);
        peer.IsI2p.Should().BeTrue();
        peer.Port.Should().Be(0);
    }

    [Fact]
    public void TrackerPeer_FromI2pCompactList_ParsesMultiplePeers()
    {
        var data = new byte[96];
        Random.Shared.NextBytes(data);

        var peers = TrackerPeer.FromI2pCompactList(data);

        peers.Should().HaveCount(3);
        peers[0].IsI2p.Should().BeTrue();
        peers[0].I2pDestination!.ToCompact().Should().BeEquivalentTo(data.AsSpan(0, 32).ToArray());
        peers[1].I2pDestination!.ToCompact().Should().BeEquivalentTo(data.AsSpan(32, 32).ToArray());
        peers[2].I2pDestination!.ToCompact().Should().BeEquivalentTo(data.AsSpan(64, 32).ToArray());
    }

    [Fact]
    public void TrackerPeer_FromI2pCompactList_RejectsInvalidLength()
    {
        var data = new byte[50];
        var act = () => TrackerPeer.FromI2pCompactList(data);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TrackerPeer_FromI2pCompactList_EmptyData_ReturnsEmpty()
    {
        var peers = TrackerPeer.FromI2pCompactList(Array.Empty<byte>());
        peers.Should().BeEmpty();
    }

    [Fact]
    public void ParseTrackerResponse_CompactI2pPeers_ParsesCorrectly()
    {
        var destHash1 = new byte[32];
        var destHash2 = new byte[32];
        Random.Shared.NextBytes(destHash1);
        Random.Shared.NextBytes(destHash2);

        var compactPeers = new byte[64];
        Buffer.BlockCopy(destHash1, 0, compactPeers, 0, 32);
        Buffer.BlockCopy(destHash2, 0, compactPeers, 32, 32);

        var dict = new BDictionary
        {
            { new BString("interval"), new BNumber(1800) },
            { new BString("complete"), new BNumber(5) },
            { new BString("incomplete"), new BNumber(3) },
            { new BString("peers"), new BString(compactPeers) }
        };

        var encoded = dict.EncodeAsBytes();

        var response = I2pHttpTrackerClient.ParseTrackerResponseForTest(encoded);

        response.IsSuccess.Should().BeTrue();
        response.Interval.Should().Be(1800);
        response.Complete.Should().Be(5);
        response.Incomplete.Should().Be(3);
        response.Peers.Should().HaveCount(2);
        response.Peers[0].IsI2p.Should().BeTrue();
        response.Peers[0].I2pDestination!.ToCompact().Should().BeEquivalentTo(destHash1);
    }

    [Fact]
    public void ParseTrackerResponse_FailureReason_ReturnsFailure()
    {
        var dict = new BDictionary
        {
            { new BString("failure reason"), new BString("torrent not found") }
        };
        var encoded = dict.EncodeAsBytes();
        var response = I2pHttpTrackerClient.ParseTrackerResponseForTest(encoded);
        response.IsSuccess.Should().BeFalse();
        response.FailureReason.Should().Be("torrent not found");
    }

    [Fact]
    public void ParseTrackerResponse_EmptyBody_ReturnsFailure()
    {
        var response = I2pHttpTrackerClient.ParseTrackerResponseForTest(Array.Empty<byte>());
        response.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void ParseTrackerResponse_StandardCompactPeers_ParsesAsClearnet()
    {
        var clearnetPeer = new byte[] { 192, 168, 1, 1, 0x1A, 0xE1 }; // 192.168.1.1:6881
        var dict = new BDictionary
        {
            { new BString("interval"), new BNumber(900) },
            { new BString("peers"), new BString(clearnetPeer) }
        };
        var encoded = dict.EncodeAsBytes();
        var response = I2pHttpTrackerClient.ParseTrackerResponseForTest(encoded);
        response.IsSuccess.Should().BeTrue();
        response.Peers.Should().HaveCount(1);
        response.Peers[0].IsI2p.Should().BeFalse();
        response.Peers[0].Ip.ToString().Should().Be("192.168.1.1");
        response.Peers[0].Port.Should().Be(6881);
    }
}
