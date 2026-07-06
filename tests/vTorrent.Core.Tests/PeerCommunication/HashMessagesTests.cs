using FluentAssertions;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PeerCommunication.Models;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication;

public class HashMessagesTests
{
    private static SHA256Hash TestRoot()
    {
        var b = new byte[32]; b[0] = 0xAA; b[31] = 0xBB;
        return new SHA256Hash(b);
    }

    // --- HashRequestMessage ---

    [Fact]
    public void HashRequest_RoundTrip_Serialization()
    {
        var msg = new HashRequestMessage
        {
            PiecesRoot = TestRoot(),
            BaseLayer = 0,
            Index = 128,
            Length = 64,
            ProofLayers = 3
        };

        var buffer = new byte[msg.SerializedSize];
        msg.WriteTo(buffer);

        var parsed = HashRequestMessage.Parse(buffer);

        parsed.PiecesRoot.Should().Be(msg.PiecesRoot);
        parsed.BaseLayer.Should().Be(0);
        parsed.Index.Should().Be(128);
        parsed.Length.Should().Be(64);
        parsed.ProofLayers.Should().Be(3);
    }

    [Fact]
    public void HashRequest_SerializedSize_Is48Bytes()
    {
        var msg = new HashRequestMessage
        {
            PiecesRoot = TestRoot(), BaseLayer = 0,
            Index = 0, Length = 2, ProofLayers = 1
        };
        msg.SerializedSize.Should().Be(48);
    }

    // --- HashesMessage ---

    [Fact]
    public void Hashes_RoundTrip_WithProofHashes()
    {
        var hashes = new SHA256Hash[3];
        for (int i = 0; i < 3; i++)
        {
            var b = new byte[32]; b[0] = (byte)(i + 1);
            hashes[i] = new SHA256Hash(b);
        }

        var msg = new HashesMessage
        {
            PiecesRoot = TestRoot(),
            BaseLayer = 0,
            Index = 0,
            Length = 2,
            ProofLayers = 1,
            Hashes = hashes
        };

        var buffer = new byte[msg.SerializedSize];
        msg.WriteTo(buffer);

        var parsed = HashesMessage.Parse(buffer);

        parsed.PiecesRoot.Should().Be(msg.PiecesRoot);
        parsed.Length.Should().Be(2);
        parsed.ProofLayers.Should().Be(1);
        parsed.Hashes.Should().HaveCount(3);
        parsed.Hashes[0].Should().Be(hashes[0]);
        parsed.Hashes[2].Should().Be(hashes[2]);
    }

    [Fact]
    public void Hashes_SerializedSize_IncludesAllHashes()
    {
        var msg = new HashesMessage
        {
            PiecesRoot = TestRoot(), BaseLayer = 0,
            Index = 0, Length = 4, ProofLayers = 2,
            Hashes = new SHA256Hash[6]
        };
        // 48 header + 6 * 32 = 48 + 192 = 240
        msg.SerializedSize.Should().Be(240);
    }

    // --- HashRejectMessage ---

    [Fact]
    public void HashReject_RoundTrip_Serialization()
    {
        var msg = new HashRejectMessage
        {
            PiecesRoot = TestRoot(),
            BaseLayer = 1,
            Index = 256,
            Length = 128,
            ProofLayers = 5
        };

        var buffer = new byte[msg.SerializedSize];
        msg.WriteTo(buffer);

        var parsed = HashRejectMessage.Parse(buffer);

        parsed.PiecesRoot.Should().Be(msg.PiecesRoot);
        parsed.BaseLayer.Should().Be(1);
        parsed.Index.Should().Be(256);
        parsed.Length.Should().Be(128);
        parsed.ProofLayers.Should().Be(5);
    }

    // --- MessageType enum ---

    [Fact]
    public void MessageType_HasV2Ids()
    {
        ((byte)MessageType.HashRequest).Should().Be(21);
        ((byte)MessageType.Hashes).Should().Be(22);
        ((byte)MessageType.HashReject).Should().Be(23);
    }
}
