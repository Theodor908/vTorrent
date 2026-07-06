using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.Network.PortMapping;

public sealed class UpnpClient : IAsyncDisposable
{
    private const int MaxMappings = 50; // libtorrent: max_global_mappings

    private static readonly HashSet<string> ValidSoapContentTypes =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "text/xml", "text/soap+xml", "application/xml", "application/soap+xml"
    };

    private static bool IsValidSoapContentType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType)) return true;
        return ValidSoapContentTypes.Contains(mediaType);
    }

    private readonly HttpClient _http;
    private readonly Dictionary<int, PortMapping> _activeMappings = new();
    private int _nextMappingId;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;
    private readonly List<MappingTracker> _trackers = new();
    private readonly object _trackersLock = new();
    private DateTime _currentNextExpiry = DateTime.MaxValue;
    private volatile bool _closing;
    private readonly object _mappingsLock = new();

    public IReadOnlyCollection<PortMapping> ActiveMappings
    {
        get { lock (_mappingsLock) return _activeMappings.Values.ToList(); }
    }

    public UpnpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>Reject new operations during shutdown. Called before DisposeAsync.</summary>
    internal void Close()
    {
        _closing = true;
    }

    /// <summary>Mark all trackers as Abandoned. Called during shutdown to prevent refresh re-adds.</summary>
    internal void AbandonAllTrackers()
    {
        lock (_trackersLock)
            foreach (var t in _trackers)
                while (t.State != MappingState.Abandoned)
                    t.RecordFailure();
    }

    /// <summary>Get current tracker states for the manager to inspect.</summary>
    internal IReadOnlyList<MappingTracker> GetTrackers()
    {
        lock (_trackersLock) return _trackers.ToList();
    }

    internal async Task<UpnpDevice?> FetchDeviceDescriptionAsync(string locationUrl, CancellationToken ct)
    {
        try
        {
            var xml = await _http.GetStringAsync(locationUrl, ct);
            var desc = UpnpXmlParsers.FindControlUrl(xml);
            if (string.IsNullOrEmpty(desc.ControlUrl) || string.IsNullOrEmpty(desc.ServiceType))
                return null;

            var baseUri = new Uri(locationUrl);
            string controlUrl;
            if (!string.IsNullOrEmpty(desc.UrlBase) && !desc.ControlUrl.StartsWith("http"))
            {
                var urlBase = desc.UrlBase.TrimEnd('/');
                var ctlPath = desc.ControlUrl.StartsWith("/") ? desc.ControlUrl : "/" + desc.ControlUrl;
                controlUrl = urlBase + ctlPath;
            }
            else if (desc.ControlUrl.StartsWith("http"))
                controlUrl = desc.ControlUrl;
            else
                controlUrl = new Uri(baseUri, desc.ControlUrl).ToString();

            var ctlUri = new Uri(controlUrl);
            return new UpnpDevice
            {
                Url = locationUrl,
                ControlUrl = controlUrl,
                ServiceType = desc.ServiceType,
                Hostname = ctlUri.Host,
                Port = ctlUri.Port,
                Path = ctlUri.AbsolutePath,
                Model = string.IsNullOrEmpty(desc.Model) ? null : desc.Model
            };
        }
        catch { return null; }
    }

    internal async Task<PortMapping?> AddMappingAsync(
        UpnpDevice device, PortMapProtocol protocol,
        int internalPort, int externalPort, uint leaseSeconds,
        CancellationToken ct = default)
    {
        if (_closing || device.Disabled) return null;
        int count;
        lock (_mappingsLock) count = _activeMappings.Count;
        if (count >= MaxMappings) return null;
        int currentExtPort = externalPort;
        int maxAttempts = 5;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var lease = device.UseLeaseDuration ? leaseSeconds : 0;
            var protocolStr = protocol == PortMapProtocol.Tcp ? "TCP" : "UDP";
            var localIp = GetLocalIpForDevice(device);

            var soap = "<?xml version=\"1.0\"?>\n" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                $"<s:Body><u:AddPortMapping xmlns:u=\"{device.ServiceType}\">" +
                "<NewRemoteHost></NewRemoteHost>" +
                $"<NewExternalPort>{currentExtPort}</NewExternalPort>" +
                $"<NewProtocol>{protocolStr}</NewProtocol>" +
                $"<NewInternalPort>{internalPort}</NewInternalPort>" +
                $"<NewInternalClient>{localIp}</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>vTorrent</NewPortMappingDescription>" +
                $"<NewLeaseDuration>{lease}</NewLeaseDuration>" +
                "</u:AddPortMapping></s:Body></s:Envelope>";

            var (responseBody, statusCode) = await PostSoapAsync(device, "AddPortMapping", soap, ct);
            if (responseBody == null) return null;

            var errorCode = UpnpXmlParsers.FindErrorCode(responseBody);
            if (errorCode == -1 && statusCode >= 200 && statusCode < 300)
            {
                if (_closing) return null;

                int id;
                int? existingId;
                PortMapping mapping;
                lock (_mappingsLock)
                {
                    existingId = FindExistingMapping(protocol, internalPort);
                    id = existingId ?? Interlocked.Increment(ref _nextMappingId);
                    mapping = new PortMapping
                    {
                        Id = id,
                        Protocol = protocol,
                        Transport = PortMapTransport.Upnp,
                        InternalPort = internalPort,
                        ExternalPort = currentExtPort,
                        ExternalAddress = device.ExternalIp,
                        Expiry = lease > 0 ? DateTime.UtcNow.AddSeconds(lease * 3.0 / 4.0) : DateTime.MaxValue
                    };
                    _activeMappings[id] = mapping;
                }

                if (lease > 0)
                {
                    bool isRefresh = existingId != null;
                    if (!isRefresh)
                    {
                        var tracker = new MappingTracker(device, mapping, lease);
                        tracker.RecordSuccess(mapping);
                        lock (_trackersLock) _trackers.Add(tracker);
                    }

                    var newExpiry = mapping.Expiry;
                    if (newExpiry < _currentNextExpiry)
                    {
                        _refreshCts?.Cancel();
                        if (_refreshTask != null)
                            try { await _refreshTask; } catch { }
                        _refreshCts = new CancellationTokenSource();
                        _refreshTask = RefreshLoopAsync(_refreshCts.Token);
                    }
                    else if (_refreshTask == null)
                    {
                        _refreshCts = new CancellationTokenSource();
                        _refreshTask = RefreshLoopAsync(_refreshCts.Token);
                    }
                }
                return mapping;
            }

            if (errorCode == (int)UpnpErrorCode.OnlyPermanentLeasesSupported) { device.UseLeaseDuration = false; continue; }
            if (errorCode == (int)UpnpErrorCode.InternalPortMustMatchExternal) { currentExtPort = internalPort; continue; }
            if (errorCode == (int)UpnpErrorCode.PortMappingConflict || errorCode == (int)UpnpErrorCode.ActionFailed)
            { currentExtPort = 40000 + Random.Shared.Next(10000); continue; }
            return null;
        }
        return null;
    }

    internal async Task DeleteMappingAsync(UpnpDevice device, PortMapping mapping, CancellationToken ct = default)
    {
        var protocolStr = mapping.Protocol == PortMapProtocol.Tcp ? "TCP" : "UDP";
        var soap = "<?xml version=\"1.0\"?>\n" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            $"<s:Body><u:DeletePortMapping xmlns:u=\"{device.ServiceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{mapping.ExternalPort}</NewExternalPort>" +
            $"<NewProtocol>{protocolStr}</NewProtocol>" +
            "</u:DeletePortMapping></s:Body></s:Envelope>";

        await PostSoapAsync(device, "DeletePortMapping", soap, ct);
        lock (_mappingsLock) _activeMappings.Remove(mapping.Id);
        lock (_trackersLock)
            _trackers.RemoveAll(t => t.Mapping.Id == mapping.Id && t.Device.Url == device.Url);
    }

    internal async Task<IPAddress?> GetExternalIpAsync(UpnpDevice device, CancellationToken ct = default)
    {
        var soap = "<?xml version=\"1.0\"?>\n" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            $"<s:Body><u:GetExternalIPAddress xmlns:u=\"{device.ServiceType}\">" +
            "</u:GetExternalIPAddress></s:Body></s:Envelope>";

        var (responseBody, statusCode) = await PostSoapAsync(device, "GetExternalIPAddress", soap, ct);
        if (responseBody == null || statusCode >= 300) return null;

        var ipStr = UpnpXmlParsers.FindIpAddress(responseBody);
        if (ipStr != null && IPAddress.TryParse(ipStr, out var ip))
        {
            device.ExternalIp = ip;
            return ip;
        }
        return null;
    }

    private async Task<(string? body, int statusCode)> PostSoapAsync(
        UpnpDevice device, string action, string soapBody, CancellationToken ct)
    {
        try
        {
            var content = new StringContent(soapBody, Encoding.UTF8, "text/xml");
            content.Headers.TryAddWithoutValidation("SOAPAction", $"\"{device.ServiceType}#{action}\"");
            var response = await _http.PostAsync(device.ControlUrl, content, ct);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!IsValidSoapContentType(mediaType))
                return (null, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(ct);
            return (body, (int)response.StatusCode);
        }
        catch { return (null, 0); }
    }

    private int? FindExistingMapping(PortMapProtocol protocol, int internalPort)
    {
        foreach (var kvp in _activeMappings)
            if (kvp.Value.Protocol == protocol && kvp.Value.InternalPort == internalPort)
                return kvp.Key;
        return null;
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTime earliest = DateTime.MaxValue;
            lock (_trackersLock)
            {
                foreach (var t in _trackers)
                    if (t.ShouldRefresh && t.Mapping.Expiry < earliest)
                        earliest = t.Mapping.Expiry;
            }

            if (earliest == DateTime.MaxValue) break;

            _currentNextExpiry = earliest;
            var delay = earliest - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
            }

            if (ct.IsCancellationRequested) break;

            List<MappingTracker> toRefresh;
            lock (_trackersLock)
                toRefresh = _trackers.FindAll(t => t.ShouldRefresh && t.Mapping.Expiry <= DateTime.UtcNow);

            foreach (var tracker in toRefresh)
            {
                if (_closing) break;
                try
                {
                    var refreshed = await AddMappingAsync(
                        tracker.Device, tracker.Mapping.Protocol,
                        tracker.Mapping.InternalPort, tracker.Mapping.ExternalPort,
                        tracker.LeaseSeconds, ct);

                    if (refreshed != null)
                        tracker.RecordSuccess(refreshed);
                    else
                        tracker.RecordFailure();
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    tracker.RecordFailure();
                }
            }
        }
    }

    private static string GetLocalIpForDevice(UpnpDevice device)
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Connect(device.Hostname, device.Port);
            return ((IPEndPoint)sock.LocalEndPoint!).Address.ToString();
        }
        catch { return "0.0.0.0"; }
    }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        _refreshCts?.Cancel();
        if (_refreshTask != null)
            try { await _refreshTask; } catch { }
        _http.Dispose();
        _refreshCts?.Dispose();
    }
}
