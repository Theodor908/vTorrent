// src/vTorrent.Core/TrackerCommunication/I2P/I2pHttpTrackerClient.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;
using vTorrent.Core.Network.I2P;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.TrackerCommunication.I2P;

/// <summary>
/// I2P tracker client that speaks raw HTTP over SAM streams.
/// Does NOT use HttpClient to avoid DNS resolution leaks.
/// </summary>
public sealed class I2pHttpTrackerClient : ITrackerClient
{
    private readonly string _trackerUrl;
    private readonly I2pService _i2pService;
    private readonly ILogger? _logger;
    private int _failureCount;
    private bool _disposed;

    public TrackerType Type => TrackerType.Http; // Conceptually HTTP, just over I2P
    public bool IsAvailable => _failureCount < 5;
    public int FailureCount => _failureCount;
    public string TrackerUrl => _trackerUrl;
    public DateTime? LastAnnounce { get; private set; }

    public I2pHttpTrackerClient(string trackerUrl, I2pService i2pService, ILogger? logger = null)
    {
        _trackerUrl = trackerUrl ?? throw new ArgumentNullException(nameof(trackerUrl));
        _i2pService = i2pService ?? throw new ArgumentNullException(nameof(i2pService));
        _logger = logger;
    }

    public async Task<TrackerResponse> AnnounceAsync(TrackerRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve SAM session lazily — if not connected, throw and let tracker retry handle it
            var session = _i2pService.Session;
            if (session == null || !session.IsConnected)
                throw new InvalidOperationException("I2P SAM session not connected — will retry");

            // Parse the .i2p hostname from the tracker URL
            var uri = new Uri(_trackerUrl);
            var hostname = uri.Host; // e.g., "tracker.example.i2p"
            var path = uri.PathAndQuery; // e.g., "/announce"

            // Build query string
            var query = BuildAnnounceQuery(request, path);

            // Resolve the .i2p hostname to a full destination via SAM
            var resolveClient = new I2pSamClient(session.SamHostname, session.SamPort);
            await resolveClient.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            var destBase64 = await resolveClient.NamingLookupAsync(hostname, cancellationToken).ConfigureAwait(false);
            await resolveClient.DisposeAsync().ConfigureAwait(false);

            // Connect to tracker via SAM stream
            var streamClient = new I2pSamClient(session.SamHostname, session.SamPort);
            await streamClient.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            await streamClient.StreamConnectAsync(session.SessionId!, destBase64, cancellationToken).ConfigureAwait(false);

            var networkStream = streamClient.RawStream
                ?? throw new I2pSamException("No stream after STREAM CONNECT");

            // Send raw HTTP GET request
            var httpRequest = $"GET {query} HTTP/1.1\r\nHost: {hostname}\r\nConnection: close\r\n\r\n";
            var requestBytes = Encoding.ASCII.GetBytes(httpRequest);
            await networkStream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);

            // Read response
            var responseBytes = await ReadFullResponseAsync(networkStream, cancellationToken).ConfigureAwait(false);
            await streamClient.DisposeAsync().ConfigureAwait(false);

            // Parse HTTP response body (skip headers)
            var body = ExtractHttpBody(responseBytes);

            // Parse bencode response
            var response = ParseTrackerResponse(body);
            response.TrackerUrl = _trackerUrl;
            _failureCount = 0;
            LastAnnounce = DateTime.UtcNow;
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failureCount++;
            _logger?.LogWarning(ex, "I2P tracker announce failed for {Url}", _trackerUrl);
            throw;
        }
    }

    public Task<ScrapeResponse> ScrapeAsync(byte[] infoHash, CancellationToken cancellationToken = default)
    {
        // Scrape over I2P follows same pattern — deferred for now
        throw new NotSupportedException("I2P tracker scrape not yet implemented");
    }

    internal string BuildAnnounceQuery(TrackerRequest request, string path)
    {
        var sb = new StringBuilder(path);
        sb.Append(path.Contains('?') ? '&' : '?');
        sb.Append("info_hash=").Append(UrlEncode(request.InfoHash));
        sb.Append("&peer_id=").Append(UrlEncode(request.PeerId));
        sb.Append("&port=6881"); // Dummy port for I2P
        sb.Append("&uploaded=").Append(request.Uploaded);
        sb.Append("&downloaded=").Append(request.Downloaded);
        sb.Append("&left=").Append(request.Left);
        sb.Append("&compact=1");

        if (_i2pService.Session?.LocalDestination?.Base64Destination != null)
            sb.Append("&ip=").Append(Uri.EscapeDataString(_i2pService.Session.LocalDestination.Base64Destination + ".i2p"));

        if (request.Event != TrackerEvent.None)
            sb.Append("&event=").Append(request.Event.ToString().ToLowerInvariant());

        sb.Append("&numwant=").Append(request.NumWant > 0 ? request.NumWant : 50);

        return sb.ToString();
    }

    /// <summary>
    /// Test accessor for BuildAnnounceQuery.
    /// </summary>
    public string BuildAnnounceQueryForTest(TrackerRequest request, string path)
        => BuildAnnounceQuery(request, path);

    private static string UrlEncode(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 3);
        foreach (var b in data)
        {
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                || b == '-' || b == '_' || b == '.' || b == '~')
                sb.Append((char)b);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static async Task<byte[]> ReadFullResponseAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > 1024 * 1024) break; // 1MB safety limit
        }
        return ms.ToArray();
    }

    internal static byte[] ExtractHttpBody(byte[] response)
    {
        // Find \r\n\r\n boundary
        for (int i = 0; i < response.Length - 3; i++)
        {
            if (response[i] == '\r' && response[i + 1] == '\n' &&
                response[i + 2] == '\r' && response[i + 3] == '\n')
            {
                var body = new byte[response.Length - i - 4];
                Buffer.BlockCopy(response, i + 4, body, 0, body.Length);
                return body;
            }
        }
        return response; // Fallback: treat entire response as body
    }

    /// <summary>
    /// Test accessor for ExtractHttpBody.
    /// </summary>
    public static byte[] ExtractHttpBodyForTest(byte[] response)
        => ExtractHttpBody(response);

    /// <summary>
    /// Test accessor for ParseTrackerResponse.
    /// </summary>
    public static TrackerResponse ParseTrackerResponseForTest(byte[] body)
        => ParseTrackerResponse(body);

    private static TrackerResponse ParseTrackerResponse(byte[] body)
    {
        if (body == null || body.Length == 0)
            return TrackerResponse.CreateFailure("Empty response body");

        try
        {
            var parser = new BencodeParser();
            var decoded = parser.Parse(body, out _);

            if (decoded is not BDictionary dict)
                return TrackerResponse.CreateFailure("Invalid response format");

            if (dict.TryGetValue(new BString("failure reason"), out var failureObj) &&
                failureObj is BString failureStr)
            {
                return TrackerResponse.CreateFailure(failureStr.ToString());
            }

            var response = new TrackerResponse();

            if (dict.TryGetValue(new BString("interval"), out var intervalObj) &&
                intervalObj is BNumber intervalNum)
                response.Interval = (int)intervalNum.Value;

            if (dict.TryGetValue(new BString("min interval"), out var minIntervalObj) &&
                minIntervalObj is BNumber minIntervalNum)
                response.MinInterval = (int)minIntervalNum.Value;

            if (dict.TryGetValue(new BString("tracker id"), out var trackerIdObj) &&
                trackerIdObj is BString trackerIdStr)
                response.TrackerId = trackerIdStr.ToString();

            if (dict.TryGetValue(new BString("complete"), out var completeObj) &&
                completeObj is BNumber completeNum)
                response.Complete = (int)completeNum.Value;

            if (dict.TryGetValue(new BString("incomplete"), out var incompleteObj) &&
                incompleteObj is BNumber incompleteNum)
                response.Incomplete = (int)incompleteNum.Value;

            if (dict.TryGetValue(new BString("peers"), out var peersObj))
            {
                if (peersObj is BString compactPeers)
                {
                    var peerData = compactPeers.Value.ToArray();

                    if (peerData.Length > 0 && peerData.Length % 32 == 0 && peerData.Length % 6 != 0)
                        response.Peers = TrackerPeer.FromI2pCompactList(peerData);
                    else if (peerData.Length > 0 && peerData.Length % 6 == 0 && peerData.Length % 32 != 0)
                        response.Peers = TrackerPeer.FromCompactList(peerData);
                    else if (peerData.Length > 0 && peerData.Length % 32 == 0)
                        response.Peers = TrackerPeer.FromI2pCompactList(peerData);
                    else if (peerData.Length > 0 && peerData.Length % 6 == 0)
                        response.Peers = TrackerPeer.FromCompactList(peerData);
                }
                else if (peersObj is BList peerList)
                {
                    response.Peers = ParsePeerDictionaries(peerList);
                }
            }

            return response;
        }
        catch (Exception)
        {
            return TrackerResponse.CreateFailure("Failed to parse bencode response");
        }
    }

    private static List<TrackerPeer> ParsePeerDictionaries(BList peerList)
    {
        var peers = new List<TrackerPeer>();
        foreach (var item in peerList)
        {
            if (item is not BDictionary peerDict) continue;

            if (peerDict.TryGetValue(new BString("dest"), out var destObj) && destObj is BString destStr)
            {
                var destBytes = destStr.Value.ToArray();
                if (destBytes.Length == 32)
                {
                    try { peers.Add(TrackerPeer.FromI2pCompact(destBytes)); }
                    catch { }
                }
                continue;
            }

            if (peerDict.TryGetValue(new BString("ip"), out var ipObj) && ipObj is BString ipStr &&
                peerDict.TryGetValue(new BString("port"), out var portObj) && portObj is BNumber portNum)
            {
                if (System.Net.IPAddress.TryParse(ipStr.ToString(), out var ip))
                {
                    byte[] peerId = null;
                    if (peerDict.TryGetValue(new BString("peer id"), out var idObj) && idObj is BString idStr)
                        peerId = idStr.Value.ToArray();
                    peers.Add(new TrackerPeer(ip, (int)portNum.Value, peerId));
                }
            }
        }
        return peers;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
