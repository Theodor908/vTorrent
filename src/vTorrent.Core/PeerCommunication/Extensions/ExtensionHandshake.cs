using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Represents a BEP 10 extension handshake message.
///
/// Format:
/// {
///   "m": {               // Extension name to ID mapping
///     "ut_pex": 1,
///     "ut_metadata": 2,
///     ...
///   },
///   "p": 6881,           // Listen port (optional)
///   "v": "client/1.0",   // Client version (optional)
///   "yourip": <bytes>,   // Our external IP as seen by peer (optional)
///   "ipv4": <bytes>,     // Our IPv4 address (optional)
///   "ipv6": <bytes>,     // Our IPv6 address (optional)
///   "reqq": 250,         // Request queue size (optional)
///   "metadata_size": N   // For ut_metadata (optional)
/// }
/// </summary>
public class ExtensionHandshake
{
    /// <summary>
    /// Extension ID 0 is reserved for the handshake message itself.
    /// </summary>
    public const byte HandshakeExtensionId = 0;

    /// <summary>
    /// Extension name to ID mapping ("m" dictionary).
    /// Key is extension name (e.g., "ut_pex"), value is extension ID.
    /// </summary>
    public Dictionary<string, byte> SupportedExtensions { get; set; } = new();

    /// <summary>
    /// Listen port ("p").
    /// </summary>
    public int? ListenPort { get; set; }

    /// <summary>
    /// Client version string ("v").
    /// </summary>
    public string ClientVersion { get; set; }

    /// <summary>
    /// Our external IP as seen by the peer ("yourip").
    /// </summary>
    public byte[] YourIp { get; set; }

    /// <summary>
    /// Our IPv4 address ("ipv4").
    /// </summary>
    public byte[] IPv4 { get; set; }

    /// <summary>
    /// Our IPv6 address ("ipv6").
    /// </summary>
    public byte[] IPv6 { get; set; }

    /// <summary>
    /// Request queue size ("reqq").
    /// </summary>
    public int? RequestQueueSize { get; set; }

    /// <summary>
    /// Metadata size for ut_metadata extension ("metadata_size").
    /// </summary>
    public int? MetadataSize { get; set; }

    /// <summary>
    /// The raw dictionary for accessing any additional fields.
    /// </summary>
    public BDictionary RawDictionary { get; private set; }

    /// <summary>
    /// Parses an extension handshake from bencoded data.
    /// </summary>
    public static ExtensionHandshake Parse(ReadOnlySpan<byte> data)
    {
        var parser = new BencodeParser();
        var obj = parser.Parse(data, out _);

        if (obj is not BDictionary dict)
            throw new InvalidDataException("Extension handshake must be a bencoded dictionary");

        var handshake = new ExtensionHandshake
        {
            RawDictionary = dict
        };

        // Parse "m" dictionary
        if (dict.TryGetValue("m", out var mObj) && mObj is BDictionary mDict)
        {
            foreach (var kvp in mDict)
            {
                if (kvp.Value is BNumber num)
                {
                    handshake.SupportedExtensions[kvp.Key.ToString()] = (byte)num.Value;
                }
            }
        }

        // Parse optional fields
        if (dict.TryGetValue("p", out var pObj) && pObj is BNumber pNum)
            handshake.ListenPort = (int)pNum.Value;

        if (dict.TryGetValue("v", out var vObj) && vObj is BString vStr)
            handshake.ClientVersion = vStr.ToString();

        if (dict.TryGetValue("yourip", out var youripObj) && youripObj is BString youripStr)
            handshake.YourIp = youripStr.Value.ToArray();

        if (dict.TryGetValue("ipv4", out var ipv4Obj) && ipv4Obj is BString ipv4Str)
            handshake.IPv4 = ipv4Str.Value.ToArray();

        if (dict.TryGetValue("ipv6", out var ipv6Obj) && ipv6Obj is BString ipv6Str)
            handshake.IPv6 = ipv6Str.Value.ToArray();

        if (dict.TryGetValue("reqq", out var reqqObj) && reqqObj is BNumber reqqNum)
            handshake.RequestQueueSize = (int)reqqNum.Value;

        if (dict.TryGetValue("metadata_size", out var metaObj) && metaObj is BNumber metaNum)
            handshake.MetadataSize = (int)metaNum.Value;

        return handshake;
    }

    /// <summary>
    /// Encodes this extension handshake to bencoded bytes.
    /// </summary>
    public byte[] Encode()
    {
        var dict = new BDictionary();

        // Add "m" dictionary
        var mDict = new BDictionary();
        foreach (var kvp in SupportedExtensions)
        {
            mDict.AddNumber(kvp.Key, kvp.Value);
        }
        dict.Add("m", mDict);

        // Add optional fields
        if (ListenPort.HasValue)
            dict.AddNumber("p", ListenPort.Value);

        if (!string.IsNullOrEmpty(ClientVersion))
            dict.AddString("v", ClientVersion);

        if (YourIp != null && YourIp.Length > 0)
            dict.AddBytes("yourip", YourIp);

        if (IPv4 != null && IPv4.Length > 0)
            dict.AddBytes("ipv4", IPv4);

        if (IPv6 != null && IPv6.Length > 0)
            dict.AddBytes("ipv6", IPv6);

        if (RequestQueueSize.HasValue)
            dict.AddNumber("reqq", RequestQueueSize.Value);

        if (MetadataSize.HasValue)
            dict.AddNumber("metadata_size", MetadataSize.Value);

        // Encode to bytes
        var size = dict.GetSizeInBytes();
        var buffer = new byte[size];
        dict.EncodeTo(buffer);
        return buffer;
    }

    /// <summary>
    /// Gets the extension ID for a specific extension name.
    /// Returns null if the peer doesn't support this extension.
    /// </summary>
    public byte? GetExtensionId(string extensionName)
    {
        return SupportedExtensions.TryGetValue(extensionName, out var id) ? id : null;
    }

    /// <summary>
    /// Checks if the peer supports a specific extension.
    /// </summary>
    public bool SupportsExtension(string extensionName)
    {
        return SupportedExtensions.ContainsKey(extensionName);
    }

    /// <summary>
    /// Parses the "yourip" field as an IPAddress.
    /// </summary>
    public IPAddress GetYourIpAddress()
    {
        if (YourIp == null || YourIp.Length == 0)
            return null;

        try
        {
            return new IPAddress(YourIp);
        }
        catch
        {
            return null;
        }
    }

    public override string ToString()
    {
        var extensions = string.Join(", ", SupportedExtensions.Keys);
        return $"ExtensionHandshake[Extensions: {extensions}, Client: {ClientVersion ?? "unknown"}]";
    }
}
