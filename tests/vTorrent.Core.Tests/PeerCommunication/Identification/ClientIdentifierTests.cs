using System;
using System.Text;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Identification;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication.Identification;

public class ClientIdentifierTests
{
    // Helper: builds a 20-byte peer ID from a string (pads with zeros)
    private static byte[] PeerId(string ascii)
    {
        var bytes = new byte[20];
        Encoding.ASCII.GetBytes(ascii, 0, Math.Min(ascii.Length, 20), bytes, 0);
        return bytes;
    }

    // === Azureus-style tests ===

    [Theory]
    [InlineData("-AZ2060-xxxxxxxxxxxx", "Azureus", "2.0.6.0")]
    [InlineData("-UT3520-xxxxxxxxxxxx", "uTorrent", "3.5.2.0")]
    [InlineData("-TR3040-xxxxxxxxxxxx", "Transmission", "3.0.4.0")]
    [InlineData("-qB4520-xxxxxxxxxxxx", "qBittorrent", "4.5.2.0")]
    [InlineData("-DE2140-xxxxxxxxxxxx", "Deluge", "2.1.4.0")]
    [InlineData("-LT2090-xxxxxxxxxxxx", "libtorrent", "2.0.9.0")]
    [InlineData("-VT1000-xxxxxxxxxxxx", "vTorrent", "1.0.0.0")]
    [InlineData("-BT7120-xxxxxxxxxxxx", "BitTorrent", "7.1.2.0")]
    [InlineData("-BC0010-xxxxxxxxxxxx", "BitComet", "0.0.1.0")]
    [InlineData("-FW1230-xxxxxxxxxxxx", "FrostWire", "1.2.3.0")]
    public void Identify_AzureusStyle_ReturnsCorrectClient(string peerIdStr, string expectedName, string expectedVersion)
    {
        var result = ClientIdentifier.Identify(PeerId(peerIdStr));
        result.Name.Should().Be(expectedName);
        result.Version.Should().Be(expectedVersion);
    }

    [Fact]
    public void Identify_AzureusStyle_KeepsAllFourVersionComponents()
    {
        // libtorrent always shows all 4 components, no trimming
        var result = ClientIdentifier.Identify(PeerId("-LT2000-xxxxxxxxxxxx"));
        result.Version.Should().Be("2.0.0.0");
    }

    // === Shadow-style tests ===

    [Theory]
    [InlineData("T", "BitTornado")]
    [InlineData("S", "Shadow")]
    [InlineData("A", "ABC")]
    [InlineData("M", "Mainline")]
    public void Identify_ShadowStyle_RecognizesClient(string clientChar, string expectedName)
    {
        // Build Shadow-style ID: client char + version digits + dash padding at 6-7-8
        var bytes = new byte[20];
        bytes[0] = (byte)clientChar[0];
        bytes[1] = (byte)'0';
        bytes[2] = (byte)'3';
        bytes[3] = (byte)'0';
        bytes[4] = (byte)'0';
        bytes[5] = (byte)'0';
        bytes[6] = (byte)'-';
        bytes[7] = (byte)'-';
        bytes[8] = (byte)'-';
        var result = ClientIdentifier.Identify(bytes);
        result.Name.Should().Be(expectedName);
    }

    // === Generic pattern tests ===

    [Theory]
    [InlineData("exbc", "BitComet")]
    [InlineData("XBT", "XBT")]
    [InlineData("OP", "Opera")]
    [InlineData("-ML", "MLdonkey")]
    [InlineData("TIX", "Tixati")]
    public void Identify_GenericPattern_RecognizesClient(string pattern, string expectedName)
    {
        var bytes = new byte[20];
        Encoding.ASCII.GetBytes(pattern, 0, pattern.Length, bytes, 0);
        var result = ClientIdentifier.Identify(bytes);
        result.Name.Should().Be(expectedName);
    }

    // === Edge cases ===

    [Fact]
    public void Identify_EmptySpan_ReturnsUnknown()
    {
        var result = ClientIdentifier.Identify(ReadOnlySpan<byte>.Empty);
        result.Should().Be(ClientSoftware.Unknown);
    }

    [Fact]
    public void Identify_ShortSpan_ReturnsUnknown()
    {
        var result = ClientIdentifier.Identify(new byte[5]);
        result.Should().Be(ClientSoftware.Unknown);
    }

    [Fact]
    public void Identify_AllZeros_ReturnsUnknown()
    {
        var result = ClientIdentifier.Identify(new byte[20]);
        result.Should().Be(ClientSoftware.Unknown);
    }

    [Fact]
    public void Identify_UnknownAzureusCode_ReturnsUnknownWithCode()
    {
        var result = ClientIdentifier.Identify(PeerId("-ZZ1234-xxxxxxxxxxxx"));
        result.Name.Should().Contain("Unknown");
    }

    [Fact]
    public void ClientSoftware_ToString_FormatsCorrectly()
    {
        new ClientSoftware("qBittorrent", "4.5.2").ToString().Should().Be("qBittorrent 4.5.2");
        new ClientSoftware("Unknown", "").ToString().Should().Be("Unknown");
    }
}
