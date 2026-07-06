using FluentAssertions;
using Xunit;
using vTorrent.Abstractions.Models;
using vTorrent.Core.PeerCommunication.Extensions;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pPexExtensionTests
{
    [Fact]
    public void ExtensionName_IsI2pPex()
    {
        var ext = CreateExtension();
        ext.ExtensionName.Should().Be("i2p_pex");
    }

    [Fact]
    public void EncodeAdded_Uses32ByteDestHashes()
    {
        var peers = CreateI2pPeers(3);
        var encoded = I2pPexExtension.EncodeI2pPeers(peers);
        encoded.Length.Should().Be(3 * 32);
    }

    [Fact]
    public void DecodeAdded_Parses32ByteEntries()
    {
        var original = CreateI2pPeers(3);
        var encoded = I2pPexExtension.EncodeI2pPeers(original);
        var decoded = I2pPexExtension.DecodeI2pPeers(encoded);
        decoded.Should().HaveCount(3);
        for (int i = 0; i < 3; i++)
            decoded[i].Destination.Should().Be(original[i].Destination);
    }

    [Fact]
    public void EncodeFlags_OnlySeedBitUsed()
    {
        var peers = new[]
        {
            PeerInfo.FromI2p(MakeDestination(1), "pex"),
            PeerInfo.FromI2p(MakeDestination(2), "pex")
        };
        peers[0].IsSeed = true;
        peers[1].IsSeed = false;

        var flags = I2pPexExtension.EncodeFlags(peers);
        flags.Length.Should().Be(2);
        (flags[0] & 0x01).Should().Be(0x01); // seed
        (flags[1] & 0x01).Should().Be(0x00); // not seed
        // No encryption (0x02) or uTP (0x04) flags for I2P
        (flags[0] & 0xFE).Should().Be(0x00);
    }

    [Fact]
    public void RoundTrip_EncodeDecode_PreservesDestinations()
    {
        var peers = CreateI2pPeers(5);
        var encoded = I2pPexExtension.EncodeI2pPeers(peers);
        var decoded = I2pPexExtension.DecodeI2pPeers(encoded);

        decoded.Should().HaveCount(5);
        for (int i = 0; i < 5; i++)
        {
            decoded[i].IsI2p.Should().BeTrue();
            decoded[i].Destination!.ToCompact().Should().BeEquivalentTo(
                peers[i].Destination!.ToCompact());
        }
    }

    [Fact]
    public void DecodeEmpty_ReturnsEmptyList()
    {
        var decoded = I2pPexExtension.DecodeI2pPeers(Array.Empty<byte>());
        decoded.Should().BeEmpty();
    }

    [Fact]
    public void DecodeInvalidLength_ThrowsOrReturnsPartial()
    {
        // 33 bytes is not divisible by 32
        var data = new byte[33];
        var act = () => I2pPexExtension.DecodeI2pPeers(data);
        act.Should().Throw<ArgumentException>();
    }

    private static I2pPexExtension CreateExtension()
    {
        return new I2pPexExtension();
    }

    private static PeerInfo[] CreateI2pPeers(int count)
    {
        var peers = new PeerInfo[count];
        for (int i = 0; i < count; i++)
        {
            peers[i] = PeerInfo.FromI2p(MakeDestination((byte)(i + 1)), "pex");
        }
        return peers;
    }

    private static I2pDestination MakeDestination(byte seed)
    {
        var hash = new byte[32];
        for (int i = 0; i < 32; i++) hash[i] = (byte)(seed + i);
        return I2pDestination.FromHash(hash);
    }
}
