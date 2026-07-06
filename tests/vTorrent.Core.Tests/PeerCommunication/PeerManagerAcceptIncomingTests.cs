using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Tests.PeerCommunication.Support;
using vTorrent.Core.Tests.PeerCommunication.Transport;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication;

public class PeerManagerAcceptIncomingTests
{
    [Fact]
    public async Task AcceptIncomingPeerAsync_AtMaxConnections_DisposesStreamAndDoesNotAttach()
    {
        // Build a PeerManager with MaxConnections = 0 so any incoming peer is rejected.
        var pm = PeerManagerTestFactory.Create(maxConnections: 0);
        var stream = new ScriptedTransportStream(readScript: System.Array.Empty<byte>());

        await pm.AcceptIncomingPeerAsync(stream, new IPEndPoint(IPAddress.Loopback, 5000),
            isEncrypted: false, preReadHandshake: null, CancellationToken.None);

        stream.Disposed.Should().BeTrue();
        pm.ConnectedPeerCount.Should().Be(0);
    }

    /// <summary>
    /// Regression test for the dead ConnectionSettings/ConnectionMonitor wiring: with
    /// AllowMultipleConnectionsPerIp = false (via the live monitor, not just the snapshot),
    /// an incoming peer from an IP that already has a connected peer must be rejected as a
    /// duplicate IP — the guard must actually read _connectionMonitor now that it is threaded
    /// through from the engine.
    /// </summary>
    [Fact]
    public async Task AcceptIncomingPeerAsync_DuplicateIp_MonitorDisallows_RejectsAndDisposesStream()
    {
        var registry = new PeerRegistry();
        var connectionMonitor = new StaticOptionsMonitor<ConnectionSettings>(
            new ConnectionSettings { AllowMultipleConnectionsPerIp = false });
        var pm = PeerManagerTestFactory.Create(maxConnections: 50, registry, connectionMonitor);

        // Pre-register an already-connected peer at 127.0.0.1 (different port) so the
        // duplicate-IP guard has something to match against.
        RegisterConnectedPeer(registry, new IPEndPoint(IPAddress.Loopback, 6881));

        var incomingStream = new ScriptedTransportStream(readScript: Array.Empty<byte>());

        await pm.AcceptIncomingPeerAsync(
            incomingStream,
            new IPEndPoint(IPAddress.Loopback, 7000),
            isEncrypted: false,
            preReadHandshake: null,
            CancellationToken.None);

        // Rejected synchronously by the duplicate-IP guard, before any handshake I/O was
        // attempted — proven by the stream never having been read from (Writes is empty and
        // the method returned without needing the CancellationToken to fire).
        incomingStream.Disposed.Should().BeTrue("a second peer from the same IP must be rejected " +
            "as a duplicate when the live ConnectionSettings monitor reports AllowMultipleConnectionsPerIp=false");
        incomingStream.Writes.Should().BeEmpty("rejection happens before any handshake bytes are written");
    }

    /// <summary>
    /// Mirror of the test above with AllowMultipleConnectionsPerIp = true: the duplicate-IP
    /// guard must NOT reject the second peer from the same IP. Because AcceptIncomingPeerAsync
    /// then proceeds into the real handshake path (which blocks on the scripted stream's empty
    /// read script), this test can only observe "did the synchronous duplicate-IP gate let the
    /// call through" — it cannot deterministically assert the peer fully attaches without a
    /// real handshake partner. We prove passage past the gate by observing the call does NOT
    /// return synchronously with the stream disposed (unlike the maxConnections=0 test and the
    /// AllowMultipleConnectionsPerIp=false test above, which both dispose the stream before
    /// yielding); instead it is still in-flight, blocked on the handshake read, until we cancel.
    /// </summary>
    [Fact]
    public async Task AcceptIncomingPeerAsync_DuplicateIp_MonitorAllows_DoesNotRejectSynchronously()
    {
        var registry = new PeerRegistry();
        var connectionMonitor = new StaticOptionsMonitor<ConnectionSettings>(
            new ConnectionSettings { AllowMultipleConnectionsPerIp = true });
        var pm = PeerManagerTestFactory.Create(maxConnections: 50, registry, connectionMonitor);

        RegisterConnectedPeer(registry, new IPEndPoint(IPAddress.Loopback, 6881));

        var incomingStream = new ScriptedTransportStream(readScript: Array.Empty<byte>());
        using var cts = new CancellationTokenSource();

        var acceptTask = pm.AcceptIncomingPeerAsync(
            incomingStream,
            new IPEndPoint(IPAddress.Loopback, 7000),
            isEncrypted: false,
            preReadHandshake: null,
            cts.Token);

        // Give the synchronous portion of AcceptIncomingPeerAsync (max-conn check, duplicate-IP
        // check, PeerConnection construction) a chance to run. It has no awaits that complete
        // synchronously before the blocking handshake read, so if the duplicate-IP guard had
        // rejected it, the stream would already be disposed and the task already completed by
        // the time this delay elapses.
        await Task.Delay(200);

        acceptTask.IsCompleted.Should().BeFalse(
            "with AllowMultipleConnectionsPerIp=true the duplicate-IP guard must not short-circuit " +
            "the call — it should still be blocked in the (unrelated) handshake read");
        incomingStream.Disposed.Should().BeFalse(
            "the stream must not have been disposed by the duplicate-IP guard when duplicates are allowed");

        // Unblock the handshake read so the task can complete (it will fail the handshake since
        // no real peer is on the other end — that failure is expected and irrelevant to this test).
        cts.Cancel();
        await acceptTask;
    }

    private static void RegisterConnectedPeer(PeerRegistry registry, IPEndPoint endpoint)
    {
        var peerInfo = PeerInfo.Incoming(endpoint);
        registry.GetOrRegister(peerInfo);
        var key = PeerRegistry.GetPeerKey(peerInfo);

        var fakeConnection = new PeerConnection(
            peerInfo,
            new PeerSettings(),
            new ScriptedTransportStream(readScript: Array.Empty<byte>()),
            NullLoggerFactory.Instance.CreateLogger<PeerConnection>());

        registry.UpdateConnection(key, fakeConnection, PeerConnectionStatus.Connected);
    }
}
