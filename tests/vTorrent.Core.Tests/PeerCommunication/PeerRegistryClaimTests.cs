using System.Net;
using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.PeerCommunication;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication;

public class PeerRegistryClaimTests
{
    private static PeerInfo MakePeer(int port) =>
        new(IPAddress.Loopback, port, null, "test");

    [Fact]
    public void TryBeginConnecting_FirstClaim_TransitionsToConnecting()
    {
        var registry = new PeerRegistry();
        var info = MakePeer(6881);
        var state = registry.GetOrRegister(info)!;
        var key = PeerRegistry.GetPeerKey(info);

        registry.TryBeginConnecting(key).Should().BeTrue();
        state.Status.Should().Be(PeerConnectionStatus.Connecting);
    }

    [Fact]
    public void TryBeginConnecting_WhileConnecting_SecondClaimLoses()
    {
        // Regression: two concurrent peer-add paths (DHT/PEX event vs. the
        // connect-boost drain loop) could both dial the same endpoint because
        // AddPeerAsync's status check and status write were not atomic.
        var registry = new PeerRegistry();
        var info = MakePeer(6882);
        registry.GetOrRegister(info);
        var key = PeerRegistry.GetPeerKey(info);

        registry.TryBeginConnecting(key).Should().BeTrue();
        registry.TryBeginConnecting(key).Should().BeFalse();
    }

    [Fact]
    public void TryBeginConnecting_WhileConnected_Loses()
    {
        var registry = new PeerRegistry();
        var info = MakePeer(6883);
        registry.GetOrRegister(info);
        var key = PeerRegistry.GetPeerKey(info);
        registry.UpdateConnection(key, null, PeerConnectionStatus.Connected);

        registry.TryBeginConnecting(key).Should().BeFalse();
    }

    [Fact]
    public void TryBeginConnecting_AfterDisconnect_ClaimableAgain()
    {
        var registry = new PeerRegistry();
        var info = MakePeer(6884);
        registry.GetOrRegister(info);
        var key = PeerRegistry.GetPeerKey(info);

        registry.TryBeginConnecting(key).Should().BeTrue();
        registry.UpdateConnection(key, null, PeerConnectionStatus.Disconnected);
        registry.TryBeginConnecting(key).Should().BeTrue();
    }

    [Fact]
    public void TryBeginConnecting_UnknownKey_ReturnsFalse()
    {
        new PeerRegistry().TryBeginConnecting("203.0.113.1:9").Should().BeFalse();
    }
}
