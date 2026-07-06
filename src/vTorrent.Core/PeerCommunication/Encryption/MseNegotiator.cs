using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Core.Settings;
using vTorrent.Core.PeerCommunication.Encryption.Primitives;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Core.PeerCommunication.Encryption;

/// <summary>
/// MSE/PE 4-packet negotiation state machine.
/// Handles both initiator (outbound) and responder (inbound) roles.
/// </summary>
public sealed class MseNegotiator
{
    private const int DhKeyLength = 96;
    private const int MaxPaddingLength = 512;
    private const int SyncHashLength = 20; // SHA1
    private const int VcLength = 8;
    private static readonly byte[] Vc = new byte[VcLength]; // 8 zero bytes

    private readonly ITransportStream _stream;
    private readonly IOptionsMonitor<EncryptionSettings> _encryptionMonitor;
    private readonly ILogger<MseNegotiator> _logger;

    public MseNegotiator(ITransportStream stream, IOptionsMonitor<EncryptionSettings> encryptionMonitor, ILogger<MseNegotiator> logger)
    {
        _stream = stream;
        _encryptionMonitor = encryptionMonitor;
        _logger = logger;
    }

    // --- Public entry points ---

    public async Task<MseResult> NegotiateOutboundAsync(byte[] infoHash, byte[] peerId, CancellationToken ct)
    {
        _logger.LogDebug("Starting outbound MSE negotiation");

        var dh = new DiffieHellman();

        // Step 1-2: Send Ya + padding
        await SendPublicKeyWithPaddingAsync(dh.PublicKey, ct).ConfigureAwait(false);

        // Step 3: Receive Yb (96 bytes only — padding consumed via VC scan below)
        var remoteKey = await ReceivePublicKeyAsync(ct).ConfigureAwait(false);

        // Step 4: Compute shared secret
        var S = dh.ComputeSharedSecret(remoteKey);
        _logger.LogDebug("DH shared secret computed");

        // Step 5: Derive RC4 streams
        var syncHash = MseKeyDerivation.Hash("req1", S);
        var skeyHash = ComputeSkeyXor(infoHash, S);

        // Create initiator ciphers (keyA = outgoing, keyB = incoming)
        var (outCipher, inCipher) = MseKeyDerivation.CreateRC4Pair(S, infoHash);

        // Step 6: Send encrypted: syncHash + skeyHash + VC + crypto_provide + pad + IA
        await SendCryptoProvideAsync(outCipher, syncHash, skeyHash, infoHash, peerId, ct).ConfigureAwait(false);

        // Step 7: Receive encrypted response — scan for encrypted VC through padding
        var selectedLevel = await ScanAndReceiveCryptoSelectAsync(inCipher, ct).ConfigureAwait(false);
        _logger.LogDebug("MSE outbound negotiated: {Level}", selectedLevel);

        // Capture any bytes the bulk scan over-read past padD.
        // These are the start of the responder's encrypted BT handshake.
        // Without this, MseTransportStream reads from _inner which has already
        // advanced past these bytes → stream misalignment → RC4 decrypts garbage
        // → every piece fails hash verification → cascading peer bans.
        var excess = GetAndClearScanExcess();

        return new MseResult
        {
            IsEncrypted = true,
            NegotiatedLevel = selectedLevel,
            OutgoingCipher = selectedLevel == EncryptionLevel.RC4 ? outCipher : null,
            IncomingCipher = selectedLevel == EncryptionLevel.RC4 ? inCipher : null,
            InitialPayloadSent = true, // BT handshake was sent as IA
            InitialPayload = excess    // Over-read bytes from bulk scan
        };
    }

    public async Task<MseResult> NegotiateInboundAsync(
        Func<byte[], byte[]?> req2HashLookup, CancellationToken ct)
    {
        _logger.LogDebug("Starting inbound MSE negotiation");

        // Peek first byte to detect plaintext vs MSE
        var firstByte = new byte[1];
        await ReadExactAsync(firstByte, ct).ConfigureAwait(false);

        if (firstByte[0] == 0x13) // BitTorrent protocol header
        {
            if (_encryptionMonitor.CurrentValue.InPolicy == EncryptionPolicy.Forced)
                throw new MseNegotiationException("Plaintext handshake rejected (InPolicy=Forced)");

            _logger.LogDebug("Plaintext handshake detected, passing through");
            return new MseResult
            {
                IsEncrypted = false,
                NegotiatedLevel = EncryptionLevel.Plaintext,
                InitialPayload = firstByte // Buffer the peeked byte
            };
        }

        if (_encryptionMonitor.CurrentValue.InPolicy == EncryptionPolicy.Disabled)
            throw new MseNegotiationException("MSE handshake rejected (InPolicy=Disabled)");

        // Read remaining 95 bytes of Ya (first byte already read)
        var yaBytes = new byte[DhKeyLength];
        yaBytes[0] = firstByte[0];
        await ReadExactAsync(yaBytes.AsMemory(1, DhKeyLength - 1), ct).ConfigureAwait(false);

        // Generate our DH key and send Yb + padding
        var dh = new DiffieHellman();
        await SendPublicKeyWithPaddingAsync(dh.PublicKey, ct).ConfigureAwait(false);

        // Compute shared secret
        var S = dh.ComputeSharedSecret(yaBytes);
        _logger.LogDebug("DH shared secret computed (inbound)");

        // Scan for sync hash HASH('req1' + S) — up to 532 bytes
        var syncHash = MseKeyDerivation.Hash("req1", S);
        await ScanForSyncHashAsync(syncHash, ct).ConfigureAwait(false);

        // Read HASH('req2'+SKEY) XOR HASH('req3'+S)
        var xorHash = new byte[SyncHashLength];
        await ReadExactAsync(xorHash, ct).ConfigureAwait(false);

        // Extract HASH('req2'+SKEY) by XOR-ing with HASH('req3'+S)
        var req3Hash = MseKeyDerivation.Hash("req3", S);
        for (int i = 0; i < SyncHashLength; i++)
            xorHash[i] ^= req3Hash[i];

        // Identify torrent via req2 hash lookup
        var identifiedInfoHash = req2HashLookup(xorHash)
            ?? throw new MseNegotiationException("Unknown torrent — req2 hash not recognized");

        _logger.LogDebug("Identified torrent via req2 hash");

        // Create responder ciphers — REVERSED from initiator:
        // CreateRC4Pair returns (keyA, keyB). For initiator, keyA=out, keyB=in.
        // For responder, keyA=in (we read what initiator wrote), keyB=out.
        var (inCipher, outCipher) = MseKeyDerivation.CreateRC4Pair(S, identifiedInfoHash);

        // Read encrypted: VC + crypto_provide + padC + IA
        var (cryptoProvide, initialPayload) = await ReceiveCryptoProvideAsync(inCipher, ct).ConfigureAwait(false);

        // Select encryption level
        var selectedLevel = SelectEncryptionLevel(cryptoProvide);

        // Send encrypted: VC + crypto_select + padD
        await SendCryptoSelectAsync(outCipher, selectedLevel, ct).ConfigureAwait(false);

        _logger.LogDebug("MSE inbound negotiated: {Level}", selectedLevel);

        // Capture any remaining bytes the bulk scan over-read past the IA.
        // These are post-handshake protocol messages from the initiator.
        var excess = GetAndClearScanExcess();
        byte[]? combinedPayload = initialPayload;
        if (excess != null)
        {
            if (initialPayload != null)
            {
                combinedPayload = new byte[initialPayload.Length + excess.Length];
                Buffer.BlockCopy(initialPayload, 0, combinedPayload, 0, initialPayload.Length);
                Buffer.BlockCopy(excess, 0, combinedPayload, initialPayload.Length, excess.Length);
            }
            else
            {
                combinedPayload = excess;
            }
        }

        return new MseResult
        {
            IsEncrypted = true,
            NegotiatedLevel = selectedLevel,
            OutgoingCipher = selectedLevel == EncryptionLevel.RC4 ? outCipher : null,
            IncomingCipher = selectedLevel == EncryptionLevel.RC4 ? inCipher : null,
            InitialPayload = combinedPayload,
            IdentifiedInfoHash = identifiedInfoHash
        };
    }

    // --- Private helpers ---

    private async Task SendPublicKeyWithPaddingAsync(byte[] publicKey, CancellationToken ct)
    {
        var padLen = RandomNumberGenerator.GetInt32(MaxPaddingLength + 1);
        var buffer = new byte[DhKeyLength + padLen];
        publicKey.CopyTo(buffer, 0);
        if (padLen > 0)
            RandomNumberGenerator.Fill(buffer.AsSpan(DhKeyLength));
        await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReceivePublicKeyAsync(CancellationToken ct)
    {
        var key = new byte[DhKeyLength];
        await ReadExactAsync(key, ct).ConfigureAwait(false);
        return key;
    }

    // Excess bytes from bulk scanning that need to be consumed before stream reads
    private byte[]? _scanExcess;
    private int _scanExcessOffset;

    private async Task ScanForSyncHashAsync(byte[] syncHash, CancellationToken ct)
    {
        // Defensive reset — prevent stale excess from a prior scan method
        _scanExcess = null;
        _scanExcessOffset = 0;

        // Scan up to 532 bytes (512 padding + 20 hash) for the sync pattern.
        // Read in bulk chunks instead of byte-at-a-time to reduce syscalls.
        // libtorrent reads into a large buffer then scans in-memory.
        const int maxScan = MaxPaddingLength + SyncHashLength;
        const int chunkSize = 128;
        var scanBuffer = new byte[maxScan];
        int filled = 0;

        while (filled < maxScan)
        {
            int toRead = Math.Min(chunkSize, maxScan - filled);
            int bytesRead = await _stream.ReadAsync(
                scanBuffer.AsMemory(filled, toRead), ct).ConfigureAwait(false);
            if (bytesRead == 0)
                throw new MseNegotiationException("Connection closed during sync hash scan");
            filled += bytesRead;

            // Check for sync hash in the newly available data
            int checkStart = Math.Max(0, filled - bytesRead - SyncHashLength + 1);
            for (int pos = checkStart; pos <= filled - SyncHashLength; pos++)
            {
                bool match = true;
                for (int i = 0; i < SyncHashLength; i++)
                {
                    if (scanBuffer[pos + i] != syncHash[i])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    _logger.LogDebug("Sync hash found at offset {Offset}", pos);

                    // Buffer any excess past the sync hash for later ReadExactAsync calls
                    int excessStart = pos + SyncHashLength;
                    int excessLen = filled - excessStart;
                    if (excessLen > 0)
                    {
                        _scanExcess = new byte[excessLen];
                        Buffer.BlockCopy(scanBuffer, excessStart, _scanExcess, 0, excessLen);
                        _scanExcessOffset = 0;
                    }
                    return;
                }
            }
        }

        throw new MseNegotiationException("Sync hash not found within scan window");
    }

    private EncryptionLevel SelectEncryptionLevel(uint cryptoProvide)
    {
        var allowed = (uint)_encryptionMonitor.CurrentValue.AllowedLevel;
        var common = cryptoProvide & allowed;

        if (common == 0)
            throw new MseNegotiationException(
                $"No common encryption level (provide=0x{cryptoProvide:X}, allowed=0x{allowed:X})");

        // If both plaintext and RC4 are mutually supported, prefer RC4 — the operator enabled
        // encryption, so the encrypted level should win (matches libtorrent/uTorrent, whose
        // crypto_select picks the strongest common level). Returning Plaintext here silently
        // defeated encryption whenever AllowedLevel=Both on both peers.
        if (common == (uint)EncryptionLevel.Both)
            return EncryptionLevel.RC4;

        return (EncryptionLevel)common;
    }

    private async Task SendCryptoProvideAsync(
        RC4 cipher, byte[] syncHash, byte[] skeyXor, byte[] infoHash, byte[] peerId, CancellationToken ct)
    {
        // Build: syncHash(20) + skeyXor(20) + encrypted[VC(8) + crypto_provide(4) + padC_len(2) + padC(0) + IA_len(2) + IA(68)]
        var padCLen = (ushort)0;
        var ia = BuildBitTorrentHandshake(infoHash, peerId);
        var iaLen = (ushort)ia.Length;

        // Unencrypted prefix
        var prefix = new byte[syncHash.Length + skeyXor.Length];
        syncHash.CopyTo(prefix, 0);
        skeyXor.CopyTo(prefix, syncHash.Length);
        await _stream.WriteAsync(prefix, ct).ConfigureAwait(false);

        // Encrypted payload
        var payload = new byte[VcLength + 4 + 2 + padCLen + 2 + iaLen];
        int offset = 0;

        Vc.CopyTo(payload, offset); offset += VcLength;
        WriteBigEndianUInt32(payload, offset, (uint)_encryptionMonitor.CurrentValue.AllowedLevel); offset += 4;
        WriteBigEndianUInt16(payload, offset, padCLen); offset += 2;
        // padC: 0 bytes
        WriteBigEndianUInt16(payload, offset, iaLen); offset += 2;
        ia.CopyTo(payload, offset);

        cipher.Process(payload);
        await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task<(uint cryptoProvide, byte[]? initialPayload)> ReceiveCryptoProvideAsync(
        RC4 cipher, CancellationToken ct)
    {
        // Read encrypted: VC(8) + crypto_provide(4) + padC_len(2)
        var header = new byte[VcLength + 4 + 2];
        await ReadExactAsync(header, ct).ConfigureAwait(false);
        cipher.Process(header);

        // Validate VC (should be 8 zero bytes after decryption)
        for (int i = 0; i < VcLength; i++)
        {
            if (header[i] != 0)
                throw new MseNegotiationException("VC validation failed");
        }

        uint cryptoProvide = ReadBigEndianUInt32(header, VcLength);
        ushort padCLen = ReadBigEndianUInt16(header, VcLength + 4);

        if (padCLen > MaxPaddingLength)
            throw new MseNegotiationException($"PadC length {padCLen} exceeds maximum {MaxPaddingLength}");

        // Read and discard padC
        if (padCLen > 0)
        {
            var padC = new byte[padCLen];
            await ReadExactAsync(padC, ct).ConfigureAwait(false);
            cipher.Process(padC);
        }

        // Read IA_len(2) + IA
        var iaLenBuf = new byte[2];
        await ReadExactAsync(iaLenBuf, ct).ConfigureAwait(false);
        cipher.Process(iaLenBuf);
        ushort iaLen = ReadBigEndianUInt16(iaLenBuf, 0);

        byte[]? ia = null;
        if (iaLen > 0)
        {
            ia = new byte[iaLen];
            await ReadExactAsync(ia, ct).ConfigureAwait(false);
            cipher.Process(ia);
        }

        return (cryptoProvide, ia);
    }

    private async Task SendCryptoSelectAsync(RC4 cipher, EncryptionLevel level, CancellationToken ct)
    {
        // Encrypted: VC(8) + crypto_select(4) + padD_len(2) + padD(0)
        var payload = new byte[VcLength + 4 + 2];
        Vc.CopyTo(payload, 0);
        WriteBigEndianUInt32(payload, VcLength, (uint)level);
        WriteBigEndianUInt16(payload, VcLength + 4, 0); // no padD

        cipher.Process(payload);
        await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Initiator side: scan for the responder's encrypted VC after Yb + padding.
    /// PadB is plaintext (NOT encrypted), so we cannot decrypt-and-check.
    /// Instead: pre-compute encrypted VC bytes, scan raw stream for the pattern.
    /// After the cipher processes the 8 VC zeros, it's aligned for crypto_select.
    /// </summary>
    private async Task<EncryptionLevel> ScanAndReceiveCryptoSelectAsync(RC4 cipher, CancellationToken ct)
    {
        // Defensive reset — prevent stale excess from a prior scan method
        _scanExcess = null;
        _scanExcessOffset = 0;

        // Pre-compute what the encrypted VC looks like.
        // VC = 8 zeros, encrypted by the responder's outCipher (= our inCipher).
        // Processing 8 zeros advances cipher by 8, so it's ready for crypto_select next.
        var expectedEncVc = new byte[VcLength];
        cipher.Process(expectedEncVc); // expectedEncVc now holds ENCRYPT(00000000)

        // Scan raw stream for expectedEncVc pattern using bulk reads.
        const int maxScan = MaxPaddingLength + VcLength;
        const int chunkSize = 128;
        var scanBuffer = new byte[maxScan];
        int filled = 0;

        while (filled < maxScan)
        {
            int toRead = Math.Min(chunkSize, maxScan - filled);
            int bytesRead = await _stream.ReadAsync(
                scanBuffer.AsMemory(filled, toRead), ct).ConfigureAwait(false);
            if (bytesRead == 0)
                throw new MseNegotiationException("Connection closed during VC scan");
            filled += bytesRead;

            int checkStart = Math.Max(0, filled - bytesRead - VcLength + 1);
            for (int pos = checkStart; pos <= filled - VcLength; pos++)
            {
                bool match = true;
                for (int i = 0; i < VcLength; i++)
                {
                    if (scanBuffer[pos + i] != expectedEncVc[i])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    _logger.LogDebug("Initiator found encrypted VC at offset {Offset}", pos);

                    // Buffer any excess past the VC match
                    int excessStart = pos + VcLength;
                    int excessLen = filled - excessStart;
                    if (excessLen > 0)
                    {
                        _scanExcess = new byte[excessLen];
                        Buffer.BlockCopy(scanBuffer, excessStart, _scanExcess, 0, excessLen);
                        _scanExcessOffset = 0;
                    }

                    // VC matched — cipher already advanced past VC (from pre-computation).
                    // Read crypto_select(4) + padD_len(2)
                    var tail = new byte[4 + 2];
                    await ReadExactAsync(tail, ct).ConfigureAwait(false);
                    cipher.Process(tail);

                    uint cryptoSelect = ReadBigEndianUInt32(tail, 0);
                    ushort padDLen = ReadBigEndianUInt16(tail, 4);

                    if (padDLen > 0)
                    {
                        var padD = new byte[padDLen];
                        await ReadExactAsync(padD, ct).ConfigureAwait(false);
                        cipher.Process(padD);
                    }

                    return (EncryptionLevel)cryptoSelect;
                }
            }
        }

        throw new MseNegotiationException("Encrypted VC not found within scan window (initiator)");
    }

    private static byte[] ComputeSkeyXor(byte[] infoHash, byte[] S)
    {
        var req2 = MseKeyDerivation.Hash("req2", infoHash);
        var req3 = MseKeyDerivation.Hash("req3", S);
        var result = new byte[SyncHashLength];
        for (int i = 0; i < SyncHashLength; i++)
            result[i] = (byte)(req2[i] ^ req3[i]);
        return result;
    }

    private static byte[] BuildBitTorrentHandshake(byte[] infoHash, byte[] peerId)
    {
        var handshake = Handshake.CreateWithExtensions(infoHash, peerId, supportDHT: true);
        return handshake.ToBytes();
    }

    /// <summary>
    /// Returns any remaining bytes from bulk scanning that haven't been consumed
    /// by ReadExactAsync. These bytes were read from the transport during pattern
    /// scanning but belong to the post-handshake protocol stream. They must be
    /// passed through MseResult.InitialPayload → MseTransportStream._bufferedPayload
    /// so they're drained before the transport is read directly.
    /// </summary>
    private byte[]? GetAndClearScanExcess()
    {
        if (_scanExcess is null) return null;
        int remaining = _scanExcess.Length - _scanExcessOffset;
        if (remaining <= 0)
        {
            _scanExcess = null;
            return null;
        }
        var result = new byte[remaining];
        Buffer.BlockCopy(_scanExcess, _scanExcessOffset, result, 0, remaining);
        _scanExcess = null;
        _scanExcessOffset = 0;
        return result;
    }

    // --- I/O helpers ---

    private async Task<int> ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;

        // Drain any excess bytes from bulk scanning first
        if (_scanExcess is not null)
        {
            int available = _scanExcess.Length - _scanExcessOffset;
            int toCopy = Math.Min(buffer.Length, available);
            _scanExcess.AsSpan(_scanExcessOffset, toCopy).CopyTo(buffer.Span);
            _scanExcessOffset += toCopy;
            totalRead += toCopy;

            if (_scanExcessOffset >= _scanExcess.Length)
                _scanExcess = null;

            if (totalRead >= buffer.Length)
                return totalRead;
        }

        while (totalRead < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new MseNegotiationException("Connection closed during MSE negotiation");
            totalRead += read;
        }
        return totalRead;
    }

    private static void WriteBigEndianUInt32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteBigEndianUInt16(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static uint ReadBigEndianUInt32(byte[] buf, int offset)
        => (uint)(buf[offset] << 24 | buf[offset + 1] << 16 | buf[offset + 2] << 8 | buf[offset + 3]);

    private static ushort ReadBigEndianUInt16(byte[] buf, int offset)
        => (ushort)(buf[offset] << 8 | buf[offset + 1]);
}
