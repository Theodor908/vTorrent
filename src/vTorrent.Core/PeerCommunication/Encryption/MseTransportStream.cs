using System;
using System.Buffers;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Core.Settings;
using vTorrent.Core.PeerCommunication.Encryption.Primitives;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Core.PeerCommunication.Encryption;

/// <summary>
/// ITransportStream decorator that transparently encrypts/decrypts via MSE/PE.
/// After MSE negotiation, all ReadAsync/WriteAsync calls pass through RC4 ciphers.
/// </summary>
public sealed class MseTransportStream : ITransportStream
{
    private readonly ITransportStream _inner;
    private readonly RC4? _outCipher;
    private readonly RC4? _inCipher;
    private byte[]? _bufferedPayload;
    private int _bufferOffset;

    public bool IsConnected => _inner.IsConnected;
    public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
    public TransportType TransportType => _inner.TransportType;

    /// <summary>True if MSE negotiation was performed.</summary>
    public bool IsEncrypted { get; }

    /// <summary>Negotiated encryption level.</summary>
    public EncryptionLevel NegotiatedLevel { get; }

    /// <summary>True if the BT handshake was sent as MSE IA data (initiator only).</summary>
    public bool InitialPayloadSent { get; }

    /// <summary>Responder-side info-hash resolved via req2 lookup; null for plaintext or outbound.</summary>
    public byte[]? IdentifiedInfoHash { get; }

    public MseTransportStream(ITransportStream inner, MseResult result)
    {
        _inner = inner;
        IsEncrypted = result.IsEncrypted;
        NegotiatedLevel = result.NegotiatedLevel;
        InitialPayloadSent = result.InitialPayloadSent;
        IdentifiedInfoHash = result.IdentifiedInfoHash;
        _outCipher = result.OutgoingCipher;
        _inCipher = result.IncomingCipher;
        _bufferedPayload = result.InitialPayload;
    }

    public static async Task<MseTransportStream> CreateOutboundAsync(
        ITransportStream inner, byte[] infoHash, byte[] peerId,
        IOptionsMonitor<EncryptionSettings> encryptionMonitor, ILogger<MseNegotiator> logger, CancellationToken ct)
    {
        var negotiator = new MseNegotiator(inner, encryptionMonitor, logger);
        var result = await negotiator.NegotiateOutboundAsync(infoHash, peerId, ct).ConfigureAwait(false);
        return new MseTransportStream(inner, result);
    }

    public static async Task<MseTransportStream> CreateInboundAsync(
        ITransportStream inner, Func<byte[], byte[]?> req2HashLookup,
        IOptionsMonitor<EncryptionSettings> encryptionMonitor, ILogger<MseNegotiator> logger, CancellationToken ct)
    {
        var negotiator = new MseNegotiator(inner, encryptionMonitor, logger);
        var result = await negotiator.NegotiateInboundAsync(req2HashLookup, ct).ConfigureAwait(false);
        return new MseTransportStream(inner, result);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        // 1. Drain buffered InitialPayload first
        if (_bufferedPayload is not null)
        {
            int remaining = _bufferedPayload.Length - _bufferOffset;
            int toCopy = Math.Min(buffer.Length, remaining);
            _bufferedPayload.AsSpan(_bufferOffset, toCopy).CopyTo(buffer.Span);
            _bufferOffset += toCopy;
            if (_bufferOffset >= _bufferedPayload.Length)
                _bufferedPayload = null;
            return toCopy;
        }

        // 2. Read from inner stream
        int bytesRead = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);

        // 3. Decrypt in-place if RC4 active
        if (bytesRead > 0 && _inCipher is not null)
            _inCipher.Process(buffer.Span[..bytesRead]);

        return bytesRead;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_outCipher is not null)
        {
            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.Span.CopyTo(rented);
                _outCipher.Process(rented.AsSpan(0, buffer.Length));
                await _inner.WriteAsync(rented.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        else
        {
            await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _outCipher?.Dispose();
        _inCipher?.Dispose();
        _inner.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _outCipher?.Dispose();
        _inCipher?.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
