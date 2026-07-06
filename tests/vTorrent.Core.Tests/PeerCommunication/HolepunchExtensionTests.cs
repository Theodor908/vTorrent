using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
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

public class HolepunchExtensionTests
{
    // -------------------------------------------------------------------------
    // Factory helpers
    // -------------------------------------------------------------------------

    private static HolepunchExtension CreateExtension(
        Action<IPeerConnection, HolepunchMessage> onMessageReceived = null,
        Func<PeerMessage, Task> sendMessageAsync = null,
        bool isEnabled = true)
    {
        var logger = new Mock<ILogger<HolepunchExtension>>().Object;
        onMessageReceived ??= (_, _) => { };
        sendMessageAsync ??= _ => Task.CompletedTask;
        return new HolepunchExtension(logger, onMessageReceived, sendMessageAsync, isEnabled);
    }

    /// <summary>
    /// Builds a raw holepunch payload byte array manually.
    /// Wire format: msg_type(1) + addr_type(1) + addr(4|16) + port(2) + err_code(4)
    /// </summary>
    private static byte[] BuildRawPayload(
        HolepunchMessageType msgType,
        AddressType addrType,
        IPAddress address,
        ushort port,
        HolepunchError error)
    {
        var addrBytes = address.GetAddressBytes();
        var buf = new byte[2 + addrBytes.Length + 2 + 4];
        int offset = 0;
        buf[offset++] = (byte)msgType;
        buf[offset++] = (byte)addrType;
        addrBytes.CopyTo(buf, offset);
        offset += addrBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), port);
        offset += 2;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(offset, 4), (int)error);
        return buf;
    }

    // -------------------------------------------------------------------------
    // Test 1: Serialize IPv4 Rendezvous — 12-byte payload, correct format
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendRendezvousAsync_IPv4Target_Sends12BytePayloadWithCorrectFormat()
    {
        var sentMessages = new List<PeerMessage>();
        var ext = CreateExtension(sendMessageAsync: msg =>
        {
            sentMessages.Add(msg);
            return Task.CompletedTask;
        });

        ext.RemoteExtensionId = 4;
        var target = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 6881);
        await ext.SendRendezvousAsync(target);

        sentMessages.Should().HaveCount(1);
        var msg = sentMessages[0];
        msg.Type.Should().Be(MessageType.Extended);

        // Payload = [extensionId(1)] + [holepunch data(12)]
        msg.Payload.Should().HaveCount(13);
        msg.Payload[0].Should().Be(4); // remote extension ID

        // Holepunch data starts at offset 1
        var data = msg.Payload.AsSpan(1);
        data[0].Should().Be((byte)HolepunchMessageType.Rendezvous);
        data[1].Should().Be((byte)AddressType.IPv4);

        var parsedAddress = new IPAddress(data.Slice(2, 4).ToArray());
        parsedAddress.Should().Be(IPAddress.Parse("192.168.1.1"));

        ushort parsedPort = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2));
        parsedPort.Should().Be(6881);

        int errCode = BinaryPrimitives.ReadInt32BigEndian(data.Slice(8, 4));
        errCode.Should().Be((int)HolepunchError.None);
    }

    // -------------------------------------------------------------------------
    // Test 2: Serialize IPv6 Connect — 24-byte payload
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendConnectAsync_IPv6Target_Sends24ByteHolepunchPayload()
    {
        var sentMessages = new List<PeerMessage>();
        var ext = CreateExtension(sendMessageAsync: msg =>
        {
            sentMessages.Add(msg);
            return Task.CompletedTask;
        });

        ext.RemoteExtensionId = 4;
        var target = new IPEndPoint(IPAddress.Parse("::1"), 12345);
        await ext.SendConnectAsync(target);

        sentMessages.Should().HaveCount(1);
        var msg = sentMessages[0];

        // Payload = [extensionId(1)] + [holepunch data(24)]
        msg.Payload.Should().HaveCount(25);

        var data = msg.Payload.AsSpan(1);
        data[0].Should().Be((byte)HolepunchMessageType.Connect);
        data[1].Should().Be((byte)AddressType.IPv6);

        var parsedAddress = new IPAddress(data.Slice(2, 16).ToArray());
        parsedAddress.Should().Be(IPAddress.Parse("::1"));

        ushort parsedPort = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(18, 2));
        parsedPort.Should().Be(12345);
    }

    // -------------------------------------------------------------------------
    // Test 3: Serialize Error — err_code field populated correctly
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendErrorAsync_WithErrorCode_PopulatesErrCodeFieldCorrectly()
    {
        var sentMessages = new List<PeerMessage>();
        var ext = CreateExtension(sendMessageAsync: msg =>
        {
            sentMessages.Add(msg);
            return Task.CompletedTask;
        });

        ext.RemoteExtensionId = 4;
        var target = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881);
        await ext.SendErrorAsync(target, HolepunchError.NoSuchPeer);

        sentMessages.Should().HaveCount(1);
        var data = sentMessages[0].Payload.AsSpan(1); // skip extension ID byte

        data[0].Should().Be((byte)HolepunchMessageType.Error);
        int errCode = BinaryPrimitives.ReadInt32BigEndian(data.Slice(8, 4));
        errCode.Should().Be((int)HolepunchError.NoSuchPeer);
    }

    // -------------------------------------------------------------------------
    // Test 4: Deserialize IPv4 — parses msg_type, addr, port, err_code
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnMessageReceivedAsync_ValidIPv4Payload_ParsesAllFieldsCorrectly()
    {
        HolepunchMessage received = null;
        var ext = CreateExtension(onMessageReceived: (_, msg) => received = msg);

        var payload = BuildRawPayload(
            HolepunchMessageType.Connect,
            AddressType.IPv4,
            IPAddress.Parse("192.168.1.1"),
            6881,
            HolepunchError.None);

        await ext.OnMessageReceivedAsync(payload.AsMemory());

        received.Should().NotBeNull();
        received.Type.Should().Be(HolepunchMessageType.Connect);
        received.AddrType.Should().Be(AddressType.IPv4);
        received.Endpoint.Address.Should().Be(IPAddress.Parse("192.168.1.1"));
        received.Endpoint.Port.Should().Be(6881);
        received.ErrorCode.Should().Be(HolepunchError.None);
    }

    // -------------------------------------------------------------------------
    // Test 5: Deserialize IPv6 — parses 16-byte address
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnMessageReceivedAsync_ValidIPv6Payload_ParsesAddressCorrectly()
    {
        HolepunchMessage received = null;
        var ext = CreateExtension(onMessageReceived: (_, msg) => received = msg);

        var payload = BuildRawPayload(
            HolepunchMessageType.Rendezvous,
            AddressType.IPv6,
            IPAddress.Parse("::1"),
            9999,
            HolepunchError.None);

        await ext.OnMessageReceivedAsync(payload.AsMemory());

        received.Should().NotBeNull();
        received.AddrType.Should().Be(AddressType.IPv6);
        received.Endpoint.Address.Should().Be(IPAddress.Parse("::1"));
        received.Endpoint.Port.Should().Be(9999);
    }

    // -------------------------------------------------------------------------
    // Test 6: Deserialize too short — ignores messages < 12 bytes
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(11)]
    public async Task OnMessageReceivedAsync_PayloadShorterThan12Bytes_DoesNotInvokeCallback(int length)
    {
        var callbackInvoked = false;
        var ext = CreateExtension(onMessageReceived: (_, _) => callbackInvoked = true);

        var payload = new byte[length];
        await ext.OnMessageReceivedAsync(payload.AsMemory());

        callbackInvoked.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Test 7: Deserialize IPv6 too short — ignores IPv6 messages < 24 bytes
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnMessageReceivedAsync_IPv6PayloadTooShort_DoesNotInvokeCallback()
    {
        var callbackInvoked = false;
        var ext = CreateExtension(onMessageReceived: (_, _) => callbackInvoked = true);

        // 12 bytes: valid for IPv4 header length but addr_type=IPv6 requires 24
        var payload = new byte[12];
        payload[0] = (byte)HolepunchMessageType.Connect;
        payload[1] = (byte)AddressType.IPv6; // triggers IPv6 path, but only 12 bytes
        await ext.OnMessageReceivedAsync(payload.AsMemory());

        callbackInvoked.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Test 8: Handshake — AddToHandshake adds ut_holepunch: 4 to m dict
    // -------------------------------------------------------------------------

    [Fact]
    public void AddToHandshake_WhenEnabled_AddsUtHolepunchWithLocalExtensionId4UnderMKey()
    {
        var ext = CreateExtension(isEnabled: true);
        var handshake = new BDictionary();

        ext.AddToHandshake(handshake);

        handshake.TryGetValue("m", out var mObj).Should().BeTrue();
        mObj.Should().BeOfType<BDictionary>();
        var mDict = (BDictionary)mObj;

        mDict.TryGetValue("ut_holepunch", out var idObj).Should().BeTrue();
        idObj.Should().BeOfType<BNumber>();
        ((BNumber)idObj).Value.Should().Be(4L);
    }

    [Fact]
    public void AddToHandshake_WhenDisabled_DoesNotAddToHandshake()
    {
        var ext = CreateExtension(isEnabled: false);
        var handshake = new BDictionary();

        ext.AddToHandshake(handshake);

        handshake.ContainsKey("m").Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Test 9: Handshake received — stores RemoteExtensionId from peer m dict
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnExtensionHandshakeReceivedAsync_PeerAdvertisesUtHolepunch_StoresRemoteExtensionId()
    {
        var ext = CreateExtension();
        ext.RemoteExtensionId.Should().BeNull();

        var mDict = new BDictionary();
        mDict.AddNumber("ut_holepunch", 7);
        var handshake = new BDictionary();
        handshake.Add("m", mDict);

        await ext.OnExtensionHandshakeReceivedAsync(handshake);

        ext.RemoteExtensionId.Should().Be((byte)7);
    }

    [Fact]
    public async Task OnExtensionHandshakeReceivedAsync_PeerDoesNotAdvertise_RemoteExtensionIdRemainsNull()
    {
        var ext = CreateExtension();

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
    // Test 10: Send gating — send methods do nothing when RemoteExtensionId is null
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendRendezvousAsync_RemoteExtensionIdNull_DoesNotSendAnyMessage()
    {
        var sentMessages = new List<PeerMessage>();
        var ext = CreateExtension(sendMessageAsync: msg =>
        {
            sentMessages.Add(msg);
            return Task.CompletedTask;
        });

        ext.RemoteExtensionId.Should().BeNull();

        await ext.SendRendezvousAsync(new IPEndPoint(IPAddress.Loopback, 6881));

        sentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task SendConnectAsync_RemoteExtensionIdNull_DoesNotSendAnyMessage()
    {
        var sentMessages = new List<PeerMessage>();
        var ext = CreateExtension(sendMessageAsync: msg =>
        {
            sentMessages.Add(msg);
            return Task.CompletedTask;
        });

        await ext.SendConnectAsync(new IPEndPoint(IPAddress.Loopback, 6881));

        sentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task SendErrorAsync_RemoteExtensionIdNull_DoesNotSendAnyMessage()
    {
        var sentMessages = new List<PeerMessage>();
        var ext = CreateExtension(sendMessageAsync: msg =>
        {
            sentMessages.Add(msg);
            return Task.CompletedTask;
        });

        await ext.SendErrorAsync(new IPEndPoint(IPAddress.Loopback, 6881), HolepunchError.NoSuchPeer);

        sentMessages.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Test 11: IsEnabled — reflects constructor parameter
    // -------------------------------------------------------------------------

    [Fact]
    public void IsEnabled_WhenConstructedWithTrue_ReturnsTrue()
    {
        var ext = CreateExtension(isEnabled: true);
        ext.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_WhenConstructedWithFalse_ReturnsFalse()
    {
        var ext = CreateExtension(isEnabled: false);
        ext.IsEnabled.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Test 12: GenerateMessageAsync returns null (event-driven, no periodic generation)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GenerateMessageAsync_AlwaysReturnsNull()
    {
        var ext = CreateExtension();

        var result = await ext.GenerateMessageAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Additional: Name and LocalExtensionId are correct
    // -------------------------------------------------------------------------

    [Fact]
    public void Name_IsUtHolepunch()
    {
        var ext = CreateExtension();
        ext.Name.Should().Be("ut_holepunch");
    }

    [Fact]
    public void LocalExtensionId_Is4()
    {
        var ext = CreateExtension();
        ext.LocalExtensionId.Should().Be(4);
    }
}
