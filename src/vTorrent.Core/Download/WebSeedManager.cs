using System;

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

using vTorrent.Core.PeerCommunication.Models;

using vTorrent.Abstractions.Settings;
using vTorrent.Core.Engine;

namespace vTorrent.Core.Download;

/// <summary>

/// Manages web seed connections for a single torrent.

/// Created in Phase 2, started in Phase 5, disposed on engine stop.

/// </summary>

public class WebSeedManager : IDisposable

{

    private readonly TorrentInfo _torrentInfo;

    private readonly byte[] _infoHash;

    private readonly WebSeedSettings _settings;

    private readonly HttpClient _httpClient;

    private readonly ILogger _logger;

    private readonly IStatisticsTracker? _statisticsTracker;

    private readonly List<WebSeedState> _seeds = new();

    private readonly List<IPeerConnection> _activeConnections = new();

    private readonly object _lock = new();

    private int _bep17Count;

    private int _bep19Count;

    private CancellationTokenSource? _retryCts;

    private bool _disposed;

    /// <summary>

    /// Relayed from individual web seed connections so PeerMessageRouter

    /// can dispatch piece data through the same handler pipeline as regular peers.

    /// Same relay pattern as PeerManager.OnPeerMessageReceived (PeerManager.cs:918-924).

    /// </summary>

    public event EventHandler<PeerMessageEventArgs>? MessageReceived;

    private void OnWebSeedMessageReceived(object? sender, PeerMessageReceivedEventArgs e)

    {

        if (sender is IPeerConnection peer)

            MessageReceived?.Invoke(this, new PeerMessageEventArgs(peer, e.Message));

    }

    public WebSeedManager(

        IReadOnlyList<string>? urlSeeds,

        IReadOnlyList<string>? httpSeeds,

        TorrentInfo torrentInfo,

        byte[] infoHash,

        WebSeedSettings settings,

        HttpClient httpClient,

        ILogger<WebSeedManager> logger,

        IStatisticsTracker? statisticsTracker = null)

    {

        _torrentInfo = torrentInfo;

        _infoHash = infoHash;

        _settings = settings;

        _httpClient = httpClient;

        _logger = logger;

        _statisticsTracker = statisticsTracker;

        if (urlSeeds != null)

        {

            foreach (var url in urlSeeds)

            {

                _seeds.Add(new WebSeedState { Url = url, Type = WebSeedType.BEP19 });

                _bep19Count++;

            }

        }

        if (httpSeeds != null)

        {

            foreach (var url in httpSeeds)

            {

                _seeds.Add(new WebSeedState { Url = url, Type = WebSeedType.BEP17 });

                _bep17Count++;

            }

        }

        _logger.LogDebug("WebSeedManager created with {Count} seeds ({Bep19} BEP19, {Bep17} BEP17)",

            _seeds.Count, _bep19Count, _bep17Count);

    }

    /// <summary>Active connections for the download coordinator.</summary>

    public IReadOnlyList<IPeerConnection> ActiveConnections

    {

        get { lock (_lock) return _activeConnections.ToList(); }

    }

    /// <summary>All seed states for UI display.</summary>

    public IReadOnlyList<WebSeedState> AllSeeds

    {

        get { lock (_lock) return _seeds.ToList(); }

    }

    /// <summary>Add a web seed at runtime. Returns false if URL already exists or is invalid.</summary>
    public bool AddSeed(string url, WebSeedType type)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return false;

        lock (_lock)
        {
            if (_seeds.Any(s => s.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                return false;

            _seeds.Add(new WebSeedState { Url = url, Type = type });
            if (type == WebSeedType.BEP17) _bep17Count++;
            else _bep19Count++;
        }

        _logger.LogDebug("Added web seed at runtime: {Url} ({Type})", url, type);
        return true;
    }

    /// <summary>Remove a web seed at runtime. Does not disconnect active connections.</summary>
    public bool RemoveSeed(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        lock (_lock)
        {
            var seed = _seeds.FirstOrDefault(s => s.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (seed == null) return false;

            _seeds.Remove(seed);
            if (seed.Type == WebSeedType.BEP17) _bep17Count--;
            else _bep19Count--;
        }

        _logger.LogDebug("Removed web seed at runtime: {Url}", url);
        return true;
    }

    public int ActiveCount

    {

        get { lock (_lock) return _activeConnections.Count; }

    }

    /// <summary>

    /// Resolve DNS and connect up to MaxConnectionsPerTorrent web seeds.

    /// Called during Phase 5 (Download).

    /// </summary>

    public async Task StartAsync(CancellationToken ct)

    {

        if (_seeds.Count == 0) return;

        _retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        int connected = 0;

        foreach (var seed in _seeds)

        {

            if (connected >= _settings.MaxConnectionsPerTorrent) break;

            if (seed.Status != WebSeedStatus.Idle) continue;

            try

            {

                var uri = new Uri(seed.Url);

                var addresses = await Dns.GetHostAddressesAsync(

                    uri.Host, ct).ConfigureAwait(false);

                if (addresses.Length == 0)

                {

                    seed.Status = WebSeedStatus.Banned;

                    _logger.LogWarning("DNS resolution failed for web seed {Url}", seed.Url);

                    continue;

                }

                // Wire download callback so bytes flow through TorrentStatistics

                // (same pattern as PeerConnection._onBytesDownloaded)

                Action<IPeerConnection, int>? dlCallback = _statisticsTracker != null

                    ? (peer, bytes) => _statisticsTracker.RecordDownload(peer, bytes)

                    : null;

                IPeerConnection connection;

                if (seed.Type == WebSeedType.BEP19)

                {

                    connection = new WebSeedConnection(

                        seed.Url, _torrentInfo, _httpClient, _settings,

                        addresses[0], _logger, dlCallback);

                }

                else

                {

                    connection = new HttpSeedConnection(

                        seed.Url, _torrentInfo, _infoHash, _httpClient, _settings,

                        addresses[0], _logger, dlCallback);

                }

                connection.MessageReceived += OnWebSeedMessageReceived;

                EventHandler<PeerConnectionLostEventArgs> lostHandler =
                    (s, e) => OnConnectionError(connection, e.Exception ?? new Exception(e.Reason ?? "Connection lost"));
                connection.ConnectionLost += lostHandler;
                seed.ConnectionLostHandler = lostHandler;

                _statisticsTracker?.RegisterPeer(connection);

                seed.Connection = connection;

                seed.Status = WebSeedStatus.Active;

                lock (_lock)

                    _activeConnections.Add(connection);

                connected++;

                _logger.LogDebug("Connected to web seed {Url} ({Type})",

                    seed.Url, seed.Type);

            }

            catch (Exception ex)

            {

                seed.FailureCount++;

                seed.Status = WebSeedStatus.Backoff;

                seed.NextRetryTime = DateTime.UtcNow.AddSeconds(_settings.WaitRetrySeconds);

                _logger.LogWarning(ex, "Failed to connect to web seed {Url}", seed.Url);

            }

        }

    }

    /// <summary>Called when a piece from a web seed fails hash verification.</summary>

    public void OnPieceFailed(int piece, IPeerConnection source)

    {

        lock (_lock)

        {

            var seed = _seeds.FirstOrDefault(s => s.Connection == source);

            if (seed == null || seed.Connection == null) return;

            // BanOnBadData hardcoded true
            seed.Status = WebSeedStatus.Banned;

            _activeConnections.Remove(source);

            source.MessageReceived -= OnWebSeedMessageReceived;

            if (seed.ConnectionLostHandler != null)
            {
                source.ConnectionLost -= seed.ConnectionLostHandler;
                seed.ConnectionLostHandler = null;
            }

            _statisticsTracker?.UnregisterPeer(source);

            seed.Connection = null;

            source.Dispose();

            _logger.LogWarning("Banned web seed {Url} for bad data on piece {Piece}",

                seed.Url, piece);

        }

    }

    /// <summary>Called when an HTTP request to a web seed fails.</summary>

    public void OnConnectionError(IPeerConnection conn, Exception ex)

    {

        lock (_lock)

        {

            var seed = _seeds.FirstOrDefault(s => s.Connection == conn);

            if (seed == null || seed.Connection == null) return;

            seed.FailureCount++;

            conn.MessageReceived -= OnWebSeedMessageReceived;

            if (seed.ConnectionLostHandler != null)
            {
                conn.ConnectionLost -= seed.ConnectionLostHandler;
                seed.ConnectionLostHandler = null;
            }

            _statisticsTracker?.UnregisterPeer(conn);

            seed.Connection = null;

            _activeConnections.Remove(conn);

            if (seed.FailureCount >= 5)

            {

                seed.Status = WebSeedStatus.Banned;

                _logger.LogWarning("Web seed {Url} permanently unavailable after {Count} failures",

                    seed.Url, seed.FailureCount);

            }

            else

            {

                seed.Status = WebSeedStatus.Backoff;

                seed.NextRetryTime = DateTime.UtcNow.AddSeconds(

                    _settings.WaitRetrySeconds * Math.Pow(2, seed.FailureCount - 1));

                _logger.LogDebug("Web seed {Url} backing off until {Time}",

                    seed.Url, seed.NextRetryTime);

            }

        }

    }

    /// <summary>

    /// Called periodically from the download loop's timeout check (~500ms).

    /// Promotes seeds from Backoff -> Idle and reconnects if under the connection limit.

    /// </summary>

    public async Task TryRetryBackoffSeedsAsync(CancellationToken ct)

    {

        if (_disposed) return;

        List<WebSeedState> retriable;

        int currentActive;

        lock (_lock)

        {

            currentActive = _activeConnections.Count;

            retriable = _seeds

                .Where(s =>

                    s.Status == WebSeedStatus.Idle ||

                    (s.Status == WebSeedStatus.Backoff

                        && s.NextRetryTime.HasValue

                        && DateTime.UtcNow >= s.NextRetryTime.Value))

                .ToList();

        }

        foreach (var seed in retriable)

        {

            if (currentActive >= _settings.MaxConnectionsPerTorrent) break;

            seed.Status = WebSeedStatus.Idle;

            try

            {

                var uri = new Uri(seed.Url);

                var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct)

                    .ConfigureAwait(false);

                if (addresses.Length == 0) { seed.Status = WebSeedStatus.Banned; continue; }

                Action<IPeerConnection, int>? dlCallback = _statisticsTracker != null

                    ? (peer, bytes) => _statisticsTracker.RecordDownload(peer, bytes)

                    : null;

                IPeerConnection connection = seed.Type == WebSeedType.BEP19

                    ? new WebSeedConnection(seed.Url, _torrentInfo, _httpClient, _settings, addresses[0], _logger, dlCallback)

                    : new HttpSeedConnection(seed.Url, _torrentInfo, _infoHash, _httpClient, _settings, addresses[0], _logger, dlCallback);

                connection.MessageReceived += OnWebSeedMessageReceived;

                _statisticsTracker?.RegisterPeer(connection);

                lock (_lock)
                {
                    EventHandler<PeerConnectionLostEventArgs> lostHandler =
                        (s, e) => OnConnectionError(connection, e.Exception ?? new Exception(e.Reason ?? "Connection lost"));
                    connection.ConnectionLost += lostHandler;
                    seed.ConnectionLostHandler = lostHandler;

                    seed.Connection = connection;

                    seed.Status = WebSeedStatus.Active;

                    _activeConnections.Add(connection);
                }

                currentActive++;

                _logger.LogDebug("Reconnected to web seed {Url} after backoff", seed.Url);

            }

            catch (Exception ex)

            {

                seed.FailureCount++;

                seed.Status = seed.FailureCount >= 5 ? WebSeedStatus.Banned : WebSeedStatus.Backoff;

                seed.NextRetryTime = DateTime.UtcNow.AddSeconds(

                    _settings.WaitRetrySeconds * Math.Pow(2, seed.FailureCount - 1));

                _logger.LogWarning(ex, "Retry failed for web seed {Url}", seed.Url);

            }

        }

    }

    public void Dispose()

    {

        if (_disposed) return;

        _disposed = true;

        _retryCts?.Cancel();

        _retryCts?.Dispose();

        lock (_lock)

        {

            foreach (var conn in _activeConnections)

            {

                conn.MessageReceived -= OnWebSeedMessageReceived;

                var seed = _seeds.FirstOrDefault(s => s.Connection == conn);
                if (seed?.ConnectionLostHandler != null)
                {
                    conn.ConnectionLost -= seed.ConnectionLostHandler;
                    seed.ConnectionLostHandler = null;
                }

                _statisticsTracker?.UnregisterPeer(conn);

                conn.Dispose();

            }

            _activeConnections.Clear();

        }

    }

}

/// <summary>State of a single web seed URL.</summary>

public class WebSeedState

{

    public string Url { get; set; } = "";

    public WebSeedType Type { get; set; }

    public WebSeedStatus Status { get; set; } = WebSeedStatus.Idle;

    public int FailureCount { get; set; }

    public DateTime? NextRetryTime { get; set; }

    public long BytesDownloaded => Connection?.BytesDownloaded ?? 0;

    public IPeerConnection? Connection { get; set; }

    /// <summary>Stored so it can be unsubscribed when the connection is torn down.</summary>

    public EventHandler<PeerConnectionLostEventArgs>? ConnectionLostHandler { get; set; }

}

public enum WebSeedType { BEP19, BEP17 }

public enum WebSeedStatus { Idle, Active, Backoff, Banned }
