using System;
using System.Linq;
using System.Text;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Identification;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication.Identification;

public class ClientFingerprintTests
{
    [Fact]
    public void GeneratePrefix_VT_1_0_0_ReturnsCorrectPrefix()
    {
        var prefix = ClientFingerprint.GeneratePrefix("VT", 1, 0, 0);
        prefix.Should().Be("-VT1000-");
    }

    [Fact]
    public void GeneratePrefix_HighVersionDigits_UsesAlphaEncoding()
    {
        // 10='A', 35='Z', 0='0', 0='0'
        var prefix = ClientFingerprint.GeneratePrefix("LT", 10, 35, 0, 0);
        prefix.Should().Be("-LTAZ00-");
    }

    [Fact]
    public void GeneratePrefix_StandardVersions_MatchesExpected()
    {
        // libtorrent 2.0.9.0 → "-LT2090-"
        ClientFingerprint.GeneratePrefix("LT", 2, 0, 9, 0).Should().Be("-LT2090-");
        // qBittorrent 4.5.2 → "-qB4520-"
        ClientFingerprint.GeneratePrefix("qB", 4, 5, 2, 0).Should().Be("-qB4520-");
    }

    [Theory]
    [InlineData("V", 1, 0, 0, 0)]       // Too short
    [InlineData("VTX", 1, 0, 0, 0)]     // Too long
    public void GeneratePrefix_InvalidClientId_Throws(string clientId, int maj, int min, int rev, int tag)
    {
        var act = () => ClientFingerprint.GeneratePrefix(clientId, maj, min, rev, tag);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(36)]
    public void GeneratePrefix_InvalidVersionComponent_Throws(int version)
    {
        var act = () => ClientFingerprint.GeneratePrefix("VT", version, 0, 0, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GeneratePeerId_ReturnsExactly20Characters()
    {
        var peerId = ClientFingerprint.GeneratePeerId("VT", 1, 0, 0);
        peerId.Should().HaveLength(20);
    }

    [Fact]
    public void GeneratePeerId_StartsWithCorrectPrefix()
    {
        var peerId = ClientFingerprint.GeneratePeerId("VT", 1, 0, 0);
        peerId.Should().StartWith("-VT1000-");
    }

    [Fact]
    public void GeneratePeerId_SuffixIsAlphanumeric()
    {
        var peerId = ClientFingerprint.GeneratePeerId("VT", 1, 0, 0);
        var suffix = peerId.Substring(8); // 12 chars after prefix
        suffix.Should().MatchRegex("^[A-Za-z0-9]{12}$");
    }

    [Fact]
    public void GeneratePeerId_IsUniqueEachCall()
    {
        var id1 = ClientFingerprint.GeneratePeerId("VT", 1, 0, 0);
        var id2 = ClientFingerprint.GeneratePeerId("VT", 1, 0, 0);
        id1.Should().NotBe(id2);
    }

    [Fact]
    public void GeneratePeerIdFromPrefix_AppendsAlphanumericSuffix()
    {
        var peerId = ClientFingerprint.GeneratePeerIdFromPrefix("-VT1000-");
        peerId.Should().HaveLength(20);
        peerId.Should().StartWith("-VT1000-");
        peerId.Substring(8).Should().MatchRegex("^[A-Za-z0-9]{12}$");
    }

    [Fact]
    public void GeneratePeerId_SurvivesAsciiEncoding()
    {
        var peerId = ClientFingerprint.GeneratePeerId("VT", 1, 0, 0);
        var bytes = Encoding.ASCII.GetBytes(peerId);
        var roundTripped = Encoding.ASCII.GetString(bytes);
        roundTripped.Should().Be(peerId, "peer ID must survive ASCII encoding (no bytes > 127)");
    }
}
