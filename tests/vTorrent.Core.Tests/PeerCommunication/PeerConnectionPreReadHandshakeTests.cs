using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Tests.PeerCommunication.Support;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication;

public class PeerConnectionPreReadHandshakeTests
{
    [Fact]
    public async Task ConnectAsync_WithMatchingPreReadHandshake_DoesNotReadHandshakeFromWire()
    {
        var infoHash = new byte[20]; for (int i = 0; i < 20; i++) infoHash[i] = (byte)i;
        var peerId = new byte[20]; for (int i = 0; i < 20; i++) peerId[i] = (byte)(0xF0 + (i & 0x0F));
        var preRead = new Handshake(infoHash, peerId).ToBytes(); // 68 bytes

        var transport = new ScriptedTransportStream(readScript: Array.Empty<byte>());
        var conn = TestPeerConnectionFactory.CreateIncoming(transport);

        var connect = conn.ConnectAsync(infoHash, CancellationToken.None, preReadHandshake: preRead);
        await connect.WaitAsync(TimeSpan.FromSeconds(2)); // must not hang on a wire read
        transport.LastWriteWasHandshake().Should().BeTrue();
    }

    [Fact]
    public async Task ConnectAsync_WithMismatchedPreReadHandshake_Rejects()
    {
        var ourHash = new byte[20]; for (int i = 0; i < 20; i++) ourHash[i] = (byte)i;
        var otherHash = new byte[20]; for (int i = 0; i < 20; i++) otherHash[i] = (byte)(i + 1);
        var peerId = new byte[20];
        var preRead = new Handshake(otherHash, peerId).ToBytes();

        var transport = new ScriptedTransportStream(readScript: Array.Empty<byte>());
        var conn = TestPeerConnectionFactory.CreateIncoming(transport);

        Func<Task> act = async () => await conn.ConnectAsync(ourHash, CancellationToken.None, preReadHandshake: preRead);
        await act.Should().ThrowAsync<Exception>();
    }
}
