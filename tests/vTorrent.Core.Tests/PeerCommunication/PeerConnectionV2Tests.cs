using FluentAssertions;
using vTorrent.Core.PeerCommunication.Models;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication;

public class PeerConnectionV2Tests
{
    [Theory]
    [InlineData(0x10, true)]   // bit 4 set
    [InlineData(0x00, false)]  // no v2 support
    [InlineData(0x30, true)]   // bit 4 + bit 5 set (future extensions)
    [InlineData(0x01, false)]  // only bit 0 set (not v2)
    public void SupportsV2_FromReservedByte7(byte reservedByte7, bool expected)
    {
        var reserved = new byte[8];
        reserved[7] = reservedByte7;

        PeerConnectionV2Helpers.SupportsV2(reserved).Should().Be(expected);
    }

    [Fact]
    public void SetV2Support_SetsCorrectBit()
    {
        var reserved = new byte[8];
        PeerConnectionV2Helpers.SetV2Support(reserved);
        (reserved[7] & 0x10).Should().NotBe(0);
    }

    [Fact]
    public void SetV2Support_PreservesOtherBits()
    {
        var reserved = new byte[8];
        reserved[7] = 0x01;
        PeerConnectionV2Helpers.SetV2Support(reserved);
        reserved[7].Should().Be(0x11);
    }
}
