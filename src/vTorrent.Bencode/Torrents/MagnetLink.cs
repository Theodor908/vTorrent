using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Web;

namespace vTorrent.Bencode.Torrents;

/// <summary>
/// Represents a parsed magnet link with extracted metadata.
/// Based on libtorrent's parse_magnet_uri() implementation.
///
/// Magnet URI format:
/// magnet:?xt=urn:btih:INFOHASH&amp;dn=NAME&amp;tr=TRACKER&amp;...
///
/// Supported parameters:
/// - xt (exact topic): Info hash (btih for v1, btmh for v2)
/// - dn (display name): Torrent name
/// - tr (tracker): Tracker URLs (multiple allowed)
/// - ws (web seed): Web seed URLs
/// - xl (exact length): Total size in bytes
/// - x.pe (peer): Initial peer endpoints
/// - dht (DHT nodes): Initial DHT bootstrap nodes
/// - so (select only): File indices to download
/// </summary>
public class MagnetLink
{
    /// <summary>
    /// The primary info hash bytes (20 bytes for v1 SHA-1, or 32 bytes for v2 SHA-256).
    /// For hybrid magnets, this is the v1 hash.
    /// </summary>
    public byte[] InfoHash { get; init; }

    /// <summary>
    /// The v2 info hash bytes (32 bytes SHA-256), if present. Null for v1-only magnets.
    /// </summary>
    public byte[] InfoHashV2Bytes { get; init; }

    /// <summary>
    /// The primary info hash as a hex string.
    /// </summary>
    public string InfoHashHex => InfoHash != null ? Convert.ToHexString(InfoHash) : null;

    /// <summary>
    /// Display name of the torrent (from dn parameter).
    /// </summary>
    public string DisplayName { get; init; }

    /// <summary>
    /// List of tracker URLs (from tr parameters).
    /// </summary>
    public IReadOnlyList<string> Trackers { get; init; } = Array.Empty<string>();

    /// <summary>
    /// List of web seed URLs (from ws parameters).
    /// </summary>
    public IReadOnlyList<string> WebSeeds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Total size in bytes (from xl parameter), if specified.
    /// </summary>
    public long? ExactLength { get; init; }

    /// <summary>
    /// Initial peer endpoints (from x.pe parameters).
    /// </summary>
    public IReadOnlyList<IPEndPoint> Peers { get; init; } = Array.Empty<IPEndPoint>();

    /// <summary>
    /// DHT bootstrap nodes (from dht parameters).
    /// </summary>
    public IReadOnlyList<(string Host, int Port)> DhtNodes { get; init; } = Array.Empty<(string, int)>();

    /// <summary>
    /// File indices to download (from so parameter).
    /// Empty means download all files.
    /// </summary>
    public IReadOnlyList<int> FileIndices { get; init; } = Array.Empty<int>();

    /// <summary>
    /// The original magnet URI string.
    /// </summary>
    public string OriginalUri { get; init; }

    /// <summary>
    /// Whether this is a BitTorrent v2 magnet link (btmh hash).
    /// </summary>
    public bool IsV2 { get; init; }

    /// <summary>
    /// Parses a magnet URI string into a MagnetLink object.
    /// </summary>
    /// <param name="uri">The magnet URI to parse.</param>
    /// <returns>A parsed MagnetLink object.</returns>
    /// <exception cref="ArgumentException">If the URI is invalid.</exception>
    public static MagnetLink Parse(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("Magnet URI cannot be null or empty", nameof(uri));

        // Validate scheme
        if (!uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid magnet URI: must start with 'magnet:?'", nameof(uri));

        var queryString = uri.Substring(8); // Skip "magnet:?"
        var parameters = ParseQueryString(queryString);

        byte[] infoHash = null;
        byte[] infoHashV2Bytes = null;
        bool isV2 = false;
        string displayName = null;
        var trackers = new List<string>();
        var webSeeds = new List<string>();
        long? exactLength = null;
        var peers = new List<IPEndPoint>();
        var dhtNodes = new List<(string, int)>();
        var fileIndices = new List<int>();

        foreach (var (key, value) in parameters)
        {
            // Handle suffixed parameters (e.g., tr.0, tr.1)
            var baseKey = key.Contains('.') ? key.Substring(0, key.IndexOf('.')) : key;

            switch (baseKey.ToLowerInvariant())
            {
                case "xt":
                    // Exact topic - info hash (may appear twice for hybrid magnets)
                    var (hash, v2) = ParseInfoHash(value);
                    if (hash != null)
                    {
                        if (v2)
                        {
                            infoHashV2Bytes = hash;
                            isV2 = true;
                        }
                        else
                        {
                            infoHash = hash;
                        }
                    }
                    break;

                case "dn":
                    // Display name
                    displayName = value;
                    break;

                case "tr":
                    // Tracker URL
                    if (!string.IsNullOrEmpty(value) && !trackers.Contains(value))
                        trackers.Add(value);
                    break;

                case "ws":
                    // Web seed URL
                    if (!string.IsNullOrEmpty(value) && !webSeeds.Contains(value))
                        webSeeds.Add(value);
                    break;

                case "xl":
                    // Exact length
                    if (long.TryParse(value, out var length) && length > 0)
                        exactLength = length;
                    break;

                case "x.pe":
                case "peer":
                    // Peer endpoint
                    var peer = ParsePeerEndpoint(value);
                    if (peer != null && !peers.Any(p => p.Equals(peer)))
                        peers.Add(peer);
                    break;

                case "dht":
                    // DHT node
                    var node = ParseDhtNode(value);
                    if (node.HasValue)
                        dhtNodes.Add(node.Value);
                    break;

                case "so":
                    // Select only (file indices)
                    fileIndices.AddRange(ParseFileIndices(value));
                    break;
            }
        }

        // For v2-only magnets, use v2 hash as primary if no v1 hash present
        if (infoHash == null && infoHashV2Bytes != null)
            infoHash = infoHashV2Bytes;

        if (infoHash == null)
            throw new ArgumentException("Invalid magnet URI: missing or invalid info hash (xt parameter)", nameof(uri));

        return new MagnetLink
        {
            InfoHash = infoHash,
            InfoHashV2Bytes = infoHashV2Bytes,
            DisplayName = displayName,
            Trackers = trackers,
            WebSeeds = webSeeds,
            ExactLength = exactLength,
            Peers = peers,
            DhtNodes = dhtNodes,
            FileIndices = fileIndices.Distinct().OrderBy(i => i).ToList(),
            OriginalUri = uri,
            IsV2 = isV2
        };
    }

    /// <summary>
    /// Tries to parse a magnet URI, returning null on failure instead of throwing.
    /// </summary>
    public static MagnetLink TryParse(string uri)
    {
        try
        {
            return Parse(uri);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a string is a valid magnet URI.
    /// </summary>
    public static bool IsMagnetUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        return uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) &&
               uri.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase) ||
               uri.Contains("xt=urn:btmh:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a composite InfoHash with v1 and/or v2 hashes.
    /// </summary>
    public InfoHash GetInfoHash()
    {
        SHA1Hash? v1 = null;
        SHA256Hash? v2 = null;

        // Check if InfoHash is a v1 hash (20 bytes) and we don't have a separate v2 hash
        if (InfoHash?.Length == 20 && !IsV2)
            v1 = new SHA1Hash(InfoHash);
        else if (InfoHash?.Length == 20)
            v1 = new SHA1Hash(InfoHash);
        else if (InfoHash?.Length == 32 && IsV2 && InfoHashV2Bytes == null)
            v2 = new SHA256Hash(InfoHash);

        if (InfoHashV2Bytes?.Length == 32)
            v2 = new SHA256Hash(InfoHashV2Bytes);

        return new InfoHash { V1 = v1, V2 = v2 };
    }

    /// <summary>
    /// Creates a magnet URI string from the current data.
    /// </summary>
    public string ToUri()
    {
        var sb = new StringBuilder();

        // v1 hash
        if (InfoHash?.Length == 20)
        {
            sb.Append("magnet:?xt=urn:btih:");
            sb.Append(Convert.ToHexString(InfoHash).ToLowerInvariant());
        }
        else if (InfoHashV2Bytes != null)
        {
            // v2-only: start with btmh
            sb.Append("magnet:?xt=urn:btmh:1220");
            sb.Append(Convert.ToHexString(InfoHashV2Bytes).ToLowerInvariant());
        }
        else
        {
            sb.Append("magnet:?xt=urn:btih:");
            sb.Append(InfoHashHex.ToLowerInvariant());
        }

        // Add v2 hash if hybrid (v1 hash was already added above)
        if (InfoHash?.Length == 20 && InfoHashV2Bytes != null)
        {
            sb.Append("&xt=urn:btmh:1220");
            sb.Append(Convert.ToHexString(InfoHashV2Bytes).ToLowerInvariant());
        }

        if (!string.IsNullOrEmpty(DisplayName))
        {
            sb.Append("&dn=");
            sb.Append(Uri.EscapeDataString(DisplayName));
        }

        foreach (var tracker in Trackers)
        {
            sb.Append("&tr=");
            sb.Append(Uri.EscapeDataString(tracker));
        }

        foreach (var webSeed in WebSeeds)
        {
            sb.Append("&ws=");
            sb.Append(Uri.EscapeDataString(webSeed));
        }

        if (ExactLength.HasValue)
        {
            sb.Append("&xl=");
            sb.Append(ExactLength.Value);
        }

        return sb.ToString();
    }

    private static IEnumerable<(string Key, string Value)> ParseQueryString(string queryString)
    {
        if (string.IsNullOrEmpty(queryString))
            yield break;

        var pairs = queryString.Split('&');
        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
                continue;

            var key = pair.Substring(0, idx);
            var value = idx < pair.Length - 1 ? Uri.UnescapeDataString(pair.Substring(idx + 1)) : string.Empty;
            yield return (key, value);
        }
    }

    private static (byte[] Hash, bool IsV2) ParseInfoHash(string xt)
    {
        if (string.IsNullOrEmpty(xt))
            return (null, false);

        // BitTorrent v1: urn:btih:<hash>
        if (xt.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
        {
            var hashPart = xt.Substring(9);
            var hash = DecodeHash(hashPart, 20);
            return (hash, false);
        }

        // BitTorrent v2: urn:btmh:1220<hash>
        if (xt.StartsWith("urn:btmh:", StringComparison.OrdinalIgnoreCase))
        {
            var hashPart = xt.Substring(9);
            // btmh hashes start with "1220" (SHA-256 multihash prefix)
            if (hashPart.StartsWith("1220", StringComparison.OrdinalIgnoreCase))
                hashPart = hashPart.Substring(4);
            var hash = DecodeHash(hashPart, 32);
            return (hash, true);
        }

        return (null, false);
    }

    private static byte[] DecodeHash(string hashString, int expectedLength)
    {
        if (string.IsNullOrEmpty(hashString))
            return null;

        // Try hex encoding first (40 chars for 20 bytes, 64 chars for 32 bytes)
        if (hashString.Length == expectedLength * 2)
        {
            try
            {
                return Convert.FromHexString(hashString);
            }
            catch
            {
                // Fall through to base32
            }
        }

        // Try base32 encoding (32 chars for 20 bytes)
        if (hashString.Length == 32 && expectedLength == 20)
        {
            try
            {
                return Base32Decode(hashString.ToUpperInvariant());
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        // Remove padding
        input = input.TrimEnd('=');

        var output = new byte[input.Length * 5 / 8];
        int bitBuffer = 0;
        int bitsInBuffer = 0;
        int outputIndex = 0;

        foreach (char c in input)
        {
            int value = alphabet.IndexOf(c);
            if (value < 0)
                throw new ArgumentException($"Invalid base32 character: {c}");

            bitBuffer = (bitBuffer << 5) | value;
            bitsInBuffer += 5;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output[outputIndex++] = (byte)(bitBuffer >> bitsInBuffer);
                bitBuffer &= (1 << bitsInBuffer) - 1;
            }
        }

        return output;
    }

    private static IPEndPoint ParsePeerEndpoint(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        try
        {
            // Format: ip:port or [ipv6]:port
            int lastColon;
            string ipPart;
            string portPart;

            if (value.StartsWith("["))
            {
                // IPv6: [::1]:6881
                var closeBracket = value.IndexOf(']');
                if (closeBracket < 0)
                    return null;
                ipPart = value.Substring(1, closeBracket - 1);
                lastColon = value.IndexOf(':', closeBracket);
                portPart = lastColon >= 0 ? value.Substring(lastColon + 1) : null;
            }
            else
            {
                // IPv4: 192.168.1.1:6881
                lastColon = value.LastIndexOf(':');
                if (lastColon < 0)
                    return null;
                ipPart = value.Substring(0, lastColon);
                portPart = value.Substring(lastColon + 1);
            }

            if (!IPAddress.TryParse(ipPart, out var ip))
                return null;

            if (!int.TryParse(portPart, out var port) || port <= 0 || port > 65535)
                return null;

            return new IPEndPoint(ip, port);
        }
        catch
        {
            return null;
        }
    }

    private static (string Host, int Port)? ParseDhtNode(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var lastColon = value.LastIndexOf(':');
        if (lastColon <= 0)
            return null;

        var host = value.Substring(0, lastColon);
        if (!int.TryParse(value.Substring(lastColon + 1), out var port) || port <= 0 || port > 65535)
            return null;

        return (host, port);
    }

    private static IEnumerable<int> ParseFileIndices(string value)
    {
        if (string.IsNullOrEmpty(value))
            yield break;

        // Format: comma-separated list of indices or ranges
        // Examples: "1,3,5" or "0-5" or "1,3,5-7"
        foreach (var part in value.Split(','))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (trimmed.Contains('-'))
            {
                // Range: start-end
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0], out var start) &&
                    int.TryParse(rangeParts[1], out var end) &&
                    start >= 0 && end >= start)
                {
                    for (int i = start; i <= end && i < 10000; i++) // Limit to prevent abuse
                        yield return i;
                }
            }
            else if (int.TryParse(trimmed, out var index) && index >= 0)
            {
                yield return index;
            }
        }
    }

    public override string ToString()
    {
        return $"MagnetLink[{InfoHashHex?.Substring(0, Math.Min(8, InfoHashHex?.Length ?? 0))}..., Name={DisplayName ?? "unknown"}, Trackers={Trackers.Count}]";
    }
}
