using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.Network.PortMapping;

/// <summary>
/// SSDP M-SEARCH discovery + persistent NOTIFY listener.
/// Extracted from UpnpClient for SRP (SSDP protocol only).
/// libtorrent: upnp::on_reply() handles both M-SEARCH responses and NOTIFY.
/// </summary>
internal sealed class SsdpDiscovery : IAsyncDisposable
{
    private static readonly IPAddress SsdpMulticast = IPAddress.Parse("239.255.255.250");
    private const int DefaultSsdpPort = 1900;
    private const int MaxDevices = 10;

    private readonly UdpClient _socket;
    private readonly HashSet<string> _knownLocations = new();
    private readonly object _locationsLock = new();
    private readonly CancellationTokenSource _listenCts = new();
    private readonly ILogger? _logger;
    private Task? _listenTask;
    private bool _boundToSsdpPort;

    // Subnet filter state (set during SearchAsync, used by background loop)
    private IPAddress? _listenAddress;
    private IPAddress? _subnetMask;
    private bool _ignoreNonRouters;

    /// <summary>Fires when a new SSDP location URL is discovered (M-SEARCH or NOTIFY).</summary>
    public Action<string>? OnLocationDiscovered { get; set; }

    public SsdpDiscovery(IPAddress? bindAddress = null, ILogger? logger = null)
    {
        _logger = logger;
        var bindAddr = bindAddress ?? IPAddress.Any;
        _socket = new UdpClient();
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Try port 1900 for NOTIFY reception; fall back to ephemeral
        try
        {
            _socket.Client.Bind(new IPEndPoint(bindAddr, DefaultSsdpPort));
            _boundToSsdpPort = true;
        }
        catch (SocketException)
        {
            _socket.Client.Bind(new IPEndPoint(bindAddr, 0));
            _boundToSsdpPort = false;
            _logger?.LogWarning("SSDP: could not bind to port 1900, NOTIFY reception degraded");
        }

        try { _socket.JoinMulticastGroup(SsdpMulticast, bindAddr); } catch { }
        _socket.Client.SetSocketOption(
            SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

        _listenTask = ListenLoopAsync(_listenCts.Token);
    }

    /// <summary>
    /// Send M-SEARCH and collect location URLs. Socket stays open for NOTIFY.
    /// Uses linear increasing delay: 2*retry seconds between retries (libtorrent: 2 * m_retry_count).
    /// </summary>
    public async Task<List<string>> SearchAsync(
        IPAddress? listenAddress = null,
        IPAddress? subnetMask = null,
        bool ignoreNonRouters = false,
        int maxRetries = 12,
        int maxWaitSeconds = 3,
        CancellationToken ct = default)
    {
        _listenAddress = listenAddress;
        _subnetMask = subnetMask;
        _ignoreNonRouters = ignoreNonRouters;

        var msearch = "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "ST: upnp:rootdevice\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            $"MX: {maxWaitSeconds}\r\n" +
            "\r\n";

        var requestBytes = Encoding.ASCII.GetBytes(msearch);
        var target = new IPEndPoint(SsdpMulticast, DefaultSsdpPort);

        for (int retry = 0; retry < maxRetries; retry++)
        {
            int locationCount;
            lock (_locationsLock) locationCount = _knownLocations.Count;
            if (locationCount >= MaxDevices) break;

            try { await _socket.SendAsync(requestBytes, requestBytes.Length, target); } catch { }

            // Wait for responses
            await Task.Delay(TimeSpan.FromSeconds(maxWaitSeconds), ct)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            lock (_locationsLock) locationCount = _knownLocations.Count;

            // libtorrent: stop after 4 retries if devices found
            if (locationCount > 0 && retry >= 3) break;

            // Linear increasing delay (libtorrent: seconds(2 * m_retry_count))
            var backoff = TimeSpan.FromSeconds(2 * (retry + 1));
            await Task.Delay(backoff, ct)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            if (ct.IsCancellationRequested) break;
        }

        lock (_locationsLock)
            return new List<string>(_knownLocations);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveAsync(ct);
                ProcessDatagram(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private void ProcessDatagram(byte[] buffer, IPEndPoint remote)
    {
        var response = Encoding.ASCII.GetString(buffer);

        // libtorrent: accept HTTP 200 (M-SEARCH response) OR NOTIFY method
        var firstLine = response.AsSpan();
        var newlineIdx = firstLine.IndexOf('\r');
        if (newlineIdx > 0) firstLine = firstLine.Slice(0, newlineIdx);

        bool isResponse = firstLine.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase);
        bool isNotify = firstLine.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase);
        if (!isResponse && !isNotify) return;

        // libtorrent: upnp_ignore_nonrouters -- filter devices not on our subnet
        if (_ignoreNonRouters && _listenAddress != null
            && !IPAddress.Any.Equals(_listenAddress)
            && !IPAddress.Loopback.Equals(_listenAddress)
            && _subnetMask != null)
        {
            if (!IsOnSameSubnet(remote.Address, _listenAddress, _subnetMask))
                return;
        }

        // Validate ST for M-SEARCH responses
        if (isResponse)
        {
            var st = ParseHeader(response, "ST");
            if (!string.IsNullOrEmpty(st)
                && !st.Contains("rootdevice", StringComparison.OrdinalIgnoreCase)
                && !st.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase))
                return;
        }

        var location = ParseHeader(response, "Location");
        if (string.IsNullOrEmpty(location)) return;

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https") || uri.Port == 0)
            return;

        bool isNew;
        lock (_locationsLock) isNew = _knownLocations.Add(location);

        if (isNew)
        {
            try { OnLocationDiscovered?.Invoke(location); }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SSDP: OnLocationDiscovered callback failed for {Url}", location);
            }
        }
    }

    internal static bool IsOnSameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var aBytes = a.GetAddressBytes();
        var bBytes = b.GetAddressBytes();
        var mBytes = mask.GetAddressBytes();
        if (aBytes.Length != bBytes.Length || aBytes.Length != mBytes.Length) return false;
        for (int i = 0; i < aBytes.Length; i++)
            if ((aBytes[i] & mBytes[i]) != (bBytes[i] & mBytes[i])) return false;
        return true;
    }

    internal static string? ParseHeader(string httpResponse, string headerName)
    {
        var lines = httpResponse.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring(headerName.Length + 1).Trim().TrimEnd('\r');
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _listenCts.Cancel();
        if (_listenTask != null)
            try { await _listenTask; } catch { }
        _socket.Dispose();
        _listenCts.Dispose();
    }
}
