using System;
using System.Buffers.Binary;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Bencode.Objects;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Implements the ut_holepunch extension (BEP 55).
/// Allows peers behind NAT to establish direct connections via a relay/rendezvous peer.
/// Wire format: msg_type(1) + addr_type(1) + addr(4|16) + port(2) + err_code(4)
/// </summary>
public class HolepunchExtension : IExtension
{
    private readonly ILogger<HolepunchExtension> _logger;
    private readonly Action<IPeerConnection, HolepunchMessage> _onMessageReceived;
    private readonly Func<PeerMessage, Task> _sendMessageAsync;
    private readonly bool _isEnabled;

    public string Name => "ut_holepunch";
    public byte LocalExtensionId { get; } = 4;
    public byte? RemoteExtensionId { get; set; }
    public bool IsEnabled => _isEnabled;

    public HolepunchExtension(
        ILogger<HolepunchExtension> logger,
        Action<IPeerConnection, HolepunchMessage> onMessageReceived,
        Func<PeerMessage, Task> sendMessageAsync,
        bool isEnabled = true)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onMessageReceived = onMessageReceived ?? throw new ArgumentNullException(nameof(onMessageReceived));
        _sendMessageAsync = sendMessageAsync ?? throw new ArgumentNullException(nameof(sendMessageAsync));
        _isEnabled = isEnabled;
    }

    public Task OnExtensionHandshakeReceivedAsync(BDictionary handshake)
    {
        if (handshake.TryGetValue("m", out var mObj) && mObj is BDictionary mDict)
        {
            if (mDict.TryGetValue(Name, out var idObj) && idObj is BNumber idNum)
            {
                RemoteExtensionId = (byte)idNum.Value;
                _logger.LogDebug("Peer supports {Extension} with ID {Id}", Name, RemoteExtensionId);
            }
        }

        return Task.CompletedTask;
    }

    public void AddToHandshake(BDictionary handshake)
    {
        if (!IsEnabled)
            return;

        if (!handshake.TryGetValue("m", out var mObj) || mObj is not BDictionary mDict)
        {
            mDict = new BDictionary();
            handshake.Add("m", mDict);
        }

        mDict.AddNumber(Name, LocalExtensionId);
    }

    public Task<byte[]> GenerateMessageAsync(CancellationToken cancellationToken = default)
    {
        // Holepunch is event-driven, not periodic
        return Task.FromResult<byte[]>(null);
    }

    public Task OnMessageReceivedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var span = payload.Span;

        // Minimum length for IPv4 message: 1 + 1 + 4 + 2 + 4 = 12
        if (span.Length < 12)
        {
            _logger.LogDebug("ut_holepunch message too short ({Length} bytes), ignoring", span.Length);
            return Task.CompletedTask;
        }

        var msgType = (HolepunchMessageType)span[0];
        var addrType = (AddressType)span[1];

        IPAddress address;
        int portOffset;

        if (addrType == AddressType.IPv4)
        {
            if (span.Length < 12)
            {
                _logger.LogDebug("ut_holepunch IPv4 message too short ({Length} bytes), ignoring", span.Length);
                return Task.CompletedTask;
            }
            address = new IPAddress(span.Slice(2, 4));
            portOffset = 6;
        }
        else if (addrType == AddressType.IPv6)
        {
            if (span.Length < 24)
            {
                _logger.LogDebug("ut_holepunch IPv6 message too short ({Length} bytes), ignoring", span.Length);
                return Task.CompletedTask;
            }
            address = new IPAddress(span.Slice(2, 16));
            portOffset = 18;
        }
        else
        {
            _logger.LogDebug("ut_holepunch unknown addr_type {AddrType}, ignoring", addrType);
            return Task.CompletedTask;
        }

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(portOffset, 2));
        int errCodeRaw = BinaryPrimitives.ReadInt32BigEndian(span.Slice(portOffset + 2, 4));
        var errorCode = (HolepunchError)errCodeRaw;

        var endpoint = new IPEndPoint(address, port);
        var message = new HolepunchMessage(msgType, addrType, endpoint, errorCode);

        _logger.LogDebug("Received ut_holepunch {Type} from {Endpoint} (err={Error})", msgType, endpoint, errorCode);

        // Pass null for the peer — the caller sets it externally (event-driven pattern)
        _onMessageReceived(null, message);

        return Task.CompletedTask;
    }

    public Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task OnDisconnectingAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Public send methods — NOT part of IExtension. Called directly via stored reference.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a Rendezvous message asking the remote peer to relay holepunch to the target.
    /// Only sends if the peer advertised ut_holepunch in their handshake.
    /// </summary>
    public async Task SendRendezvousAsync(IPEndPoint target)
    {
        if (RemoteExtensionId == null)
            return;

        var payload = BuildPayload(HolepunchMessageType.Rendezvous, target, HolepunchError.None);
        await SendAsync(payload, HolepunchMessageType.Rendezvous, target).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a Connect message instructing the remote peer to initiate a direct connection to the target.
    /// Only sends if the peer advertised ut_holepunch in their handshake.
    /// </summary>
    public async Task SendConnectAsync(IPEndPoint target)
    {
        if (RemoteExtensionId == null)
            return;

        var payload = BuildPayload(HolepunchMessageType.Connect, target, HolepunchError.None);
        await SendAsync(payload, HolepunchMessageType.Connect, target).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an Error message in response to a failed Rendezvous or Connect.
    /// Only sends if the peer advertised ut_holepunch in their handshake.
    /// </summary>
    public async Task SendErrorAsync(IPEndPoint target, HolepunchError errorCode)
    {
        if (RemoteExtensionId == null)
            return;

        var payload = BuildPayload(HolepunchMessageType.Error, target, errorCode);
        await SendAsync(payload, HolepunchMessageType.Error, target).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static byte[] BuildPayload(HolepunchMessageType msgType, IPEndPoint target, HolepunchError errorCode)
    {
        bool isIPv6 = target.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var addrType = isIPv6 ? AddressType.IPv6 : AddressType.IPv4;
        var addrBytes = target.Address.GetAddressBytes(); // 4 or 16 bytes

        // Total: 1 (msg_type) + 1 (addr_type) + addrBytes.Length + 2 (port) + 4 (err_code)
        var payload = new byte[2 + addrBytes.Length + 2 + 4];
        int offset = 0;

        payload[offset++] = (byte)msgType;
        payload[offset++] = (byte)addrType;

        addrBytes.CopyTo(payload, offset);
        offset += addrBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), (ushort)target.Port);
        offset += 2;

        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset, 4), (int)errorCode);

        return payload;
    }

    private async Task SendAsync(byte[] payload, HolepunchMessageType msgType, IPEndPoint target)
    {
        var message = PeerMessage.CreateExtended(RemoteExtensionId!.Value, payload);

        try
        {
            await _sendMessageAsync(message).ConfigureAwait(false);
            _logger.LogDebug("Sent ut_holepunch {Type} for {Endpoint}", msgType, target);
        }
        catch (ObjectDisposedException)
        {
            // Peer disconnected, ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send ut_holepunch {Type} for {Endpoint}", msgType, target);
        }
    }
}
