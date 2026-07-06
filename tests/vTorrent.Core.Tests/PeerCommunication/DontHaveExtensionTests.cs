using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Bencode.Objects;
using vTorrent.Core.PeerCommunication.Extensions;
using vTorrent.Core.PeerCommunication.Models;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication;

public class DontHaveExtensionTests
{
    private const int TotalPieces = 100;

    private static DontHaveExtension CreateExtension(
        Action<int> onPeerLostPiece = null,
        Func<PeerMessage, Task> sendMessageAsync = null,
        int totalPieces = TotalPieces)
    {
        var logger = new Mock<ILogger<DontHaveExtension>>().Object;
        onPeerLostPiece ??= _ => { };
        sendMessageAsync ??= _ => Task.CompletedTask;
        return new DontHaveExtension(logger, onPeerLostPiece, sendMessageAsync, totalPieces);
    }

    // -------------------------------------------------------------------------
    // Test 1: Serialization — SendDontHaveAsync produces correct 4-byte BE payload
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendDontHaveAsync_WhenRemoteExtensionIdSet_SendsCorrectBigEndianPayload()
    {
        var sentMessages = new List<PeerMessage>();
        var ext = CreateExtension(sendMessageAsync: msg =>
        {
            sentMessages.Add(msg);
            return Task.CompletedTask;
        });

        ext.RemoteExtensionId = 5;
        await ext.SendDontHaveAsync(42);

        sentMessages.Should().HaveCount(1);
        var msg = sentMessages[0];
        msg.Type.Should().Be(MessageType.Extended);

        // Payload layout: [extensionId (1 byte)] [piece index (4 bytes BE)]
        msg.Payload.Should().HaveCount(5);
        msg.Payload[0].Should().Be(5); // remote extension ID

        int parsedPiece = BinaryPrimitives.ReadInt32BigEndian(msg.Payload.AsSpan(1, 4));
        parsedPiece.Should().Be(42);
    }

    // -------------------------------------------------------------------------
    // Test 2: Deserialization — OnMessageReceivedAsync parses payload and fires callback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnMessageReceivedAsync_ValidPayload_InvokesCallbackWithCorrectPieceIndex()
    {
        var receivedPieces = new List<int>();
        var ext = CreateExtension(onPeerLostPiece: idx => receivedPieces.Add(idx));

        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, 77);

        await ext.OnMessageReceivedAsync(payload.AsMemory());

        receivedPieces.Should().ContainSingle().Which.Should().Be(77);
    }

    // -------------------------------------------------------------------------
    // Test 3: Range validation — piece index >= totalPieces is ignored
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnMessageReceivedAsync_PieceIndexAtOrAboveTotalPieces_DoesNotInvokeCallback()
    {
        var receivedPieces = new List<int>();
        var ext = CreateExtension(onPeerLostPiece: idx => receivedPieces.Add(idx), totalPieces: TotalPieces);

        // Exactly at boundary (== totalPieces)
        var payloadAtBoundary = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payloadAtBoundary, TotalPieces);
        await ext.OnMessageReceivedAsync(payloadAtBoundary.AsMemory());

        // Way above boundary
        var payloadAbove = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payloadAbove, TotalPieces + 50);
        await ext.OnMessageReceivedAsync(payloadAbove.AsMemory());

        receivedPieces.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Test 4: Range validation negative — piece index < 0 is ignored
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnMessageReceivedAsync_NegativePieceIndex_DoesNotInvokeCallback()
    {
        var receivedPieces = new List<int>();
        var ext = CreateExtension(onPeerLostPiece: idx => receivedPieces.Add(idx));

        // Write -1 as a signed 32-bit big-endian integer
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, -1);

        await ext.OnMessageReceivedAsync(payload.AsMemory());

        receivedPieces.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Test 5: Short message — payloads shorter than 4 bytes are ignored
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnMessageReceivedAsync_PayloadShorterThan4Bytes_DoesNotInvokeCallback(int length)
    {
        var receivedPieces = new List<int>();
        var ext = CreateExtension(onPeerLostPiece: idx => receivedPieces.Add(idx));

        var payload = new byte[length];
        await ext.OnMessageReceivedAsync(payload.AsMemory());

        receivedPieces.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Test 6: Handshake — AddToHandshake adds lt_donthave: 3 under the "m" key
    // -------------------------------------------------------------------------

    [Fact]
    public void AddToHandshake_AddsLtDontHaveWithLocalExtensionId3UnderMKey()
    {
        var ext = CreateExtension();
        var handshake = new BDictionary();

        ext.AddToHandshake(handshake);

        handshake.TryGetValue("m", out var mObj).Should().BeTrue();
        mObj.Should().BeOfType<BDictionary>();
        var mDict = (BDictionary)mObj;

        mDict.TryGetValue("lt_donthave", out var idObj).Should().BeTrue();
        idObj.Should().BeOfType<BNumber>();
        ((BNumber)idObj).Value.Should().Be(3L);
    }

    [Fact]
    public void AddToHandshake_ExistingMDict_AddsToItWithoutReplacing()
    {
        var ext = CreateExtension();
        var handshake = new BDictionary();
        var existingMDict = new BDictionary();
        existingMDict.AddNumber("ut_pex", 1);
        handshake.Add("m", existingMDict);

        ext.AddToHandshake(handshake);

        var mDict = (BDictionary)handshake["m"];
        // Existing key preserved
        mDict.TryGetValue("ut_pex", out var pexObj).Should().BeTrue();
        ((BNumber)pexObj).Value.Should().Be(1L);
        // New key added
        mDict.TryGetValue("lt_donthave", out var dontHaveObj).Should().BeTrue();
        ((BNumber)dontHaveObj).Value.Should().Be(3L);
    }

    // -------------------------------------------------------------------------
    // Test 7: Handshake received — OnExtensionHandshakeReceivedAsync stores RemoteExtensionId
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnExtensionHandshakeReceivedAsync_PeerAdvertisesExtension_StoresRemoteExtensionId()
    {
        var ext = CreateExtension();
        ext.RemoteExtensionId.Should().BeNull();

        var mDict = new BDictionary();
        mDict.AddNumber("lt_donthave", 7);

        var handshake = new BDictionary();
        handshake.Add("m", mDict);

        await ext.OnExtensionHandshakeReceivedAsync(handshake);

        ext.RemoteExtensionId.Should().Be((byte)7);
    }

    [Fact]
    public async Task OnExtensionHandshakeReceivedAsync_PeerDoesNotAdvertiseExtension_RemoteExtensionIdRemainsNull()
    {
        var ext = CreateExtension();

        // "m" dict exists but does not contain "lt_donthave"
        var mDict = new BDictionary();
        mDict.AddNumber("ut_pex", 1);

        var handshake = new BDictionary();
        handshake.Add("m", mDict);

        await ext.OnExtensionHandshakeReceivedAsync(handshake);

        ext.RemoteExtensionId.Should().BeNull();
    }

    [Fact]
    public async Task OnExtensionHandshakeReceivedAsync_NoMKey_RemoteExtensionIdRemainsNull()
    {
        var ext = CreateExtension();
        var handshake = new BDictionary();

        await ext.OnExtensionHandshakeReceivedAsync(handshake);

        ext.RemoteExtensionId.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Test 8: Send gating — SendDontHaveAsync does NOT send when RemoteExtensionId is null
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendDontHaveAsync_RemoteExtensionIdNull_DoesNotSendAnyMessage()
    {
        var sentMessages = new List<PeerMessage>();
        var ext = CreateExtension(sendMessageAsync: msg =>
        {
            sentMessages.Add(msg);
            return Task.CompletedTask;
        });

        // RemoteExtensionId defaults to null
        ext.RemoteExtensionId.Should().BeNull();

        await ext.SendDontHaveAsync(10);

        sentMessages.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Test 9: Send gating positive — SendDontHaveAsync DOES send when RemoteExtensionId is set
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendDontHaveAsync_RemoteExtensionIdSet_SendsMessage()
    {
        var sentMessages = new List<PeerMessage>();
        var ext = CreateExtension(sendMessageAsync: msg =>
        {
            sentMessages.Add(msg);
            return Task.CompletedTask;
        });

        ext.RemoteExtensionId = 3;
        await ext.SendDontHaveAsync(0);

        sentMessages.Should().HaveCount(1);
    }

    // -------------------------------------------------------------------------
    // Test 10: GenerateMessageAsync always returns null
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GenerateMessageAsync_AlwaysReturnsNull()
    {
        var ext = CreateExtension();

        var result = await ext.GenerateMessageAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Test 11: IsEnabled always true
    // -------------------------------------------------------------------------

    [Fact]
    public void IsEnabled_IsAlwaysTrue()
    {
        var ext = CreateExtension();
        ext.IsEnabled.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Additional: Name and LocalExtensionId are correct
    // -------------------------------------------------------------------------

    [Fact]
    public void Name_IsLtDontHave()
    {
        var ext = CreateExtension();
        ext.Name.Should().Be("lt_donthave");
    }

    [Fact]
    public void LocalExtensionId_Is3()
    {
        var ext = CreateExtension();
        ext.LocalExtensionId.Should().Be(3);
    }
}
