using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;
using vTorrent.Core.Engine;
using vTorrent.Core.TrackerCommunication;
using TrackerSettings = vTorrent.Abstractions.Settings.TrackerSettings;
using vTorrent.Core.TrackerCommunication.Models;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.TrackerCommunication.Http
{
    public class HttpTrackerClient : IHttpTrackerClient
    {
        private readonly IOptionsMonitor<TrackerSettings> _trackerMonitor;
        private readonly ILogger<HttpTrackerClient> _logger;
        private readonly HttpClient _httpClient;
        private readonly IBencodeParser _bencodeParser;
        private readonly bool _ownsHttpClient;
        private readonly IOptionsMonitor<PrivacySettings> _privacyMonitor;

        private int _failureCount;
        private DateTime? _lastAnnounce;
        private bool _isDisposed;

        /// <summary>
        /// Encryption settings for BEP 8 supportcrypto/requirecrypto parameters.
        /// </summary>
        public EncryptionSettings EncryptionSettings { get; set; }

        public string TrackerUrl { get; }
        public TrackerType Type => TrackerType.Http;
        public bool IsAvailable => _failureCount < 5;
        public DateTime? LastAnnounce => _lastAnnounce;
        public int FailureCount => _failureCount;

        public bool FollowRedirects { get; set; } = true;
        public int MaxRedirects { get; set; } = 5;
        public string UserAgent { get; set; }
        public IDictionary<string, string> LastResponseHeaders { get; private set; }

        /// <summary>
        /// Creates an HttpTrackerClient using the shared connection pool.
        /// This is the recommended constructor for production use.
        /// </summary>
        public HttpTrackerClient(string trackerUrl, IOptionsMonitor<TrackerSettings> trackerMonitor, ILogger<HttpTrackerClient> logger, IBencodeParser bencodeParser, IOptionsMonitor<PrivacySettings> privacyMonitor = null)
            : this(trackerUrl, trackerMonitor, logger, bencodeParser, GetSharedHttpClient(trackerMonitor.CurrentValue), privacyMonitor)
        {
            _ownsHttpClient = false; // We don't own the shared client
        }

        /// <summary>
        /// Creates an HttpTrackerClient with a custom HttpClient.
        /// Use this for testing or when you need specific client configuration.
        /// </summary>
        public HttpTrackerClient(string trackerUrl, IOptionsMonitor<TrackerSettings> trackerMonitor, ILogger<HttpTrackerClient> logger, IBencodeParser bencodeParser, HttpClient httpClient, IOptionsMonitor<PrivacySettings> privacyMonitor = null)
        {
            if (string.IsNullOrWhiteSpace(trackerUrl))
                throw new ArgumentException("Tracker URL cannot be empty", nameof(trackerUrl));

            if (!trackerUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trackerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Tracker URL must start with http:// or https://");

            TrackerUrl = trackerUrl;
            _trackerMonitor = trackerMonitor ?? throw new ArgumentNullException(nameof(trackerMonitor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            UserAgent = trackerMonitor.CurrentValue.UserAgent;
            _bencodeParser = bencodeParser ?? throw new ArgumentNullException(nameof(bencodeParser));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsHttpClient = true; // We own custom clients
            _privacyMonitor = privacyMonitor;

            LastResponseHeaders = new Dictionary<string, string>();

            _logger.LogDebug("HttpTrackerClient created for {TrackerUrl} (connection pooling: {Pooled})",
                TrackerUrl, !_ownsHttpClient);
        }

        /// <summary>
        /// Gets the shared HTTP client with connection pooling, initializing it if necessary.
        /// </summary>
        private static HttpClient GetSharedHttpClient(TrackerSettings settings)
        {
            if (TrackerConstants.EnableHttpConnectionPooling)
            {
                try
                {
                    // Try to get the shared client
                    return SharedTrackerHttpClient.GetClient();
                }
                catch (InvalidOperationException)
                {
                    // Not initialized yet, initialize it now
                    SharedTrackerHttpClient.Initialize(new OptionsMonitorShim<TrackerSettings>(settings));
                    return SharedTrackerHttpClient.GetClient();
                }
            }
            else
            {
                // Connection pooling disabled, create a standalone client
                return new HttpClient(new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5
                })
                {
                    Timeout = TimeSpan.FromSeconds(settings.HttpTimeoutSeconds)
                };
            }
        }

        /// <summary>Returns the effective User-Agent, respecting anonymous mode.</summary>
        internal string GetEffectiveUserAgent()
        {
            if (_privacyMonitor?.CurrentValue?.AnonymousMode == true)
                return "";
            return UserAgent;
        }

        /// <summary>Returns true if announce IP should be suppressed.</summary>
        internal bool ShouldSuppressAnnounceIp()
        {
            return _privacyMonitor?.CurrentValue?.AnonymousMode == true;
        }

        public async Task<TrackerResponse> AnnounceAsync(TrackerRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            _logger.LogDebug("Announcing to tracker {TrackerUrl} [Event: {Event}]", TrackerUrl, request.Event);

            // SSRF mitigation: block requests to private/loopback addresses
            if (_trackerMonitor.CurrentValue.SsrfMitigation)
            {
                var uri = new Uri(TrackerUrl);
                if (SsrfGuard.ShouldBlock(uri.Host))
                {
                    _logger.LogWarning("SSRF mitigation: blocking tracker request to private address {Host}", uri.Host);
                    return TrackerResponse.CreateFailure("SSRF mitigation: tracker resolves to private address", TrackerUrl);
                }
            }

            for (int attempt = 0; attempt <= _trackerMonitor.CurrentValue.MaxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    _logger.LogDebug("Retry attempt {Attempt}/{MaxRetries} for {TrackerUrl}", attempt, _trackerMonitor.CurrentValue.MaxRetries, TrackerUrl);
                    await Task.Delay(TimeSpan.FromSeconds(_trackerMonitor.CurrentValue.RetryDelaySeconds), cancellationToken);
                }

                try
                {
                    string announceUrl = BuildAnnounceUrl(request);
                    _logger.LogTrace("Announce URL: {Url}", announceUrl);

                    var httpRequest = new HttpRequestMessage(HttpMethod.Get, announceUrl);
                    var effectiveUa = GetEffectiveUserAgent();
                    if (!string.IsNullOrEmpty(effectiveUa))
                        httpRequest.Headers.Add("User-Agent", effectiveUa);

                    var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

                    LastResponseHeaders = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Tracker {TrackerUrl} returned HTTP {StatusCode}", TrackerUrl, response.StatusCode);
                        _failureCount++;
                        continue;
                    }

                    byte[] responseData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var trackerResponse = ParseAnnounceResponse(responseData);
                    trackerResponse.TrackerUrl = TrackerUrl;

                    if (trackerResponse.IsSuccess)
                    {
                        _logger.LogDebug("Successful announce to {TrackerUrl}: {PeerCount} peers, {Seeders} seeders",
                            TrackerUrl, trackerResponse.Peers.Count, trackerResponse.Complete);

                        _failureCount = 0;
                        _lastAnnounce = DateTime.UtcNow;
                        return trackerResponse;
                    }
                    else
                    {
                        _logger.LogWarning("Tracker {TrackerUrl} returned failure: {Reason}",
                            TrackerUrl, trackerResponse.FailureReason);
                        _failureCount++;
                        return trackerResponse;
                    }

                }
                catch (TaskCanceledException)
                {
                    _logger.LogWarning("Announce to {TrackerUrl} timed out", TrackerUrl);
                    _failureCount++;

                    if (attempt == _trackerMonitor.CurrentValue.MaxRetries)
                        return TrackerResponse.CreateFailure("Request timed out", TrackerUrl);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "HTTP error announcing to {TrackerUrl}", TrackerUrl);
                    _failureCount++;

                    if (attempt == _trackerMonitor.CurrentValue.MaxRetries)
                        return TrackerResponse.CreateFailure($"HTTP error: {ex.Message}", TrackerUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error announcing to {TrackerUrl}", TrackerUrl);
                    _failureCount++;

                    if (attempt == _trackerMonitor.CurrentValue.MaxRetries)
                        return TrackerResponse.CreateFailure($"Error: {ex.Message}", TrackerUrl);
                }
            }

            return TrackerResponse.CreateFailure("Max retries exceeded", TrackerUrl);
        }

        public async Task<ScrapeResponse> ScrapeAsync(byte[] infoHash, CancellationToken cancellationToken = default)
        {
            if (!TrackerConstants.EnableScrape)
            {
                _logger.LogDebug("Scraping is disabled in settings");
                return ScrapeResponse.CreateFailure("Scraping disabled");
            }

            if (infoHash == null || infoHash.Length != 20)
                throw new ArgumentException("InfoHash must be exactly 20 bytes");

            _logger.LogDebug("Scraping tracker {TrackerUrl}", TrackerUrl);

            try
            {
                string? scrapeUrl = BuildScrapeUrl(infoHash);
                if (scrapeUrl == null)
                {
                    _logger.LogDebug("Tracker URL does not contain /announce, scrape not supported: {TrackerUrl}", TrackerUrl);
                    return ScrapeResponse.CreateFailure("Tracker URL does not contain /announce");
                }
                _logger.LogTrace("Scrape URL: {Url}", scrapeUrl);

                var httpRequest = new HttpRequestMessage(HttpMethod.Get, scrapeUrl);
                var effectiveUa = GetEffectiveUserAgent();
                if (!string.IsNullOrEmpty(effectiveUa))
                    httpRequest.Headers.Add("User-Agent", effectiveUa);

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Scrape to {TrackerUrl} returned HTTP {StatusCode}",
                        TrackerUrl, response.StatusCode);
                    return ScrapeResponse.CreateFailure($"HTTP {response.StatusCode}");
                }

                byte[] responseData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var scrapeResponse = ParseScrapeResponse(responseData, infoHash);

                _logger.LogDebug("Successful scrape from {TrackerUrl}: {Seeders} seeders, {Leechers} leechers",
                    TrackerUrl, scrapeResponse.Complete, scrapeResponse.Incomplete);

                return scrapeResponse;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scraping tracker {TrackerUrl}", TrackerUrl);
                return ScrapeResponse.CreateFailure(ex.Message);
            }
        }

        private string BuildAnnounceUrl(TrackerRequest request)
        {
            var builder = new StringBuilder(TrackerUrl);

            // Add query separator
            builder.Append(TrackerUrl.Contains('?') ? '&' : '?');

            // Required parameters
            builder.Append("info_hash=").Append(UrlEncodeBytes(request.InfoHash));
            builder.Append("&peer_id=").Append(UrlEncodeBytes(request.PeerId));
            builder.Append("&port=").Append(request.Port);
            builder.Append("&uploaded=").Append(request.Uploaded);
            builder.Append("&downloaded=").Append(request.Downloaded);
            builder.Append("&left=").Append(request.Left);
            builder.Append("&compact=").Append(request.Compact ? "1" : "0");

            // Optional parameters
            if (request.NumWant > 0)
                builder.Append("&numwant=").Append(request.NumWant);

            if (request.Event != TrackerEvent.None)
                builder.Append("&event=").Append(request.Event.ToQueryString());

            if (!string.IsNullOrEmpty(request.TrackerId))
                builder.Append("&trackerid=").Append(Uri.EscapeDataString(request.TrackerId));

            if (!string.IsNullOrEmpty(request.Ip) && !ShouldSuppressAnnounceIp())
                builder.Append("&ip=").Append(Uri.EscapeDataString(request.Ip));

            if (request.NoPeerId)
                builder.Append("&no_peer_id=1");

            // BEP 27: report client's external IPs to private trackers
            if (request.IsPrivateTorrent)
            {
                if (!string.IsNullOrEmpty(request.Ipv4Address))
                    builder.Append("&ipv4=").Append(Uri.EscapeDataString(request.Ipv4Address));
                if (!string.IsNullOrEmpty(request.Ipv6Address))
                    builder.Append("&ipv6=").Append(Uri.EscapeDataString(request.Ipv6Address));
            }

            // BEP 8: supportcrypto / requirecrypto (conditional on AnnounceCryptoSupport)
            if (_trackerMonitor.CurrentValue.AnnounceCryptoSupport && EncryptionSettings != null)
            {
                var cryptoParams = new Dictionary<string, string>();
                MseTrackerParams.Apply(cryptoParams, request.InfoHash, EncryptionSettings);
                foreach (var (key, value) in cryptoParams)
                    builder.Append('&').Append(key).Append('=').Append(value);
            }

            return builder.ToString();
        }

        private string? BuildScrapeUrl(byte[] infoHash)
        {
            int idx = TrackerUrl.LastIndexOf("/announce");
            if (idx < 0)
                return null;

            string scrapeUrl = string.Concat(
                TrackerUrl.AsSpan(0, idx),
                "/scrape",
                TrackerUrl.AsSpan(idx + "/announce".Length));

            var builder = new StringBuilder(scrapeUrl);
            builder.Append(scrapeUrl.Contains('?') ? '&' : '?');
            builder.Append("info_hash=").Append(UrlEncodeBytes(infoHash));
            return builder.ToString();
        }

        private TrackerResponse ParseAnnounceResponse(byte[] data)
        {
            try
            {
               // Parse bencoded response
                var decoded = BencodeDecode(data);

                if (decoded is not BDictionary dict)
                    return TrackerResponse.CreateFailure("Invalid response format", TrackerUrl);

                // Check for failure reason
                if (dict.TryGetValue(new BString("failure reason"), out var failureObj) && 
                    failureObj is BString failureStr)
                {
                    return TrackerResponse.CreateFailure(failureStr.ToString(), TrackerUrl);
                }

                var response = new TrackerResponse
                {
                    TrackerUrl = TrackerUrl
                };

                // Parse interval (required)
                if (dict.TryGetValue(new BString("interval"), out var intervalObj) && 
                    intervalObj is BNumber intervalNum)
                {
                    response.Interval = (int)intervalNum.Value;
                }
                else
                {
                    response.Interval = TrackerConstants.DefaultAnnounceInterval;
                }

                // Parse min interval (optional)
                if (dict.TryGetValue(new BString("min interval"), out var minIntervalObj) && 
                    minIntervalObj is BNumber minIntervalNum)
                {
                    response.MinInterval = (int)minIntervalNum.Value;
                }

                // Parse tracker ID (optional)
                if (dict.TryGetValue(new BString("tracker id"), out var trackerIdObj) && 
                    trackerIdObj is BString trackerIdStr)
                {
                    response.TrackerId = trackerIdStr.ToString();
                }

                // Parse complete/incomplete (optional)
                if (dict.TryGetValue(new BString("complete"), out var completeObj) && 
                    completeObj is BNumber completeNum)
                {
                    response.Complete = (int)completeNum.Value;
                }

                if (dict.TryGetValue(new BString("incomplete"), out var incompleteObj) && 
                    incompleteObj is BNumber incompleteNum)
                {
                    response.Incomplete = (int)incompleteNum.Value;
                }

                // Parse warning message (optional)
                if (dict.TryGetValue(new BString("warning message"), out var warningObj) && 
                    warningObj is BString warningStr)
                {
                    response.WarningMessage = warningStr.ToString();
                }

                // BEP 24: External IP address
                if (dict.TryGetValue(new BString("external ip"), out var extIpObj) && extIpObj is BString extIpStr)
                {
                    var ipBytes = extIpStr.Value.Span;
                    if (ipBytes.Length == 4 || ipBytes.Length == 16)
                    {
                        try
                        {
                            response.ExternalIp = new System.Net.IPAddress(ipBytes);
                        }
                        catch
                        {
                            // Malformed IP bytes — ignore
                        }
                    }
                }

                // Parse peers
                if (dict.TryGetValue(new BString("peers"), out var peersObj))
                {
                    if (peersObj is BString compactPeers)
                    {
                        // BEP 23: compact format (6 bytes per peer — 4-byte IPv4 + 2-byte port)
                        response.Peers = TrackerPeer.FromCompactList(compactPeers.Value.ToArray());
                    }
                    else if (peersObj is BList peerList)
                    {
                        // Dictionary format
                        response.Peers = ParsePeerDictionaries(peerList);
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing tracker response");
                return TrackerResponse.CreateFailure($"Parse error: {ex.Message}", TrackerUrl);
            }
        }

        private ScrapeResponse ParseScrapeResponse(byte[] data, byte[] infoHash)
        {
            try
            {
                var decoded = BencodeDecode(data);

                if (decoded is not BDictionary dict)
                    return ScrapeResponse.CreateFailure("Invalid response format");

                // Check for failure
                if (dict.TryGetValue("failure reason", out var failureObj) && failureObj is BString failureReason)
                {
                    return ScrapeResponse.CreateFailure(failureReason);
                }

                // Get files dictionary
                if (!dict.TryGetValue("files", out var filesObj) || filesObj is not BDictionary files)
                {
                    return ScrapeResponse.CreateFailure("Missing 'files' in response");
                }

                // Find our info hash
                var infoHashKey = new BString(infoHash);
                if (!files.TryGetValue(infoHashKey, out var fileObj) || fileObj is not BDictionary fileDict)
                {
                    return ScrapeResponse.CreateFailure("Info hash not found in response");
                }

                var response = new ScrapeResponse { IsSuccess = true };

                if (fileDict.TryGetValue(new BString("complete"), out var completeObj) && completeObj is BNumber completeNum)
                {
                    response.Complete = (int)completeNum.Value;
                }

                if (fileDict.TryGetValue(new BString("incomplete"), out var incompleteObj) && incompleteObj is BNumber incompleteNum)
                {
                    response.Incomplete = (int)incompleteNum.Value;
                }

                if (fileDict.TryGetValue(new BString("downloaded"), out var downloadedObj) && downloadedObj is BNumber downloadedNum)
                {
                    response.Downloaded = (int)downloadedNum.Value;
                }

                if (fileDict.TryGetValue(new BString("name"), out var nameObj) && nameObj is BString nameStr)
                {
                    response.Name = nameStr.ToString();
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing scrape response");
                return ScrapeResponse.CreateFailure($"Parse error: {ex.Message}");
            }
        }

        private List<TrackerPeer> ParsePeerDictionaries(BList peerList)
        {
            var peers = new List<TrackerPeer>();

            foreach (var peerObj in peerList)
            {
                if (peerObj is not BDictionary peerDict)
                    continue;

                try
                {
                    if (!peerDict.TryGetValue(new BString("ip"), out var ipObj) || 
                        ipObj is not BString ipStr)
                        continue;

                    if (!peerDict.TryGetValue(new BString("port"), out var portObj) || 
                        portObj is not BNumber portNum)
                        continue;

                    byte[] peerId = null;
                    if (peerDict.TryGetValue(new BString("peer id"), out var peerIdObj) && 
                        peerIdObj is BString peerIdStr)
                    {
                        peerId = peerIdStr.Value.ToArray();
                    }

                    var peer = new TrackerPeer(
                        System.Net.IPAddress.Parse(ipStr.ToString()), 
                        (int)portNum.Value, 
                        peerId
                    );
                    peers.Add(peer);
                }
                catch
                {
                    // Skip invalid peer
                    continue;
                }
            }

            return peers;
        }

        private string UrlEncodeBytes(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        // Simple bencode decoder (you should use your existing BencodeDecoder)
        private IBObject BencodeDecode(byte[] data)
        {
            return _bencodeParser.Parse(data, out _);
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            // Only dispose the HttpClient if we own it (not from shared pool)
            if (_ownsHttpClient)
            {
                _httpClient?.Dispose();
            }

            _isDisposed = true;

            _logger.LogDebug("HttpTrackerClient disposed for {TrackerUrl}", TrackerUrl);
        }
    }
}
