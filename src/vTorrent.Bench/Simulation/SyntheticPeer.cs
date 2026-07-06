using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Models;
using vTorrent.Core.PeerCommunication.Events;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Bench.Simulation;

/// <summary>
/// Simulates a BitTorrent peer with configurable rate limiting, RTT delay,
/// choke behavior, and packet loss. Implements IPeerConnection so the real
/// DownloadCoordinator can dispatch requests to it through the normal interface.
/// </summary>
public sealed class SyntheticPeer : IPeerConnection
{
    private readonly SyntheticTorrent _torrent;
    private readonly int _maxUploadRate;
    private readonly int _roundTripTimeMs;
    private readonly float _chokeProbability;
    private readonly int _chokeIntervalSec;
    private readonly float _packetLossPercent;
    private readonly bool _bandwidthFluctuation;
    private readonly float _fluctuationAmplitude;
    private readonly Random _random;

    // Token bucket state
    private double _tokens;
    private DateTime _lastRefill;
    private readonly object _tokenLock = new();

    // Choke timer
    private CancellationTokenSource? _chokeCts;
    private Task? _chokeLoopTask;

    private bool _isChoked;
    private bool _disposed;

    public SyntheticPeer(
        int id,
        SyntheticTorrent torrent,
        int maxUploadRate,
        int roundTripTimeMs,
        float chokeProbability,
        int chokeIntervalSec,
        float packetLossPercent,
        float bitfieldFill,
        bool bandwidthFluctuation,
        float fluctuationAmplitude)
    {
        _torrent = torrent;
        _maxUploadRate = maxUploadRate;
        _roundTripTimeMs = roundTripTimeMs;
        _chokeProbability = chokeProbability;
        _chokeIntervalSec = chokeIntervalSec;
        _packetLossPercent = packetLossPercent;
        _bandwidthFluctuation = bandwidthFluctuation;
        _fluctuationAmplitude = fluctuationAmplitude;
        _random = new Random(id * 7919);

        // Token bucket starts full
        _tokens = maxUploadRate;
        _lastRefill = DateTime.UtcNow;

        // Build peer identity
        var peerId = new byte[20];
        peerId[0] = (byte)'-';
        peerId[1] = (byte)'S';
        peerId[2] = (byte)'P';
        peerId[3] = (byte)'-';
        var idBytes = BitConverter.GetBytes(id);
        Buffer.BlockCopy(idBytes, 0, peerId, 4, Math.Min(idBytes.Length, 16));

        PeerInfo = new PeerInfo(IPAddress.Loopback, 6881 + id, peerId, "bench");
        EndpointString = $"127.0.0.1:{6881 + id}";
        PeerId = peerId;
        ConnectedAt = DateTime.UtcNow;

        // Generate bitfield (MSB-first: piece i = bit (7 - (i % 8)) of byte (i / 8))
        int totalPieces = torrent.Info.Pieces.Count;
        int byteCount = (totalPieces + 7) / 8;
        PeerBitfield = new byte[byteCount];

        bool isSeed = true;
        for (int i = 0; i < totalPieces; i++)
        {
            bool hasPiece = _random.NextDouble() < bitfieldFill;
            if (hasPiece)
            {
                PeerBitfield[i / 8] |= (byte)(1 << (7 - (i % 8)));
            }
            else
            {
                isSeed = false;
            }
        }
        IsSeed = isSeed;

        // Start unchoked
        _isChoked = false;
    }

    #region IPeerConnection Properties

    public PeerInfo PeerInfo { get; }
    public string EndpointString { get; }
    public byte[] PeerId { get; }
    public bool IsChoked => _isChoked;
    public bool IsInterested => true;
    public bool IsChoking => false;
    public bool PeerIsInterested => false;
    public bool IsConnected => !_disposed;
    public byte[] PeerBitfield { get; set; }
    public long BytesDownloaded => 0;
    public long BytesUploaded => 0;
    public DateTime ConnectedAt { get; }
    public double RoundTripTimeMs => _roundTripTimeMs;
    public bool IsSnubbed { get; set; }
    public bool IsSeed { get; }
    public string? ClientName => "SyntheticPeer";
    public bool IsEncrypted => false;
    public bool IsIncoming => false;
    public bool IsUtp => false;
    public bool PeerSupportsFastExtension => false;
    public int? RemoteRequestQueueSize => 250;

    #endregion

    #region Events

    public event EventHandler<PeerStateChangedEventArgs>? StateChanged;
    public event EventHandler<PeerMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<PeerConnectionLostEventArgs>? ConnectionLost;

    #endregion

    #region Connection Lifecycle

    public Task ConnectAsync(byte[] infoHash, CancellationToken cancellationToken = default, byte[]? preReadHandshake = null)
    {
        // Start the choke timer loop
        if (_chokeIntervalSec > 0 && _chokeProbability > 0f)
        {
            _chokeCts = new CancellationTokenSource();
            _chokeLoopTask = RunChokeLoopAsync(_chokeCts.Token);
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _chokeCts?.Cancel();
        return Task.CompletedTask;
    }

    #endregion

    #region Request Handling

    public Task RequestBlockAsync(int pieceIndex, int begin, int length, CancellationToken cancellationToken = default)
    {
        _ = ProcessRequestAsync(pieceIndex, begin, length);
        return Task.CompletedTask;
    }

    public Task RequestBlocksBatchAsync(IReadOnlyList<(int pieceIndex, int begin, int length)> blocks, CancellationToken cancellationToken = default)
    {
        foreach (var (pieceIndex, begin, length) in blocks)
        {
            _ = ProcessRequestAsync(pieceIndex, begin, length);
        }
        return Task.CompletedTask;
    }

    private async Task ProcessRequestAsync(int pieceIndex, int begin, int length)
    {
        try
        {
            // 1. RTT delay
            if (_roundTripTimeMs > 0)
            {
                await Task.Delay(_roundTripTimeMs).ConfigureAwait(false);
            }

            // 2. Check choke — if choked, silently drop
            if (_isChoked)
                return;

            // 3. Packet loss roll
            if (_packetLossPercent > 0f)
            {
                bool lost;
                lock (_random) { lost = _random.NextDouble() < _packetLossPercent; }
                if (lost)
                    return;
            }

            // 4. Token bucket rate limiting
            await WaitForTokensAsync(length).ConfigureAwait(false);

            // 5. Generate block data and fire MessageReceived
            byte[] block = _torrent.GetBlock(pieceIndex, begin, length);
            var message = PeerMessage.CreatePiece(pieceIndex, begin, block);
            MessageReceived?.Invoke(this, new PeerMessageReceivedEventArgs(message));
        }
        catch (ObjectDisposedException)
        {
            // Peer was disposed during processing — ignore
        }
    }

    #endregion

    #region Token Bucket Rate Limiting

    private async Task WaitForTokensAsync(int needed)
    {
        while (true)
        {
            lock (_tokenLock)
            {
                RefillTokens();
                if (_tokens >= needed)
                {
                    _tokens -= needed;
                    return;
                }
            }

            // Wait a short interval and retry
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private void RefillTokens()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0)
            return;

        double rate = _maxUploadRate;

        // Apply sine wave bandwidth fluctuation
        if (_bandwidthFluctuation)
        {
            double t = (now - ConnectedAt).TotalSeconds;
            // Sine wave modulates rate: rate * (1 + amplitude * sin(2*pi*t / period))
            // Use a 10-second period for visible fluctuation
            double modulation = 1.0 + _fluctuationAmplitude * Math.Sin(2.0 * Math.PI * t / 10.0);
            rate *= Math.Max(0.1, modulation);
        }

        _tokens = Math.Min(_tokens + rate * elapsed, rate);
        _lastRefill = now;
    }

    #endregion

    #region Choke Timer Loop

    private async Task RunChokeLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_chokeIntervalSec), ct).ConfigureAwait(false);

                bool shouldChoke;
                lock (_random) { shouldChoke = _random.NextDouble() < _chokeProbability; }

                bool previousState = _isChoked;
                _isChoked = shouldChoke;

                if (_isChoked != previousState)
                {
                    // Fire appropriate message
                    var msg = _isChoked ? PeerMessage.CreateChoke() : PeerMessage.CreateUnchoke();
                    MessageReceived?.Invoke(this, new PeerMessageReceivedEventArgs(msg));

                    // Fire state changed
                    StateChanged?.Invoke(this, new PeerStateChangedEventArgs(
                        isChoked: _isChoked,
                        isInterested: IsInterested,
                        isChoking: IsChoking,
                        isPeerInterested: PeerIsInterested));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    #endregion

    #region No-Op Methods

    public Task SendMessageAsync(PeerMessage message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendMessagesAsync(IReadOnlyList<PeerMessage> messages, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<PeerMessage> ReceiveMessageAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PeerMessage(MessageType.KeepAlive));

    public Task SendBitfieldAsync(byte[] bitfield, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SetInterestedAsync(bool interested, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SetChokingAsync(bool choking, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendBlockAsync(int pieceIndex, int begin, byte[] block, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CancelBlockAsync(int pieceIndex, int begin, int length, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task AnnounceHaveAsync(int pieceIndex, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendHaveNoneAsync(int totalPieces, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendHashRequestAsync(HashRequestMessage msg, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendHashesAsync(HashesMessage msg, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendHashRejectAsync(HashRejectMessage msg, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _chokeCts?.Cancel();
        _chokeCts?.Dispose();
    }

    #endregion
}
