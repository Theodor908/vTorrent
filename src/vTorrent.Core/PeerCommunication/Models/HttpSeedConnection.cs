using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Settings;
using vTorrent.Core.PeerCommunication.Events;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>
/// BEP 17 (Hoffman-style) HTTP seed connection.
/// Unlike BEP 19, the server is piece-aware: requests include info_hash, piece index,
/// and byte ranges. The server handles file-to-piece mapping internally.
/// </summary>
public class HttpSeedConnection : IPeerConnection
{
    private readonly string _url;
    private readonly TorrentInfo _torrentInfo;
    private readonly byte[] _infoHash;
    private readonly HttpClient _httpClient;
    private readonly WebSeedSettings _settings;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly byte[] _peerId;
    private readonly byte[] _peerBitfield;
    private readonly PeerInfo _peerInfo;
    private readonly SemaphoreSlim _pipelineSemaphore;
    private readonly Action<IPeerConnection, int>? _onBytesDownloaded;
    private long _bytesDownloaded;
    private bool _isConnected;
    private bool _disposed;

    public HttpSeedConnection(
        string url,
        TorrentInfo torrentInfo,
        byte[] infoHash,
        HttpClient httpClient,
        WebSeedSettings settings,
        IPAddress resolvedAddress,
        ILogger logger,
        Action<IPeerConnection, int>? onBytesDownloaded = null)
    {
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
        _infoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onBytesDownloaded = onBytesDownloaded;

        _pipelineSemaphore = new SemaphoreSlim(5, 5);

        // Synthetic PeerId: "-HS0017-" + first 12 bytes of URL hash
        _peerId = new byte[20];
        var prefix = System.Text.Encoding.ASCII.GetBytes("-HS0017-");
        Buffer.BlockCopy(prefix, 0, _peerId, 0, 8);
        var urlHash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));
        Buffer.BlockCopy(urlHash, 0, _peerId, 8, 12);

        // Full bitfield (all pieces available)
        int pieceCount = torrentInfo.PieceCount;
        int byteCount = (pieceCount + 7) / 8;
        _peerBitfield = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
            _peerBitfield[i] = 0xFF;
        int trailingBits = byteCount * 8 - pieceCount;
        if (trailingBits > 0)
            _peerBitfield[byteCount - 1] = (byte)(0xFF << trailingBits);

        var uri = new Uri(url);
        int port = uri.Scheme == "https" ? 443 : 80;
        _peerInfo = new PeerInfo(resolvedAddress, port, _peerId, "httpseed");

        _isConnected = true;
        ConnectedAt = DateTime.UtcNow;

        _logger.LogDebug("HttpSeedConnection created for {Url} ({Ip})", url, resolvedAddress);
    }

    #region IPeerConnection Properties

    public PeerInfo PeerInfo => _peerInfo;
    public string EndpointString => _peerInfo?.EndPoint?.ToString() ?? "";
    public byte[] PeerId => _peerId;
    public bool IsChoked => false;
    public bool IsInterested => true;
    public bool IsChoking => true;
    public bool PeerIsInterested => false;
    public bool IsConnected => _isConnected;
    public byte[] PeerBitfield { get => _peerBitfield; set { } }
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);
    public long BytesUploaded => 0;
    public DateTime ConnectedAt { get; }
    public double RoundTripTimeMs => 500;
    public bool IsSnubbed { get; set; }
    public bool IsSeed => true;
    public string? ClientName => new Uri(_url).Host;
    public bool IsEncrypted => _url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    public bool IsIncoming => false;
    public bool PeerSupportsFastExtension => false;
    public int? RemoteRequestQueueSize => null;
    /// <inheritdoc />
    public bool IsUtp => false;

    #endregion

    #region IPeerConnection Events

    public event EventHandler<PeerStateChangedEventArgs>? StateChanged;
    public event EventHandler<PeerMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<PeerConnectionLostEventArgs>? ConnectionLost;

    #endregion

    #region Block Request — Core HTTP Logic

    /// <summary>Max concurrent HTTP requests (pipeline semaphore slots).</summary>
    public int MaxConcurrentRequests => 5;

    public Task RequestBlockAsync(int pieceIndex, int begin, int length,
        CancellationToken cancellationToken = default)
    {
        return RequestBlocksBatchAsync(
            new[] { (pieceIndex, begin, length) }, cancellationToken);
    }

    public Task RequestBlocksBatchAsync(
        IReadOnlyList<(int pieceIndex, int begin, int length)> blocks,
        CancellationToken cancellationToken = default)
    {
        if (!_isConnected || _disposed || blocks.Count == 0) return Task.CompletedTask;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCts.Token);

        var ranges = CoalesceBlocks(blocks);
        _ = DownloadRangesInBackgroundAsync(ranges, linked);
        return Task.CompletedTask;
    }

    private async Task DownloadRangesInBackgroundAsync(
        List<CoalescedRange> ranges, CancellationTokenSource linked)
    {
        try
        {
            foreach (var range in ranges)
            {
                await _pipelineSemaphore.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await DownloadRangeAsync(range.pieceIndex, range.begin,
                        range.totalLength, range.blocks, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    try { _pipelineSemaphore.Release(); }
                    catch (ObjectDisposedException) { /* connection disposed mid-flight */ }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ObjectDisposedException) { /* connection disposed mid-flight */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background HTTP seed download failed for {Url}", _url);
        }
        finally
        {
            linked.Dispose();
        }
    }

    private string BuildPieceUrl(int piece, int begin, int length)
    {
        var infoHashStr = Uri.EscapeDataString(
            System.Text.Encoding.Latin1.GetString(_infoHash));
        return $"{_url}?info_hash={infoHashStr}&piece={piece}&ranges={begin}-{begin + length - 1}";
    }

    private async Task DownloadRangeAsync(int basePiece, int baseBegin, int totalLength,
        List<(int pieceIndex, int begin, int length)> blocks, CancellationToken ct)
    {
        try
        {
            var url = BuildPieceUrl(basePiece, baseBegin, totalLength);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(_settings.TimeoutSeconds));
            using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedTimeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                // BEP 17: 503 body is ASCII integer for retry delay
                var body = await response.Content.ReadAsStringAsync(linkedTimeout.Token)
                    .ConfigureAwait(false);
                int retrySeconds = int.TryParse(body.Trim(), out var parsed)
                    ? parsed : _settings.WaitRetrySeconds;
                _logger.LogDebug("HttpSeed {Url} returned 503, retry after {Seconds}s",
                    _url, retrySeconds);
                ConnectionLost?.Invoke(this,
                    new PeerConnectionLostEventArgs($"503 Retry-After {retrySeconds}s"));
                _isConnected = false;
                return;
            }

            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync(linkedTimeout.Token)
                .ConfigureAwait(false);

            // Split back into individual blocks
            int bytesProcessed = 0;
            foreach (var block in blocks)
            {
                if (bytesProcessed + block.length > data.Length) break;
                var blockData = new byte[block.length];
                Buffer.BlockCopy(data, bytesProcessed, blockData, 0, block.length);

                var pieceMsg = PeerMessage.CreatePiece(block.pieceIndex, block.begin, blockData);
                MessageReceived?.Invoke(this, new PeerMessageReceivedEventArgs(pieceMsg));
                Interlocked.Add(ref _bytesDownloaded, block.length);
                _onBytesDownloaded?.Invoke(this, block.length);
                bytesProcessed += block.length;
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested) { }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "HttpSeed request failed for {Url}", _url);
            ConnectionLost?.Invoke(this, new PeerConnectionLostEventArgs(ex.Message, ex));
            _isConnected = false;
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("HttpSeed request timed out for {Url}", _url);
            ConnectionLost?.Invoke(this, new PeerConnectionLostEventArgs("Timeout"));
            _isConnected = false;
        }
    }

    #endregion

    #region Request Coalescing

    private record struct CoalescedRange(
        int pieceIndex, int begin, int totalLength,
        List<(int pieceIndex, int begin, int length)> blocks);

    private List<CoalescedRange> CoalesceBlocks(
        IReadOnlyList<(int pieceIndex, int begin, int length)> blocks)
    {
        var ranges = new List<CoalescedRange>();
        if (blocks.Count == 0) return ranges;

        var currentBlocks = new List<(int pieceIndex, int begin, int length)> { blocks[0] };
        int currentPiece = blocks[0].pieceIndex;
        int currentBegin = blocks[0].begin;
        int currentLength = blocks[0].length;

        for (int i = 1; i < blocks.Count; i++)
        {
            var block = blocks[i];
            long prevEnd = (long)currentPiece * _torrentInfo.PieceLength + currentBegin + currentLength;
            long nextStart = (long)block.pieceIndex * _torrentInfo.PieceLength + block.begin;

            if (nextStart == prevEnd && currentLength + block.length <= _settings.MaxRequestBytes)
            {
                currentLength += block.length;
                currentBlocks.Add(block);
            }
            else
            {
                ranges.Add(new CoalescedRange(currentPiece, currentBegin, currentLength, currentBlocks));
                currentBlocks = new List<(int pieceIndex, int begin, int length)> { block };
                currentPiece = block.pieceIndex;
                currentBegin = block.begin;
                currentLength = block.length;
            }
        }

        ranges.Add(new CoalescedRange(currentPiece, currentBegin, currentLength, currentBlocks));
        return ranges;
    }

    #endregion

    #region IPeerConnection No-ops

    public Task ConnectAsync(byte[] infoHash, CancellationToken ct = default, byte[]? preReadHandshake = null) => Task.CompletedTask;
    public Task SendMessageAsync(PeerMessage message, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendMessagesAsync(IReadOnlyList<PeerMessage> messages, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PeerMessage> ReceiveMessageAsync(CancellationToken ct = default) => Task.FromResult(default(PeerMessage)!);
    public Task SendBitfieldAsync(byte[] bitfield, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetInterestedAsync(bool interested, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetChokingAsync(bool choking, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendBlockAsync(int pieceIndex, int begin, byte[] block, CancellationToken ct = default) => Task.CompletedTask;
    public Task CancelBlockAsync(int pieceIndex, int begin, int length, CancellationToken ct = default) => Task.CompletedTask;
    public Task AnnounceHaveAsync(int pieceIndex, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendHashRequestAsync(HashRequestMessage msg, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendHashesAsync(HashesMessage msg, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendHashRejectAsync(HashRejectMessage msg, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendHaveNoneAsync(int totalPieces, CancellationToken cancellationToken = default) => Task.CompletedTask;

    #endregion

    #region Disconnect + Dispose

    public Task DisconnectAsync()
    {
        if (!_isConnected) return Task.CompletedTask;
        _isConnected = false;
        _disposeCts.Cancel();
        ConnectionLost?.Invoke(this,
            new PeerConnectionLostEventArgs("Disconnected"));
        StateChanged?.Invoke(this,
            new PeerStateChangedEventArgs(false, true, true, false));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isConnected = false;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _pipelineSemaphore.Dispose();
    }

    #endregion

    /// <summary>The original URL this HTTP seed was created from.</summary>
    public string Url => _url;
}
