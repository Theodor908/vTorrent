using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using vTorrent.Core.PeerCommunication.Bandwidth;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PeerCommunication.Events;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Identification;
using vTorrent.Core.PeerCommunication.Exceptions;
using vTorrent.Core.PeerCommunication.Extensions;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Core.PeerCommunication.Transport.Tcp;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Interfaces;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PeerCommunication.Models
{
    public partial class PeerConnection : IPeerConnection, IPeerBandwidthConsumer
    {
        private readonly PeerSettings _settings;
        private readonly ILogger<PeerConnection> _logger;
        private readonly ITransportStream _transport;
        private readonly Channel<PeerMessage> _controlSendChannel;
        private readonly Channel<PeerMessage> _dataSendChannel;
        private Task? _sendFlushTask;
        private readonly CancellationTokenSource _disposeCts;
        private Timer _keepAliveTimer;
        private Task _receiveTask; // NEW: Background receive task

        // Reusable buffers to avoid per-message allocations (like libtorrent)
        private readonly byte[] _lengthBuffer = new byte[4];  // Reused for reading message length prefix (public ReceiveMessageAsync fallback)

        // Read-ahead buffer for receive loop — reduces syscalls by processing multiple messages per read
        private const int ReadBufferSize = 65536; // 64KB
        private const int MaxMessageSize = 1024 * 1024 * 20; // 20MB

        private bool _isConnected;
        private bool _isChoked = true;
        private bool _isInterested = false;
        private bool _isChoking = true;
        private bool _peerIsInterested = false;
        private byte[] _peerBitfield;
        private byte[] _peerId;
        private long _bytesUploaded;
        private long _bytesDownloaded;
        private DateTime _connectedAt;

        // RTT tracking for dynamic pipeline depth
        private double _roundTripTimeMs = 100;  // Default 100ms
        private readonly object _rttLock = new();
        private DateTime _lastRequestTime;
        private const double RttSmoothingFactor = 0.125;  // EMA smoothing factor

        // Snubbed state
        private bool _isSnubbed;

        // Seed flag - set once when bitfield is verified complete
        private volatile bool _isSeed;

        // Reject tracking (BEP 6 fast extension)
        private int _consecutiveRejects;
        private readonly int _maxRejects;
        private readonly int _allowedFastSetSize;

        // Statistics callbacks - total traffic (includes protocol overhead)
        private readonly Action<IPeerConnection, int> _onBytesDownloaded;
        private readonly Action<IPeerConnection, int> _onBytesUploaded;

        // Statistics callbacks - payload only (actual file data)
        private readonly Action<IPeerConnection, int> _onPayloadDownloaded;
        private readonly Action<IPeerConnection, int> _onPayloadUploaded;

        // Extension protocol support
        private ExtensionManager _extensionManager;
        private bool _peerSupportsExtensions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IExternalIpVoter? _externalIpVoter;
        private readonly IOptionsMonitor<PrivacySettings> _privacyMonitor;

        // BEP 52 hash exchange support
        private IHashExchangeHandler? _hashExchangeHandler;

        /// <summary>
        /// Set the hash exchange handler for v2/hybrid torrents.
        /// </summary>
        public IHashExchangeHandler? HashExchangeHandler
        {
            get => _hashExchangeHandler;
            set => _hashExchangeHandler = value;
        }

        // Bandwidth limiting (following libtorrent's pattern)
        private readonly IPeerBandwidthLimiter _bandwidthLimiter;
        private readonly string _consumerId;
        private readonly string _endpointString;
        private int _downloadQuota;
        private int _uploadQuota;
        private readonly SemaphoreSlim _downloadQuotaSignal = new(0);
        private readonly SemaphoreSlim _uploadQuotaSignal = new(0);
        private const int DefaultBandwidthPriority = 128;
        private int _bandwidthPriority = DefaultBandwidthPriority;

        public PeerInfo PeerInfo { get; }
        public string EndpointString => _endpointString;
        public byte[] PeerId => _peerId;
        public bool IsChoked => _isChoked;
        public bool IsInterested => _isInterested;
        public bool IsChoking => _isChoking;
        public bool PeerIsInterested => _peerIsInterested;
        public bool IsConnected => _isConnected && _transport.IsConnected;
        public byte[] PeerBitfield { get => _peerBitfield; set => _peerBitfield = value; }
        public long BytesDownloaded => _bytesDownloaded;
        public long BytesUploaded => _bytesUploaded;
        public DateTime ConnectedAt => _connectedAt;
        public double RoundTripTimeMs => _roundTripTimeMs;
        public bool IsSnubbed { get => _isSnubbed; set => _isSnubbed = value; }
        public bool IsSeed => _isSeed;

        /// <inheritdoc />
        public string? ClientName => _extensionManager?.PeerHandshake?.ClientVersion
            ?? (_peerId != null ? ClientIdentifier.Identify(_peerId).ToString() : null);

        /// <inheritdoc />
        /// <inheritdoc />
        public bool IsEncrypted { get; internal set; } = false;

        /// <summary>When true, the BT handshake was already sent as MSE IA data.</summary>
        public bool HandshakeAlreadySent { get; internal set; }

        /// <inheritdoc />
        public bool IsIncoming { get; internal set; } = false;

        public int? RemoteRequestQueueSize =>
            _extensionManager?.PeerHandshake?.RequestQueueSize;

        /// <summary>
        /// Whether the peer supports the extension protocol (BEP 10).
        /// </summary>
        public bool PeerSupportsExtensions => _peerSupportsExtensions;

        public bool PeerSupportsFastExtension { get; private set; }

        /// <inheritdoc />
        public bool IsUtp => _transport.TransportType == global::vTorrent.Abstractions.Enums.TransportType.Utp;

        /// <summary>
        /// The extension manager for this connection.
        /// </summary>
        public ExtensionManager ExtensionManager => _extensionManager;

        // IPeerBandwidthConsumer implementation
        public string ConsumerId => _consumerId;
        public bool IsDisconnecting => !_isConnected || _disposeCts.IsCancellationRequested;
        public int BandwidthPriority
        {
            get => _bandwidthPriority;
            set => _bandwidthPriority = Math.Clamp(value, 1, 255);
        }

        public event EventHandler<PeerStateChangedEventArgs> StateChanged;
        public event EventHandler<PeerMessageReceivedEventArgs> MessageReceived;
        public event EventHandler<PeerConnectionLostEventArgs> ConnectionLost;

        public PeerConnection(
            PeerInfo peerInfo,
            PeerSettings settings,
            ITransportStream transport,
            ILogger<PeerConnection> logger,
            Action<IPeerConnection, int> onBytesDownloaded = null,
            Action<IPeerConnection, int> onBytesUploaded = null,
            Action<IPeerConnection, int> onPayloadDownloaded = null,
            Action<IPeerConnection, int> onPayloadUploaded = null,
            ILoggerFactory loggerFactory = null,
            IPeerBandwidthLimiter bandwidthLimiter = null,
            IExternalIpVoter? externalIpVoter = null,
            IOptionsMonitor<PrivacySettings>? privacyMonitor = null)
        {
            PeerInfo = peerInfo ?? throw new ArgumentNullException(nameof(peerInfo));
            _endpointString = peerInfo.EndPoint?.ToString() ?? "";
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _onBytesDownloaded = onBytesDownloaded;
            _onBytesUploaded = onBytesUploaded;
            _onPayloadDownloaded = onPayloadDownloaded;
            _onPayloadUploaded = onPayloadUploaded;
            _loggerFactory = loggerFactory;
            _bandwidthLimiter = bandwidthLimiter;
            _externalIpVoter = externalIpVoter;
            _privacyMonitor = privacyMonitor;
            _maxRejects = settings.MaxRejects;
            _allowedFastSetSize = settings.AllowedFastSetSize;
            _consumerId = $"peer_{peerInfo.EndPoint}_{Guid.NewGuid():N}";
            _controlSendChannel = Channel.CreateUnbounded<PeerMessage>(new UnboundedChannelOptions { SingleReader = true });
            _dataSendChannel = Channel.CreateUnbounded<PeerMessage>(new UnboundedChannelOptions { SingleReader = true });
            _disposeCts = new CancellationTokenSource();

            if (_transport is TcpTransportStream tcpStream)
            {
                tcpStream.SetDscp(settings.PeerDscp);
            }
        }

        public async Task ConnectAsync(byte[] infoHash, CancellationToken cancellationToken = default, byte[]? preReadHandshake = null)
        {
            if (infoHash == null || infoHash.Length != 20)
                throw new ArgumentException("Info hash must be exactly 20 bytes", nameof(infoHash));

            if (_isConnected)
                throw new InvalidOperationException("Already connected");

            try
            {
                _logger.LogDebug("Starting handshake with {Peer} via {Transport}",
                    PeerInfo.EndPoint, _transport.TransportType);

                await PerformHandshakeAsync(infoHash, cancellationToken, preReadHandshake).ConfigureAwait(false);
                _isConnected = true;
                _connectedAt = DateTime.UtcNow;

                _logger.LogDebug("Successfully connected to {Peer} [PeerId: {PeerId}]",
                    PeerInfo.EndPoint, Encoding.ASCII.GetString(_peerId));

                // Start the send flush loop (must be before extension init, which enqueues messages)
                _sendFlushTask = Task.Run(() => SendFlushLoopAsync(_disposeCts.Token), _disposeCts.Token);

                // Start keep-alive timer
                StartKeepAliveTimer();

                // Initialize extension manager if peer supports extensions
                if (_peerSupportsExtensions && _loggerFactory != null)
                {
                    await InitializeExtensionsAsync(cancellationToken).ConfigureAwait(false);
                }

                // Start background message receiving loop
                _receiveTask = Task.Run(() => ReceiveLoopAsync(_disposeCts.Token), _disposeCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to connect to {Peer}", PeerInfo.EndPoint);
                throw;
            }
        }
        
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Starting receive loop for {Peer}", PeerInfo.EndPoint);

            byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
            int readOffset = 0; // Start of unconsumed data
            int readEnd = 0;    // End of valid data

            try
            {
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    try
                    {
                        // Read from transport into buffer (append after existing data)
                        int bytesRead = await _transport.ReadAsync(
                            readBuffer.AsMemory(readEnd, readBuffer.Length - readEnd),
                            cancellationToken).ConfigureAwait(false);

                        if (bytesRead == 0)
                        {
                            await HandleConnectionLostAsync("Peer closed connection").ConfigureAwait(false);
                            break;
                        }

                        readEnd += bytesRead;

                        // Inner loop: process ALL complete messages in the buffer
                        while (true)
                        {
                            int available = readEnd - readOffset;

                            // Need at least 4 bytes for the length prefix
                            if (available < 4)
                                break;

                            int messageLength = BinaryPrimitives.ReadInt32BigEndian(
                                readBuffer.AsSpan(readOffset, 4));

                            // Keep-alive (length == 0)
                            if (messageLength == 0)
                            {
                                LogReceivedKeepAlive(_logger, PeerInfo.EndPoint);
                                readOffset += 4;
                                continue;
                            }

                            // Validate message length
                            if (messageLength < 0 || messageLength > MaxMessageSize)
                            {
                                throw new PeerProtocolException(
                                    $"Invalid message length: {messageLength}",
                                    PeerInfo.EndPoint.ToString());
                            }

                            int totalFrameSize = 4 + messageLength;

                            // Request download quota before consuming message bytes
                            int quotaGranted = await RequestDownloadQuotaAsync(totalFrameSize, cancellationToken).ConfigureAwait(false);
                            if (quotaGranted < totalFrameSize && _bandwidthLimiter?.IsDownloadLimited == true)
                            {
                                if (quotaGranted > 0)
                                    Interlocked.Add(ref _downloadQuota, quotaGranted);

                                _logger.LogDebug("Waiting for download quota for {Peer} ({Size} bytes)",
                                    PeerInfo.EndPoint, totalFrameSize);
                                quotaGranted = await RequestDownloadQuotaAsync(totalFrameSize, cancellationToken).ConfigureAwait(false);
                            }

                            // Oversized message: won't fit in read buffer — use dedicated allocation
                            if (messageLength > ReadBufferSize - 4)
                            {
                                // We already have the 4-byte length prefix consumed; now read the body
                                int bodyInBuffer = available - 4; // bytes of body already in read buffer
                                byte[] oversized = ArrayPool<byte>.Shared.Rent(messageLength);
                                try
                                {
                                    // Copy whatever body bytes are already in the read buffer
                                    if (bodyInBuffer > 0)
                                    {
                                        Buffer.BlockCopy(readBuffer, readOffset + 4, oversized, 0, bodyInBuffer);
                                    }

                                    // Read remaining body directly from transport
                                    int remaining = messageLength - bodyInBuffer;
                                    int totalBodyRead = bodyInBuffer;
                                    while (totalBodyRead < messageLength)
                                    {
                                        int read = await _transport.ReadAsync(
                                            oversized.AsMemory(totalBodyRead, messageLength - totalBodyRead),
                                            cancellationToken).ConfigureAwait(false);

                                        if (read == 0)
                                            throw new IOException(
                                                $"Connection closed after reading {totalBodyRead}/{messageLength} bytes (partial message)");

                                        totalBodyRead += read;
                                    }

                                    var message = PeerMessage.FromBytes(oversized, messageLength);
                                    Interlocked.Add(ref _bytesDownloaded, totalFrameSize);
                                    _onBytesDownloaded?.Invoke(this, totalFrameSize);
                                    LogReceivedMessage(_logger, message.Type, PeerInfo.EndPoint);

                                    await HandleReceivedMessageAsync(message).ConfigureAwait(false);
                                    MessageReceived?.Invoke(this, new PeerMessageReceivedEventArgs(message));
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(oversized);
                                }

                                // Advance past the 4-byte length prefix + partial body we consumed from buffer
                                readOffset += 4 + bodyInBuffer;
                                continue;
                            }

                            // Normal path: check if full message is in the buffer
                            if (available < totalFrameSize)
                                break; // Need more data — exit inner loop to read from transport

                            // Parse message from buffer (copies payload out before buffer is reused)
                            var msg = PeerMessage.FromBytes(readBuffer, readOffset + 4, messageLength);
                            Interlocked.Add(ref _bytesDownloaded, totalFrameSize);
                            _onBytesDownloaded?.Invoke(this, totalFrameSize);
                            LogReceivedMessage(_logger, msg.Type, PeerInfo.EndPoint);

                            readOffset += totalFrameSize;

                            // Process message and fire event
                            await HandleReceivedMessageAsync(msg).ConfigureAwait(false);
                            MessageReceived?.Invoke(this, new PeerMessageReceivedEventArgs(msg));
                        }

                        // Shift unconsumed data to front of buffer
                        int unconsumed = readEnd - readOffset;
                        if (unconsumed > 0 && readOffset > 0)
                        {
                            Buffer.BlockCopy(readBuffer, readOffset, readBuffer, 0, unconsumed);
                        }
                        readEnd = unconsumed;
                        readOffset = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex) when (ex is IOException || ex is SocketException)
                    {
                        _logger.LogDebug("Connection closed for {Peer}: {Message}", PeerInfo.EndPoint, ex.Message);
                        await HandleConnectionLostAsync("Connection closed", ex).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in receive loop for {Peer}", PeerInfo.EndPoint);
                await HandleConnectionLostAsync("Receive loop error", ex).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
            }

            _logger.LogDebug("Receive loop ended for {Peer}", PeerInfo.EndPoint);
        }

        private async Task PerformHandshakeAsync(byte[] infoHash, CancellationToken cancellationToken, byte[]? preReadHandshake = null)
        {
            _logger.LogDebug("Starting handshake with {Peer}", PeerInfo.EndPoint);

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(_settings.HandshakeTimeout));

            try
            {
                if (!HandshakeAlreadySent)
                {
                    byte[] peerIdBytes = Encoding.ASCII.GetBytes(_settings.PeerId);
                    var handshake = Handshake.CreateWithExtensions(infoHash, peerIdBytes, supportDHT: true);
                    byte[] handshakeBytes = handshake.ToBytes();

                    await _transport.WriteAsync(handshakeBytes.AsMemory(), handshakeCts.Token).ConfigureAwait(false);
                    _logger.LogTrace("Sent handshake to {Peer}", PeerInfo.EndPoint);
                }
                else
                {
                    _logger.LogTrace("Handshake already sent via MSE IA to {Peer}", PeerInfo.EndPoint);
                }

                byte[] receivedHandshake;
                if (preReadHandshake != null)
                {
                    if (preReadHandshake.Length != Handshake.HandshakeLength)
                        throw new PeerProtocolException(
                            $"preReadHandshake must be {Handshake.HandshakeLength} bytes",
                            PeerInfo.EndPoint?.ToString() ?? "");
                    receivedHandshake = preReadHandshake;
                }
                else
                {
                    receivedHandshake = new byte[Handshake.HandshakeLength];
                    int bytesRead = 0;
                    while (bytesRead < Handshake.HandshakeLength)
                    {
                        int read = await _transport.ReadAsync(
                            receivedHandshake.AsMemory(bytesRead, Handshake.HandshakeLength - bytesRead),
                            handshakeCts.Token).ConfigureAwait(false);
                        if (read == 0)
                            throw new PeerProtocolException("Peer closed connection during handshake",
                                PeerInfo.EndPoint.ToString());
                        bytesRead += read;
                    }
                }

                var peerHandshake = Handshake.FromBytes(receivedHandshake);

                if (!peerHandshake.InfoHash.AsSpan().SequenceEqual(infoHash))
                {
                    throw new PeerProtocolException("Info hash mismatch during handshake",
                        PeerInfo.EndPoint.ToString());
                }

                _peerId = peerHandshake.PeerId;
                PeerInfo.PeerId = peerHandshake.PeerId;

                // Check if peer supports extension protocol (BEP 10)
                _peerSupportsExtensions = peerHandshake.SupportsExtensionProtocol();
                PeerSupportsFastExtension = peerHandshake.SupportsFastExtension();
                _logger.LogDebug("Peer {Peer} supports extensions: {Supports}",
                    PeerInfo.EndPoint, _peerSupportsExtensions);

                _logger.LogDebug("Handshake complete with {Peer}", PeerInfo.EndPoint);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Handshake with {PeerInfo.EndPoint} timed out");
            }
        }

        /// <summary>
        /// Initializes the extension manager and sends extension handshake.
        /// </summary>
        private async Task InitializeExtensionsAsync(CancellationToken cancellationToken)
        {
            // Only create extension manager if it doesn't already exist
            // (it may have been created in RegisterExtension if extensions were registered before connect)
            if (_extensionManager == null)
            {
                var extLogger = _loggerFactory.CreateLogger<ExtensionManager>();
                _extensionManager = new ExtensionManager(
                    extLogger,
                    _settings.ClientVersion ?? "vTorrent/1.0",
                    _settings.ListenPort,
                    async (msg, ct) => await SendMessageAsync(msg, ct),
                    externalIpVoter: _externalIpVoter,
                    privacyMonitor: _privacyMonitor);
            }

            _extensionManager.SetPeerSupportsExtensions(_peerSupportsExtensions);

            // Send our extension handshake (includes any pre-registered extensions)
            await _extensionManager.SendExtensionHandshakeAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Extension manager initialized for {Peer}", PeerInfo.EndPoint);
        }

        /// <summary>
        /// Registers an extension with this connection's extension manager.
        /// Call this before ConnectAsync to have the extension advertised in the handshake.
        /// </summary>
        public void RegisterExtension(IExtension extension)
        {
            if (_extensionManager == null)
            {
                // Create extension manager early if registering extensions before connect
                if (_loggerFactory != null)
                {
                    var extLogger = _loggerFactory.CreateLogger<ExtensionManager>();
                    _extensionManager = new ExtensionManager(
                        extLogger,
                        _settings.ClientVersion ?? "vTorrent/1.0",
                        _settings.ListenPort,
                        async (msg, ct) => await SendMessageAsync(msg, ct),
                        externalIpVoter: _externalIpVoter,
                        privacyMonitor: _privacyMonitor);
                }
                else
                {
                    _logger.LogWarning("Cannot register extension - no logger factory provided");
                    return;
                }
            }

            _extensionManager.RegisterExtension(extension);
        }

        public async Task SendMessageAsync(PeerMessage message, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected");

            if (message.Type == MessageType.Piece)
            {
                // For piece (data) messages, await upload quota BEFORE enqueuing
                // so the flush loop doesn't need to do async quota waits
                int bytesToSend = message.TotalSize;
                int quotaGranted = await RequestUploadQuotaAsync(bytesToSend, cancellationToken).ConfigureAwait(false);
                if (quotaGranted < bytesToSend && _bandwidthLimiter?.IsUploadLimited == true)
                {
                    if (quotaGranted > 0)
                        Interlocked.Add(ref _uploadQuota, quotaGranted);

                    _logger.LogDebug("Upload quota timeout for {Peer}, retrying...", PeerInfo.EndPoint);
                    quotaGranted = await RequestUploadQuotaAsync(bytesToSend, cancellationToken).ConfigureAwait(false);
                }

                _dataSendChannel.Writer.TryWrite(message);
            }
            else
            {
                // Control messages get priority — no quota needed (small overhead)
                _controlSendChannel.Writer.TryWrite(message);
            }
        }

        /// <summary>
        /// Sends multiple messages by enqueuing them to the appropriate priority channels.
        /// The background flush loop handles batching and coalescing into single network writes.
        /// </summary>
        public async Task SendMessagesAsync(IReadOnlyList<PeerMessage> messages, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected");

            if (messages == null || messages.Count == 0)
                return;

            foreach (var msg in messages)
            {
                await SendMessageAsync(msg, cancellationToken).ConfigureAwait(false);
            }
        }

        // Keep public for manual use if needed, but receive loop handles this automatically
        public async Task<PeerMessage> ReceiveMessageAsync(CancellationToken cancellationToken = default)
        {
            return await ReceiveMessageInternalAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<PeerMessage> ReceiveMessageInternalAsync(CancellationToken cancellationToken)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected");

            // Read 4-byte length prefix using reusable buffer (avoids allocation per message)
            // The length prefix is always allowed through (minimal overhead)
            int bytesRead = await ReadExactAsync(_lengthBuffer, 0, 4, cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                await HandleConnectionLostAsync("Peer closed connection").ConfigureAwait(false);
                return null;
            }

            int messageLength = BinaryPrimitives.ReadInt32BigEndian(_lengthBuffer);

            // Check for keep-alive (length = 0)
            if (messageLength == 0)
            {
                LogReceivedKeepAlive(_logger, PeerInfo.EndPoint);
                return null;
            }

            // Validate message length
            if (messageLength < 0 || messageLength > 1024 * 1024 * 20)
            {
                throw new PeerProtocolException($"Invalid message length: {messageLength}",
                    PeerInfo.EndPoint.ToString());
            }

            // Request download quota before reading the message body (following libtorrent's pattern)
            // We include the 4-byte header in the quota request
            int totalBytes = 4 + messageLength;
            int quotaGranted = await RequestDownloadQuotaAsync(totalBytes, cancellationToken).ConfigureAwait(false);
            if (quotaGranted < totalBytes && _bandwidthLimiter?.IsDownloadLimited == true)
            {
                // Return partial quota and retry
                if (quotaGranted > 0)
                    Interlocked.Add(ref _downloadQuota, quotaGranted);

                _logger.LogDebug("Waiting for download quota for {Peer} ({Size} bytes)", PeerInfo.EndPoint, totalBytes);
                quotaGranted = await RequestDownloadQuotaAsync(totalBytes, cancellationToken).ConfigureAwait(false);
            }

            // Read message ID + payload using ArrayPool to reduce GC pressure
            // This is critical for high-speed downloads (hundreds of messages/second)
            byte[] messageData = ArrayPool<byte>.Shared.Rent(messageLength);
            try
            {
                await ReadExactAsync(messageData, 0, messageLength, cancellationToken).ConfigureAwait(false);

                // Parse message from pooled buffer (only copies necessary data)
                var message = PeerMessage.FromBytes(messageData, messageLength);

                Interlocked.Add(ref _bytesDownloaded, totalBytes);
                _onBytesDownloaded?.Invoke(this, totalBytes);

                LogReceivedMessage(_logger, message.Type, PeerInfo.EndPoint);

                return message;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(messageData);
            }
        }

        private async Task<int> ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;

            while (totalRead < count)
            {
                int read = await _transport.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    if (totalRead == 0)
                        return 0; // Clean disconnect before any data read

                    // Partial read = connection died mid-message - this is corrupt data
                    throw new IOException(
                        $"Connection closed after reading {totalRead}/{count} bytes (partial message)");
                }

                totalRead += read;
            }

            return totalRead;
        }

        private async Task HandleReceivedMessageAsync(PeerMessage message)
        {
            bool stateChanged = false;

            switch (message.Type)
            {
                case MessageType.Choke:
                    if (!_isChoked)
                    {
                        _isChoked = true;
                        stateChanged = true;
                        LogChokedBy(_logger, PeerInfo.EndPoint);
                    }

                    break;

                case MessageType.Unchoke:
                    if (_isChoked)
                    {
                        _isChoked = false;
                        stateChanged = true;
                        LogUnchokedBy(_logger, PeerInfo.EndPoint);
                    }

                    break;

                case MessageType.Interested:
                    if (!_peerIsInterested)
                    {
                        _peerIsInterested = true;
                        stateChanged = true;
                        LogPeerIsInterested(_logger, PeerInfo.EndPoint);
                    }

                    break;

                case MessageType.NotInterested:
                    if (_peerIsInterested)
                    {
                        _peerIsInterested = false;
                        stateChanged = true;
                        LogPeerIsNotInterested(_logger, PeerInfo.EndPoint);
                    }

                    break;

                case MessageType.Bitfield:
                    _peerBitfield = message.Payload;
                    _logger.LogDebug("Received bitfield from {Peer} ({Length} bytes)",
                        PeerInfo.EndPoint, _peerBitfield.Length);
                    break;
                //...//
                case MessageType.Have:
                    int pieceIndex = message.ParseHave();
                    if (_peerBitfield != null)
                    {
                        int byteIndex = pieceIndex / 8;
                        int bitIndex = 7 - (pieceIndex % 8);
                        if (byteIndex < _peerBitfield.Length)
                        {
                            _peerBitfield[byteIndex] |= (byte)(1 << bitIndex);
                        }
                    }

                    LogPeerHasPiece(_logger, PeerInfo.EndPoint, pieceIndex);
                    break;

                case MessageType.HaveAll:
                    _logger.LogDebug("Peer {Peer} has ALL pieces", PeerInfo.EndPoint);
                    break;

                case MessageType.HaveNone:
                    _logger.LogDebug("Peer {Peer} has NO pieces", PeerInfo.EndPoint);
                    break;

                case MessageType.AllowedFast:
                    int allowedPiece = message.ParseAllowedFast();
                    _logger.LogDebug("Peer {Peer} allows fast piece {Piece}", PeerInfo.EndPoint, allowedPiece);
                    break;

                case MessageType.RejectRequest:
                    var (rejPiece, rejBegin, rejLength) = message.ParseRejectRequest();
                    _logger.LogDebug("Peer {Peer} rejected request for piece {Piece}", PeerInfo.EndPoint, rejPiece);
                    _consecutiveRejects++;
                    if (_consecutiveRejects >= _maxRejects)
                    {
                        _logger.LogDebug("Peer {Peer} exceeded max rejects ({Max}), disconnecting", PeerInfo.EndPoint, _maxRejects);
                        await DisconnectAsync().ConfigureAwait(false);
                    }
                    break;

                case MessageType.Piece:
                    _consecutiveRejects = 0;
                    break;

                case MessageType.SuggestPiece:
                    int suggestedPiece = message.ParseSuggestPiece();
                    _logger.LogDebug("Peer {Peer} suggests piece {Piece}", PeerInfo.EndPoint, suggestedPiece);
                    break;

                case MessageType.Extended:
                    if (_extensionManager != null)
                    {
                        await _extensionManager.HandleExtendedMessageAsync(message).ConfigureAwait(false);
                    }
                    break;

                // BEP 52 Hash Exchange
                case MessageType.HashRequest:
                    if (_hashExchangeHandler is not null)
                        await _hashExchangeHandler.OnHashRequestAsync(this, HashRequestMessage.Parse(message.Payload), _disposeCts.Token).ConfigureAwait(false);
                    break;
                case MessageType.Hashes:
                    if (_hashExchangeHandler is not null)
                        await _hashExchangeHandler.OnHashesReceivedAsync(this, HashesMessage.Parse(message.Payload), _disposeCts.Token).ConfigureAwait(false);
                    break;
                case MessageType.HashReject:
                    if (_hashExchangeHandler is not null)
                        await _hashExchangeHandler.OnHashRejectAsync(this, HashRejectMessage.Parse(message.Payload), _disposeCts.Token).ConfigureAwait(false);
                    break;
            }

            if (stateChanged)
            {
                StateChanged?.Invoke(this,
                    new PeerStateChangedEventArgs(_isChoked, _isInterested, _isChoking, _peerIsInterested));
            }

            await Task.CompletedTask;
        }

        // IPeerBandwidthConsumer implementation - quota callbacks
        public void OnDownloadQuotaAssigned(int bytes)
        {
            Interlocked.Add(ref _downloadQuota, bytes);
            // Signal any waiting receive operations
            try
            {
                _downloadQuotaSignal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Connection is being disposed, ignore
            }
            catch (SemaphoreFullException)
            {
                // Semaphore already at max count, ignore
            }
        }

        public void OnUploadQuotaAssigned(int bytes)
        {
            Interlocked.Add(ref _uploadQuota, bytes);
            // Signal any waiting send operations
            try
            {
                _uploadQuotaSignal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Connection is being disposed, ignore
            }
            catch (SemaphoreFullException)
            {
                // Semaphore already at max count, ignore
            }
        }

        /// <summary>
        /// Requests upload quota from the bandwidth limiter.
        /// Blocks until quota is available or timeout.
        /// </summary>
        private async Task<int> RequestUploadQuotaAsync(int bytes, CancellationToken cancellationToken)
        {
            if (_bandwidthLimiter == null || !_bandwidthLimiter.IsUploadLimited)
                return bytes;

            // Check if we already have enough quota
            int currentQuota = Volatile.Read(ref _uploadQuota);
            if (currentQuota >= bytes)
            {
                Interlocked.Add(ref _uploadQuota, -bytes);
                return bytes;
            }

            // Request quota from limiter
            int granted = _bandwidthLimiter.RequestUploadQuota(this, bytes);
            if (granted > 0)
            {
                return granted;
            }

            // Wait for quota to be assigned (with timeout)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            while (!timeoutCts.Token.IsCancellationRequested)
            {
                await _uploadQuotaSignal.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

                currentQuota = Volatile.Read(ref _uploadQuota);
                if (currentQuota > 0)
                {
                    int toUse = Math.Min(currentQuota, bytes);
                    Interlocked.Add(ref _uploadQuota, -toUse);
                    return toUse;
                }
            }

            return 0;
        }

        /// <summary>
        /// Requests download quota from the bandwidth limiter.
        /// Blocks until quota is available or timeout.
        /// </summary>
        private async Task<int> RequestDownloadQuotaAsync(int bytes, CancellationToken cancellationToken)
        {
            if (_bandwidthLimiter == null || !_bandwidthLimiter.IsDownloadLimited)
                return bytes;

            // Check if we already have enough quota
            int currentQuota = Volatile.Read(ref _downloadQuota);
            if (currentQuota >= bytes)
            {
                Interlocked.Add(ref _downloadQuota, -bytes);
                return bytes;
            }

            // Request quota from limiter
            int granted = _bandwidthLimiter.RequestDownloadQuota(this, bytes);
            if (granted > 0)
            {
                return granted;
            }

            // Wait for quota to be assigned (with timeout)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            while (!timeoutCts.Token.IsCancellationRequested)
            {
                await _downloadQuotaSignal.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

                currentQuota = Volatile.Read(ref _downloadQuota);
                if (currentQuota > 0)
                {
                    int toUse = Math.Min(currentQuota, bytes);
                    Interlocked.Add(ref _downloadQuota, -toUse);
                    return toUse;
                }
            }

            return 0;
        }

        public async Task SendBitfieldAsync(byte[] bitfield, CancellationToken cancellationToken = default)
        {
            var message = PeerMessage.CreateBitfield(bitfield);
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        public async Task SendHaveNoneAsync(int totalPieces, CancellationToken cancellationToken = default)
        {
            if (PeerSupportsFastExtension)
            {
                var message = PeerMessage.CreateHaveNone();
                await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Fast Extension fallback: send all-zero bitfield
                var zeroBitfield = new byte[(totalPieces + 7) / 8];
                await SendBitfieldAsync(zeroBitfield, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task SetInterestedAsync(bool interested, CancellationToken cancellationToken = default)
        {
            if (_isInterested == interested)
                return;

            var message = interested ? PeerMessage.CreateInterested() : PeerMessage.CreateNotInterested();
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);

            _isInterested = interested;
            LogSetInterested(_logger, interested, PeerInfo.EndPoint);

            StateChanged?.Invoke(this, new PeerStateChangedEventArgs(_isChoked, _isInterested, _isChoking, _peerIsInterested));
        }

        public async Task SetChokingAsync(bool choking, CancellationToken cancellationToken = default)
        {
            if (_isChoking == choking)
                return;

            var message = choking ? PeerMessage.CreateChoke() : PeerMessage.CreateUnchoke();
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);

            _isChoking = choking;
            LogSetChoking(_logger, choking, PeerInfo.EndPoint);

            StateChanged?.Invoke(this, new PeerStateChangedEventArgs(_isChoked, _isInterested, _isChoking, _peerIsInterested));
        }

        public async Task RequestBlockAsync(int pieceIndex, int begin, int length, CancellationToken cancellationToken = default)
        {
            // Track request time for RTT calculation
            lock (_rttLock)
            {
                _lastRequestTime = DateTime.UtcNow;
            }

            var message = PeerMessage.CreateRequest(pieceIndex, begin, length);
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Requests multiple blocks in a single network write (like libtorrent's request batching).
        /// This dramatically reduces latency when filling the request pipeline.
        /// </summary>
        /// <param name="blocks">List of (pieceIndex, begin, length) tuples to request.</param>
        public async Task RequestBlocksBatchAsync(IReadOnlyList<(int pieceIndex, int begin, int length)> blocks, CancellationToken cancellationToken = default)
        {
            if (blocks == null || blocks.Count == 0)
                return;

            // Track request time for RTT calculation
            lock (_rttLock)
            {
                _lastRequestTime = DateTime.UtcNow;
            }

            // Create all request messages
            var messages = new List<PeerMessage>(blocks.Count);
            foreach (var (pieceIndex, begin, length) in blocks)
            {
                messages.Add(PeerMessage.CreateRequest(pieceIndex, begin, length));
            }

            // Send all in a single batch
            await SendMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Updates RTT measurement when a piece block is received.
        /// Uses exponential moving average for smoothing.
        /// </summary>
        /// <summary>
        /// Checks if this peer has all pieces and sets the IsSeed flag.
        /// Called after bitfield/have processing. Only scans once — flag is sticky.
        /// </summary>
        internal void CheckIfSeed(int totalPieces)
        {
            if (_isSeed || _peerBitfield == null)
                return;

            for (int i = 0; i < totalPieces; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = 7 - (i % 8);
                if (byteIndex >= _peerBitfield.Length || (_peerBitfield[byteIndex] & (1 << bitIndex)) == 0)
                    return;
            }

            _isSeed = true;
        }

        internal void UpdateRtt()
        {
            lock (_rttLock)
            {
                if (_lastRequestTime != default)
                {
                    var rttSample = (DateTime.UtcNow - _lastRequestTime).TotalMilliseconds;
                    // Exponential moving average: RTT = (1-a) * old_RTT + a * sample
                    _roundTripTimeMs = (1 - RttSmoothingFactor) * _roundTripTimeMs + RttSmoothingFactor * rttSample;
                    // Clamp to reasonable range (10ms - 30s)
                    _roundTripTimeMs = Math.Clamp(_roundTripTimeMs, 10, 30000);
                }
            }
        }

        public async Task SendBlockAsync(int pieceIndex, int begin, byte[] block, CancellationToken cancellationToken = default)
        {
            var message = PeerMessage.CreatePiece(pieceIndex, begin, block);
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);

            // Record payload upload (just the actual file data, not the message overhead)
            _onPayloadUploaded?.Invoke(this, block.Length);
        }

        public async Task CancelBlockAsync(int pieceIndex, int begin, int length, CancellationToken cancellationToken = default)
        {
            var message = PeerMessage.CreateCancel(pieceIndex, begin, length);
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        public async Task AnnounceHaveAsync(int pieceIndex, CancellationToken cancellationToken = default)
        {
            var message = PeerMessage.CreateHave(pieceIndex);
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        // BEP 52 Hash Exchange send methods
        public async Task SendHashRequestAsync(HashRequestMessage msg, CancellationToken cancellationToken = default)
        {
            var payload = new byte[msg.SerializedSize];
            msg.WriteTo(payload);
            var message = new PeerMessage(MessageType.HashRequest, payload);
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        public async Task SendHashesAsync(HashesMessage msg, CancellationToken cancellationToken = default)
        {
            var payload = new byte[msg.SerializedSize];
            msg.WriteTo(payload);
            var message = new PeerMessage(MessageType.Hashes, payload);
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        public async Task SendHashRejectAsync(HashRejectMessage msg, CancellationToken cancellationToken = default)
        {
            var payload = new byte[msg.SerializedSize];
            msg.WriteTo(payload);
            var message = new PeerMessage(MessageType.HashReject, payload);
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        private void StartKeepAliveTimer()
        {
            var interval = TimeSpan.FromSeconds(PeerConstants.KeepAliveIntervalSeconds);
            _keepAliveTimer = new Timer(async _ => await SendKeepAliveAsync(), null, interval, interval);
        }

        private Task SendKeepAliveAsync()
        {
            if (!IsConnected)
                return Task.CompletedTask;

            try
            {
                // Enqueue a keep-alive sentinel (KeepAlive message type) to the control channel
                // The flush loop handles writing the 4-byte zero-length prefix
                _controlSendChannel.Writer.TryWrite(KeepAliveSentinel);
                LogSentKeepAlive(_logger, PeerInfo.EndPoint);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to enqueue keep-alive for {Peer}", PeerInfo.EndPoint);
            }

            return Task.CompletedTask;
        }

        // Sentinel message to represent a keep-alive in the channel
        private static readonly PeerMessage KeepAliveSentinel = new PeerMessage(MessageType.KeepAlive);

        /// <summary>
        /// Background flush loop that drains both send channels, batches messages into a shared buffer,
        /// and writes to the transport. Control messages are drained first for priority.
        /// Follows libtorrent's cork/uncork pattern for coalescing small messages.
        /// </summary>
        private async Task SendFlushLoopAsync(CancellationToken cancellationToken)
        {
            const int BatchBufferSize = 65536; // 64KB batch buffer
            byte[] batchBuffer = ArrayPool<byte>.Shared.Rent(BatchBufferSize);

            try
            {
                var controlReader = _controlSendChannel.Reader;
                var dataReader = _dataSendChannel.Reader;

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Wait for at least one message in either channel
                    if (!controlReader.TryPeek(out _) && !dataReader.TryPeek(out _))
                    {
                        // Wait for either channel to have data, using a linked CTS
                        // to cancel the losing wait and prevent task accumulation
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        try
                        {
                            var controlWait = controlReader.WaitToReadAsync(cts.Token).AsTask();
                            var dataWait = dataReader.WaitToReadAsync(cts.Token).AsTask();
                            await Task.WhenAny(controlWait, dataWait).ConfigureAwait(false);
                            cts.Cancel(); // Cancel the other wait to release its registration
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                        finally
                        {
                            cts.Dispose();
                        }
                    }

                    // Drain and batch: control messages first (priority), then data
                    int offset = 0;
                    int messageCount = 0;

                    // Drain control channel first (choke/unchoke/interested/have/cancel/keep-alive etc.)
                    while (controlReader.TryRead(out var controlMsg))
                    {
                        if (controlMsg.Type == MessageType.KeepAlive)
                        {
                            // Keep-alive is just 4 zero bytes (no message ID)
                            if (offset + 4 > BatchBufferSize && offset > 0)
                            {
                                // Flush current batch first
                                await FlushBatchAsync(batchBuffer, offset, messageCount, cancellationToken).ConfigureAwait(false);
                                offset = 0;
                                messageCount = 0;
                            }
                            BinaryPrimitives.WriteInt32BigEndian(batchBuffer.AsSpan(offset, 4), 0);
                            offset += 4;
                            messageCount++;
                            continue;
                        }

                        int msgSize = controlMsg.TotalSize;

                        // If this single message exceeds buffer, flush what we have then write directly
                        if (msgSize > BatchBufferSize)
                        {
                            if (offset > 0)
                            {
                                await FlushBatchAsync(batchBuffer, offset, messageCount, cancellationToken).ConfigureAwait(false);
                                offset = 0;
                                messageCount = 0;
                            }
                            await WriteOversizedMessageAsync(controlMsg, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        // Flush if adding this message would overflow the buffer
                        if (offset + msgSize > BatchBufferSize && offset > 0)
                        {
                            await FlushBatchAsync(batchBuffer, offset, messageCount, cancellationToken).ConfigureAwait(false);
                            offset = 0;
                            messageCount = 0;
                        }

                        offset += controlMsg.WriteTo(batchBuffer, offset);
                        messageCount++;
                    }

                    // Then drain data channel (piece messages — already quota-checked)
                    while (dataReader.TryRead(out var dataMsg))
                    {
                        int msgSize = dataMsg.TotalSize;

                        // Oversized piece message — flush batch and write directly
                        if (msgSize > BatchBufferSize)
                        {
                            if (offset > 0)
                            {
                                await FlushBatchAsync(batchBuffer, offset, messageCount, cancellationToken).ConfigureAwait(false);
                                offset = 0;
                                messageCount = 0;
                            }
                            await WriteOversizedMessageAsync(dataMsg, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        // Flush if adding this message would overflow the buffer
                        if (offset + msgSize > BatchBufferSize && offset > 0)
                        {
                            await FlushBatchAsync(batchBuffer, offset, messageCount, cancellationToken).ConfigureAwait(false);
                            offset = 0;
                            messageCount = 0;
                        }

                        offset += dataMsg.WriteTo(batchBuffer, offset);
                        messageCount++;
                    }

                    // Flush remaining batch
                    if (offset > 0)
                    {
                        await FlushBatchAsync(batchBuffer, offset, messageCount, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException)
            {
                _logger.LogDebug("Send flush loop connection closed for {Peer}: {Message}", PeerInfo.EndPoint, ex.Message);
                await HandleConnectionLostAsync("Send error", ex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in send flush loop for {Peer}", PeerInfo.EndPoint);
                await HandleConnectionLostAsync("Send flush loop error", ex).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(batchBuffer);
            }
        }

        /// <summary>
        /// Flushes the batch buffer to the transport and tracks statistics.
        /// </summary>
        private async Task FlushBatchAsync(byte[] buffer, int length, int messageCount, CancellationToken cancellationToken)
        {
            await _transport.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

            Interlocked.Add(ref _bytesUploaded, length);
            _onBytesUploaded?.Invoke(this, length);

            if (messageCount > 1)
            {
                LogSentBatchedMessages(_logger, messageCount, length, PeerInfo.EndPoint);
            }
        }

        /// <summary>
        /// Writes an oversized message (larger than batch buffer) directly to transport.
        /// </summary>
        private async Task WriteOversizedMessageAsync(PeerMessage message, CancellationToken cancellationToken)
        {
            byte[] messageBytes = message.ToBytes();
            await _transport.WriteAsync(messageBytes.AsMemory(), cancellationToken).ConfigureAwait(false);

            int bytesWritten = messageBytes.Length;
            Interlocked.Add(ref _bytesUploaded, bytesWritten);
            _onBytesUploaded?.Invoke(this, bytesWritten);

            LogSentMessage(_logger, message.Type, PeerInfo.EndPoint);
        }

        public async Task HandleConnectionLostAsync(string reason, Exception exception = null)
        {
            if (!_isConnected)
                return;

            _isConnected = false;

            // Cancel any pending bandwidth requests
            _bandwidthLimiter?.CancelRequests(this);

            if (exception != null)
            {
                LogConnectionLostWithException(_logger, exception, PeerInfo.EndPoint, reason);
            }
            else
            {
                LogConnectionLost(_logger, PeerInfo.EndPoint, reason);
            }

            ConnectionLost?.Invoke(this, new PeerConnectionLostEventArgs(reason, exception));

            await Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _logger.LogDebug("Disconnecting from {Peer}", PeerInfo.EndPoint);

            _isConnected = false;

            // Signal receive loop to exit immediately
            try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }

            _keepAliveTimer?.Dispose();
            _transport?.Dispose();

            // Don't wait for receive task — socket close causes it to exit naturally.
            // Matches libtorrent's immediate disconnect pattern.
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposeCts.IsCancellationRequested)
                return;

            // Complete send channels first (signal no more input)
            _controlSendChannel.Writer.TryComplete();
            _dataSendChannel.Writer.TryComplete();

            // Give flush loop time to drain remaining messages
            try { _sendFlushTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

            // Then cancel to force-stop if drain didn't finish
            _disposeCts.Cancel();

            // Cancel bandwidth requests
            _bandwidthLimiter?.CancelRequests(this);

            _keepAliveTimer?.Dispose();
            _extensionManager?.Dispose();
            _downloadQuotaSignal?.Dispose();
            _uploadQuotaSignal?.Dispose();
            _transport?.Dispose();
            _disposeCts?.Dispose();
        }

        // --- Source-generated logging (zero allocation when level disabled) ---

        [LoggerMessage(Level = LogLevel.Trace, Message = "Sent {MessageType} to {Peer}")]
        private static partial void LogSentMessage(ILogger logger, object messageType, object peer);

        [LoggerMessage(Level = LogLevel.Trace, Message = "Sent {Count} batched messages ({Size} bytes) to {Peer}")]
        private static partial void LogSentBatchedMessages(ILogger logger, int count, int size, object peer);

        [LoggerMessage(Level = LogLevel.Trace, Message = "Received keep-alive from {Peer}")]
        private static partial void LogReceivedKeepAlive(ILogger logger, object peer);

        [LoggerMessage(Level = LogLevel.Trace, Message = "Received {MessageType} from {Peer}")]
        private static partial void LogReceivedMessage(ILogger logger, object messageType, object peer);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Choked by {Peer}")]
        private static partial void LogChokedBy(ILogger logger, object peer);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Unchoked by {Peer}")]
        private static partial void LogUnchokedBy(ILogger logger, object peer);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} is interested")]
        private static partial void LogPeerIsInterested(ILogger logger, object peer);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} is not interested")]
        private static partial void LogPeerIsNotInterested(ILogger logger, object peer);

        [LoggerMessage(Level = LogLevel.Trace, Message = "Peer {Peer} has piece {PieceIndex}")]
        private static partial void LogPeerHasPiece(ILogger logger, object peer, int pieceIndex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Set interested={Interested} to {Peer}")]
        private static partial void LogSetInterested(ILogger logger, bool interested, object peer);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Set choking={Choking} to {Peer}")]
        private static partial void LogSetChoking(ILogger logger, bool choking, object peer);

        [LoggerMessage(Level = LogLevel.Trace, Message = "Sent keep-alive to {Peer}")]
        private static partial void LogSentKeepAlive(ILogger logger, object peer);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Connection lost with {Peer}: {Reason}")]
        private static partial void LogConnectionLost(ILogger logger, object peer, string reason);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Connection lost with {Peer}: {Reason}")]
        private static partial void LogConnectionLostWithException(ILogger logger, Exception exception, object peer, string reason);
    }
}