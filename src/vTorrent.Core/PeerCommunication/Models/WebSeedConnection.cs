using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// BEP 19 (GetRight-style) web seed connection.
/// Implements IPeerConnection so the DownloadCoordinator can dispatch block requests
/// to it like any regular BitTorrent peer. Internally translates block requests
/// into HTTP Range requests against a static file server.
/// </summary>
public class WebSeedConnection : IPeerConnection
{
    private readonly string _url;
    private readonly TorrentInfo _torrentInfo;
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

    // Precomputed file offset table for multi-file torrents
    private readonly (string path, long offset, long length)[] _fileTable;

    public WebSeedConnection(
        string url,
        TorrentInfo torrentInfo,
        HttpClient httpClient,
        WebSeedSettings settings,
        IPAddress resolvedAddress,
        ILogger logger,
        Action<IPeerConnection, int>? onBytesDownloaded = null)
    {
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onBytesDownloaded = onBytesDownloaded;

        // Pipeline: max 5 concurrent HTTP requests (libtorrent default)
        _pipelineSemaphore = new SemaphoreSlim(5, 5);

        // Synthetic PeerId: "-WS0019-" + first 12 bytes of URL hash
        _peerId = new byte[20];
        var prefix = System.Text.Encoding.ASCII.GetBytes("-WS0019-");
        Buffer.BlockCopy(prefix, 0, _peerId, 0, 8);
        var urlHash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));
        Buffer.BlockCopy(urlHash, 0, _peerId, 8, 12);

        // Full bitfield (all pieces available) — MSB-first per BitTorrent protocol
        int pieceCount = torrentInfo.PieceCount;
        int byteCount = (pieceCount + 7) / 8;
        _peerBitfield = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
            _peerBitfield[i] = 0xFF;
        // Clear trailing bits in last byte
        int trailingBits = byteCount * 8 - pieceCount;
        if (trailingBits > 0)
            _peerBitfield[byteCount - 1] = (byte)(0xFF << trailingBits);

        // PeerInfo with resolved IP
        var uri = new Uri(url);
        int port = uri.Scheme == "https" ? 443 : 80;
        _peerInfo = new PeerInfo(resolvedAddress, port, _peerId, "webseed");

        // Build file offset table for byte-range mapping
        _fileTable = BuildFileTable(torrentInfo);

        _isConnected = true;
        ConnectedAt = DateTime.UtcNow;

        _logger.LogDebug("WebSeedConnection created for {Url} ({Ip})", url, resolvedAddress);
    }

    private static (string path, long offset, long length)[] BuildFileTable(TorrentInfo info)
    {
        var files = info.Files;
        var table = new (string path, long offset, long length)[files.Count];
        long offset = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var path = string.Join("/", file.Path);
            table[i] = (path, offset, file.Length);
            offset += file.Length;
        }
        return table;
    }

    #region IPeerConnection Properties

    public PeerInfo PeerInfo => _peerInfo;
    public string EndpointString => _peerInfo?.EndPoint?.ToString() ?? "";
    public byte[] PeerId => _peerId;
    public bool IsChoked => false;          // Always unchoked
    public bool IsInterested => true;       // Always interested
    public bool IsChoking => true;          // We don't upload to web seeds
    public bool PeerIsInterested => false;  // Web seeds don't want our data
    public bool IsConnected => _isConnected;
    public byte[] PeerBitfield { get => _peerBitfield; set { } } // Immutable full bitfield
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);
    public long BytesUploaded => 0;
    public DateTime ConnectedAt { get; }
    // HTTP round-trip is typically 200-1000ms; 500ms prevents pipeline overestimation.
    // RTT=0 would cause CalculateOptimalPipelineDepth to use the 3s default queue time,
    // giving web seeds a pipeline of hundreds of blocks they can never fill.
    public double RoundTripTimeMs => 500;
    public bool IsSnubbed { get; set; }
    public bool IsSeed => true;             // Web seeds always have all pieces
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

        // Group blocks into contiguous ranges for coalescing
        var ranges = CoalesceBlocks(blocks);

        // Fire-and-forget: HTTP downloads run in background, deliver via MessageReceived.
        // This mirrors libtorrent's write_request() which only queues the HTTP request
        // into the send buffer — it never blocks the main loop on HTTP I/O.
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
                    // Guard against ObjectDisposedException: if the connection is banned/disposed
                    // while this background task is running (e.g., WebSeedManager.OnPieceFailed
                    // calls Dispose()), the semaphore is already disposed.
                    try { _pipelineSemaphore.Release(); }
                    catch (ObjectDisposedException) { /* connection disposed mid-flight */ }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ObjectDisposedException) { /* connection disposed mid-flight */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background web seed download failed for {Url}", _url);
        }
        finally
        {
            linked.Dispose();
        }
    }

    private async Task DownloadRangeAsync(int basePiece, int baseBegin, int totalLength,
        List<(int pieceIndex, int begin, int length)> blocks, CancellationToken ct)
    {
        long globalOffset = (long)basePiece * _torrentInfo.PieceLength + baseBegin;

        try
        {
            // Determine which file(s) this range spans
            var fileRequests = MapToFileRequests(globalOffset, totalLength);

            int bytesProcessed = 0;

            foreach (var fileReq in fileRequests)
            {
                var url = BuildFileUrl(fileReq.filePath);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(
                    fileReq.fileOffset, fileReq.fileOffset + fileReq.length - 1);

                using var cts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(_settings.TimeoutSeconds));
                using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, linkedTimeout.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    // BEP 19: 503 — respect Retry-After header
                    HandleServiceUnavailable(response);
                    return;
                }

                response.EnsureSuccessStatusCode();

                var data = await response.Content.ReadAsByteArrayAsync(linkedTimeout.Token)
                    .ConfigureAwait(false);

                // Deliver data as individual block messages
                int dataOffset = 0;
                while (dataOffset < data.Length && bytesProcessed < totalLength)
                {
                    var block = FindBlockForOffset(blocks, bytesProcessed);
                    if (block == null) break;

                    int blockStart = BlockStartOffset(blocks, block.Value);
                    int alreadyInBlock = bytesProcessed - blockStart;
                    int remaining = block.Value.length - alreadyInBlock;
                    int toCopy = Math.Min(remaining, data.Length - dataOffset);

                    if (alreadyInBlock == 0 && toCopy == block.Value.length)
                    {
                        // Full block available — deliver directly
                        var blockData = new byte[block.Value.length];
                        Buffer.BlockCopy(data, dataOffset, blockData, 0, block.Value.length);

                        var pieceMsg = PeerMessage.CreatePiece(
                            block.Value.pieceIndex, block.Value.begin, blockData);
                        MessageReceived?.Invoke(this,
                            new PeerMessageReceivedEventArgs(pieceMsg));

                        Interlocked.Add(ref _bytesDownloaded, block.Value.length);
                        _onBytesDownloaded?.Invoke(this, block.Value.length);
                    }

                    dataOffset += toCopy;
                    bytesProcessed += toCopy;
                }
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            // Shutdown — expected
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "WebSeed HTTP request failed for {Url}", _url);
            ConnectionLost?.Invoke(this,
                new PeerConnectionLostEventArgs(ex.Message, ex));
            _isConnected = false;
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("WebSeed request timed out for {Url}", _url);
            ConnectionLost?.Invoke(this,
                new PeerConnectionLostEventArgs("Timeout"));
            _isConnected = false;
        }
    }

    #endregion

    #region URL Construction (BEP 19)

    private string BuildFileUrl(string filePath)
    {
        // Single-file: URL as-is, or URL + filename if trailing slash
        if (_fileTable.Length == 1)
        {
            if (_url.EndsWith('/'))
                return _url + Uri.EscapeDataString(filePath);
            return _url;
        }

        // Multi-file: url + "/" + torrentName + "/" + filePath
        var baseUrl = _url.TrimEnd('/');
        var name = Uri.EscapeDataString(_torrentInfo.Name);
        var escapedPath = string.Join("/",
            filePath.Split('/').Select(Uri.EscapeDataString));
        return $"{baseUrl}/{name}/{escapedPath}";
    }

    #endregion

    #region Byte Offset Mapping

    private List<(string filePath, long fileOffset, int length)> MapToFileRequests(
        long globalOffset, int totalLength)
    {
        var requests = new List<(string filePath, long fileOffset, int length)>();
        int remaining = totalLength;
        long currentOffset = globalOffset;

        foreach (var (path, fileStart, fileLength) in _fileTable)
        {
            if (remaining <= 0) break;

            long fileEnd = fileStart + fileLength;
            if (currentOffset >= fileEnd) continue;
            if (currentOffset < fileStart) currentOffset = fileStart;

            long offsetInFile = currentOffset - fileStart;
            int canRead = (int)Math.Min(remaining, fileLength - offsetInFile);

            requests.Add((path, offsetInFile, canRead));
            currentOffset += canRead;
            remaining -= canRead;
        }

        return requests;
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
                // Contiguous and within max request size — coalesce
                currentLength += block.length;
                currentBlocks.Add(block);
            }
            else
            {
                // Gap or max size reached — start new range
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

    private (int pieceIndex, int begin, int length)? FindBlockForOffset(
        List<(int pieceIndex, int begin, int length)> blocks, int bytesProcessed)
    {
        int offset = 0;
        foreach (var block in blocks)
        {
            if (bytesProcessed >= offset && bytesProcessed < offset + block.length)
                return block;
            offset += block.length;
        }
        return null;
    }

    private int BlockStartOffset(
        List<(int pieceIndex, int begin, int length)> blocks,
        (int pieceIndex, int begin, int length) target)
    {
        int offset = 0;
        foreach (var block in blocks)
        {
            if (block == target) return offset;
            offset += block.length;
        }
        return offset;
    }

    #endregion

    #region Error Handling

    private void HandleServiceUnavailable(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds
            ?? _settings.WaitRetrySeconds;
        _logger.LogDebug("WebSeed {Url} returned 503, retrying after {Seconds}s",
            _url, retryAfter);
        // Manager handles retry scheduling via OnConnectionError
        ConnectionLost?.Invoke(this,
            new PeerConnectionLostEventArgs($"503 Retry-After {retryAfter}s"));
        _isConnected = false;
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

    /// <summary>The original URL this web seed was created from.</summary>
    public string Url => _url;
}
