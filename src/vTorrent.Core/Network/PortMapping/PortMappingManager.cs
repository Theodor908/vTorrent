using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Interfaces;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.PortMapping;

/// <summary>
/// Manages port mapping lifecycle across NAT-PMP and UPnP transports.
/// Both transports run in parallel. NAT-PMP results are preferred.
/// </summary>
public sealed class PortMappingManager : IPortMappingCallback, IAsyncDisposable
{
    private readonly IOptionsMonitor<ConnectionSettings> _settingsMonitor;
    private readonly IExternalIpVoter? _externalIpVoter;
    private readonly ILogger? _logger;

    // NAT-PMP transport
    private NatPmpClient? _natPmpClient;
    private PortMapping? _tcpMappingNatPmp;
    private PortMapping? _udpMappingNatPmp;
    private PeriodicTimer? _natPmpRefreshTimer;
    private CancellationTokenSource? _natPmpRefreshCts;
    private Task? _natPmpRefreshTask;

    // UPnP transport
    private UpnpClient? _upnpClient;
    private SsdpDiscovery? _ssdpDiscovery;
    private List<UpnpDevice>? _upnpDevices;
    private PortMapping? _tcpMappingUpnp;
    private PortMapping? _udpMappingUpnp;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _settingsChangeLock = new(1, 1);
    private IDisposable? _settingsChangeToken;
    private int _listenPort;
    private bool _started;

    public bool IsActive
    {
        get
        {
            lock (_lock)
                return _tcpMappingNatPmp != null || _udpMappingNatPmp != null
                    || _tcpMappingUpnp != null || _udpMappingUpnp != null;
        }
    }

    public PortMapping? TcpMapping
    {
        get { lock (_lock) return _tcpMappingNatPmp ?? _tcpMappingUpnp; }
    }

    public PortMapping? UdpMapping
    {
        get { lock (_lock) return _udpMappingNatPmp ?? _udpMappingUpnp; }
    }

    public PortMappingManager(
        IOptionsMonitor<ConnectionSettings> settingsMonitor,
        IExternalIpVoter? externalIpVoter = null,
        ILogger? logger = null)
    {
        _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
        _externalIpVoter = externalIpVoter;
        _logger = logger;
    }

    public async Task StartAsync(int listenPort, CancellationToken ct = default)
    {
        var settings = _settingsMonitor.CurrentValue;
        var tasks = new List<Task>();

        if (settings.EnableNatPmp)
            tasks.Add(StartNatPmpAsync(listenPort, settings, ct));

        if (settings.EnableUpnp)
            tasks.Add(StartUpnpAsync(listenPort, settings, ct));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);

        _listenPort = listenPort;
        _settingsChangeToken = _settingsMonitor.OnChange(OnSettingsChanged);
        _started = true;
    }

    private async Task StartNatPmpAsync(int listenPort, ConnectionSettings settings, CancellationToken ct)
    {
        try
        {
            var gateway = GatewayDiscovery.DiscoverGateway();
            if (gateway == null)
            {
                _logger?.LogWarning("NAT-PMP: no default gateway found");
                return;
            }

            _logger?.LogInformation("NAT-PMP: discovered gateway {Gateway}", gateway);
            _natPmpClient = new NatPmpClient(gateway);

            try
            {
                var externalIp = await _natPmpClient.GetExternalAddressAsync(ct);
                if (externalIp != null)
                {
                    _logger?.LogInformation("NAT-PMP: external IP is {Ip}", externalIp);
                    _externalIpVoter?.AddVote(externalIp, "natpmp");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "NAT-PMP: failed to get external IP");
            }

            var lifetime = (uint)settings.NatPmpLeaseSeconds;

            var tcp = await _natPmpClient.AddMappingAsync(
                PortMapProtocol.Tcp, listenPort, listenPort, lifetime, ct);
            var udp = await _natPmpClient.AddMappingAsync(
                PortMapProtocol.Udp, listenPort, listenPort, lifetime, ct);

            lock (_lock)
            {
                _tcpMappingNatPmp = tcp;
                _udpMappingNatPmp = udp;
            }

            if (tcp != null)
                _logger?.LogInformation("NAT-PMP: mapped TCP {Internal} -> {External}",
                    listenPort, tcp.ExternalPort);
            if (udp != null)
                _logger?.LogInformation("NAT-PMP: mapped UDP {Internal} -> {External}",
                    listenPort, udp.ExternalPort);

            var refreshInterval = TimeSpan.FromSeconds(settings.NatPmpLeaseSeconds / 2.0);
            _natPmpRefreshCts = new CancellationTokenSource();
            _natPmpRefreshTimer = new PeriodicTimer(refreshInterval);
            _natPmpRefreshTask = NatPmpRefreshLoopAsync(_natPmpRefreshCts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NAT-PMP: startup failed");
        }
    }

    private async Task StartUpnpAsync(int listenPort, ConnectionSettings settings, CancellationToken ct)
    {
        try
        {
            // Resolve listen address for subnet filtering
            IPAddress? listenAddr = null;
            var iface = settings.ListenInterfaces?.FirstOrDefault();
            if (iface != null && iface != "0.0.0.0" && iface != "[::]")
                IPAddress.TryParse(iface, out listenAddr);

            IPAddress? subnetMask = null;
            if (listenAddr != null)
                subnetMask = GatewayDiscovery.GetSubnetMask(listenAddr);

            // 1. Create SsdpDiscovery with persistent socket
            _ssdpDiscovery = new SsdpDiscovery(listenAddr, _logger);
            _ssdpDiscovery.OnLocationDiscovered = url => _ = HandleNewLocationAsync(url, listenPort, settings, ct);

            // 2. Search for devices (linear increasing delay, up to 12 retries)
            var locations = await _ssdpDiscovery.SearchAsync(
                listenAddress: listenAddr,
                subnetMask: subnetMask,
                ignoreNonRouters: settings.UpnpIgnoreNonRouters,
                ct: ct);

            if (locations.Count == 0)
            {
                _logger?.LogDebug("UPnP: no IGD devices found");
                return;
            }

            // 3. Create UpnpClient for SOAP operations
            _upnpClient = new UpnpClient();

            // 4. Fetch device descriptions
            var devices = new List<UpnpDevice>();
            foreach (var location in locations)
            {
                var device = await _upnpClient.FetchDeviceDescriptionAsync(location, ct);
                if (device != null)
                    devices.Add(device);
            }

            if (devices.Count == 0)
            {
                _logger?.LogDebug("UPnP: no valid IGD descriptions found");
                return;
            }

            _upnpDevices = devices;
            _logger?.LogInformation("UPnP: found {Count} IGD device(s)", devices.Count);

            // 5. libtorrent: map_timer — wait 1 second for late SSDP responses
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

            // 6. Map ports on each device
            var lifetime = (uint)settings.UpnpLeaseSeconds;
            foreach (var device in devices)
            {
                try
                {
                    var ip = await _upnpClient.GetExternalIpAsync(device, ct);
                    if (ip != null)
                    {
                        _logger?.LogInformation("UPnP: external IP is {Ip} (via {Model})",
                            ip, device.Model ?? "unknown");
                        _externalIpVoter?.AddVote(ip, "upnp");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "UPnP: failed to get external IP from {Url}", device.Url);
                }

                var tcp = await _upnpClient.AddMappingAsync(
                    device, PortMapProtocol.Tcp, listenPort, listenPort, lifetime, ct);
                var udp = await _upnpClient.AddMappingAsync(
                    device, PortMapProtocol.Udp, listenPort, listenPort, lifetime, ct);

                lock (_lock)
                {
                    _tcpMappingUpnp ??= tcp;
                    _udpMappingUpnp ??= udp;
                }

                if (tcp != null)
                    _logger?.LogInformation("UPnP: mapped TCP {Internal} -> {External} on {Model}",
                        listenPort, tcp.ExternalPort, device.Model ?? "unknown");
                if (udp != null)
                    _logger?.LogInformation("UPnP: mapped UDP {Internal} -> {External} on {Model}",
                        listenPort, udp.ExternalPort, device.Model ?? "unknown");
            }

            // 7. Check for abandoned mappings (spec Section 10)
            CheckForAbandonedMappings();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "UPnP: startup failed");
        }
    }

    private async Task HandleNewLocationAsync(string locationUrl, int listenPort, ConnectionSettings settings, CancellationToken ct)
    {
        if (_upnpClient == null) return;
        try
        {
            var device = await _upnpClient.FetchDeviceDescriptionAsync(locationUrl, ct);
            if (device == null) return;

            _logger?.LogInformation("UPnP: new device discovered via NOTIFY: {Model}", device.Model ?? "unknown");

            var lifetime = (uint)settings.UpnpLeaseSeconds;
            var tcp = await _upnpClient.AddMappingAsync(
                device, PortMapProtocol.Tcp, listenPort, listenPort, lifetime, ct);
            var udp = await _upnpClient.AddMappingAsync(
                device, PortMapProtocol.Udp, listenPort, listenPort, lifetime, ct);

            lock (_lock)
            {
                _tcpMappingUpnp ??= tcp;
                _udpMappingUpnp ??= udp;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "UPnP: failed to handle NOTIFY for {Url}", locationUrl);
        }
    }

    private void CheckForAbandonedMappings()
    {
        if (_upnpClient == null) return;
        foreach (var tracker in _upnpClient.GetTrackers())
        {
            if (tracker.State == MappingState.Abandoned)
            {
                _logger?.LogWarning("UPnP: abandoned {Protocol} mapping on {Model} after {Count} failures",
                    tracker.Mapping.Protocol, tracker.Device.Model ?? "unknown", tracker.FailCount);
                OnPortMapError(tracker.Mapping, $"Abandoned after {tracker.FailCount} failures");
            }
        }
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _settingsChangeToken?.Dispose();
        _settingsChangeToken = null;
        await StopInternalAsync();
        _started = false;
    }

    private void OnSettingsChanged(ConnectionSettings newSettings, string? name)
    {
        _ = Task.Run(async () =>
        {
            await _settingsChangeLock.WaitAsync();
            try
            {
                if (!_started) return;

                // ListenPort changed — tear down and recreate both transports
                if (newSettings.ListenPort != _listenPort)
                {
                    _logger?.LogInformation("Port mapping: listen port changed {Old} -> {New}, remapping",
                        _listenPort, newSettings.ListenPort);
                    await StopInternalAsync();
                    _listenPort = newSettings.ListenPort;
                    await StartInternalAsync(_listenPort, newSettings, CancellationToken.None);
                    return;
                }

                // NAT-PMP toggled off
                if (!newSettings.EnableNatPmp && _natPmpClient != null)
                {
                    _natPmpRefreshCts?.Cancel();
                    if (_natPmpRefreshTask != null)
                        try { await _natPmpRefreshTask; } catch { }
                    _natPmpRefreshTimer?.Dispose();

                    PortMapping? tcp, udp;
                    lock (_lock) { tcp = _tcpMappingNatPmp; udp = _udpMappingNatPmp; }
                    if (tcp != null) try { await _natPmpClient.DeleteMappingAsync(tcp); } catch { }
                    if (udp != null) try { await _natPmpClient.DeleteMappingAsync(udp); } catch { }
                    await _natPmpClient.DisposeAsync();
                    _natPmpClient = null;
                    lock (_lock) { _tcpMappingNatPmp = null; _udpMappingNatPmp = null; }
                    _logger?.LogInformation("NAT-PMP: disabled via settings change");
                }

                // NAT-PMP toggled on
                if (newSettings.EnableNatPmp && _natPmpClient == null)
                {
                    await StartNatPmpAsync(_listenPort, newSettings, CancellationToken.None);
                    _logger?.LogInformation("NAT-PMP: enabled via settings change");
                }

                // UPnP toggled off
                if (!newSettings.EnableUpnp && _upnpClient != null)
                {
                    _upnpClient?.AbandonAllTrackers();
                    _upnpClient?.Close();
                    if (_upnpDevices != null)
                    {
                        foreach (var device in _upnpDevices)
                            foreach (var mapping in _upnpClient.ActiveMappings.ToArray())
                                try { await _upnpClient.DeleteMappingAsync(device, mapping); } catch { }
                    }
                    await _upnpClient.DisposeAsync();
                    _upnpClient = null;
                    _upnpDevices = null;
                    if (_ssdpDiscovery != null)
                    {
                        await _ssdpDiscovery.DisposeAsync();
                        _ssdpDiscovery = null;
                    }
                    lock (_lock) { _tcpMappingUpnp = null; _udpMappingUpnp = null; }
                    _logger?.LogInformation("UPnP: disabled via settings change");
                }

                // UPnP toggled on
                if (newSettings.EnableUpnp && _upnpClient == null)
                {
                    await StartUpnpAsync(_listenPort, newSettings, CancellationToken.None);
                    _logger?.LogInformation("UPnP: enabled via settings change");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Port mapping: failed to apply settings change");
            }
            finally
            {
                _settingsChangeLock.Release();
            }
        });
    }

    /// <summary>Internal stop without touching _started or _settingsChangeToken.</summary>
    private async Task StopInternalAsync()
    {
        _natPmpRefreshCts?.Cancel();
        if (_natPmpRefreshTask != null)
            try { await _natPmpRefreshTask; } catch { }
        _natPmpRefreshTimer?.Dispose();

        if (_natPmpClient != null)
        {
            PortMapping? tcp, udp;
            lock (_lock) { tcp = _tcpMappingNatPmp; udp = _udpMappingNatPmp; }
            if (tcp != null) try { await _natPmpClient.DeleteMappingAsync(tcp); } catch { }
            if (udp != null) try { await _natPmpClient.DeleteMappingAsync(udp); } catch { }
            await _natPmpClient.DisposeAsync();
            _natPmpClient = null;
        }

        // Mark all UPnP trackers Abandoned to prevent refresh re-adds
        _upnpClient?.AbandonAllTrackers();
        _upnpClient?.Close();

        if (_upnpClient != null)
        {
            if (_upnpDevices != null)
                foreach (var device in _upnpDevices)
                    foreach (var mapping in _upnpClient.ActiveMappings.ToArray())
                        try { await _upnpClient.DeleteMappingAsync(device, mapping); } catch { }
            await _upnpClient.DisposeAsync();
            _upnpClient = null;
            _upnpDevices = null;
        }

        if (_ssdpDiscovery != null)
        {
            await _ssdpDiscovery.DisposeAsync();
            _ssdpDiscovery = null;
        }

        lock (_lock)
        {
            _tcpMappingNatPmp = null; _udpMappingNatPmp = null;
            _tcpMappingUpnp = null; _udpMappingUpnp = null;
        }
    }

    /// <summary>Internal start for both transports.</summary>
    private async Task StartInternalAsync(int listenPort, ConnectionSettings settings, CancellationToken ct)
    {
        var tasks = new List<Task>();
        if (settings.EnableNatPmp) tasks.Add(StartNatPmpAsync(listenPort, settings, ct));
        if (settings.EnableUpnp) tasks.Add(StartUpnpAsync(listenPort, settings, ct));
        if (tasks.Count > 0) await Task.WhenAll(tasks);
    }

    private async Task NatPmpRefreshLoopAsync(CancellationToken ct)
    {
        while (await _natPmpRefreshTimer!.WaitForNextTickAsync(ct))
        {
            try
            {
                var settings = _settingsMonitor.CurrentValue;
                var lifetime = (uint)settings.NatPmpLeaseSeconds;

                PortMapping? tcp, udp;
                lock (_lock) { tcp = _tcpMappingNatPmp; udp = _udpMappingNatPmp; }

                if (tcp != null && _natPmpClient != null)
                {
                    var refreshed = await _natPmpClient.AddMappingAsync(
                        PortMapProtocol.Tcp, tcp.InternalPort, tcp.ExternalPort, lifetime, ct);
                    if (refreshed != null)
                        lock (_lock) { if (_tcpMappingNatPmp != null) _tcpMappingNatPmp.Expiry = refreshed.Expiry; }
                }

                if (udp != null && _natPmpClient != null)
                {
                    var refreshed = await _natPmpClient.AddMappingAsync(
                        PortMapProtocol.Udp, udp.InternalPort, udp.ExternalPort, lifetime, ct);
                    if (refreshed != null)
                        lock (_lock) { if (_udpMappingNatPmp != null) _udpMappingNatPmp.Expiry = refreshed.Expiry; }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "NAT-PMP: lease refresh failed");
            }
        }
    }

    public void OnPortMapped(PortMapping mapping)
    {
        if (mapping.ExternalAddress != null)
        {
            var source = mapping.Transport == PortMapTransport.Upnp ? "upnp" : "natpmp";
            _externalIpVoter?.AddVote(mapping.ExternalAddress, source);
        }
    }

    public void OnPortMapError(PortMapping mapping, string error)
    {
        _logger?.LogWarning("Port mapping error for {Transport} port {Port}: {Error}",
            mapping.Transport, mapping.InternalPort, error);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _settingsChangeLock.Dispose();
        _natPmpRefreshCts?.Dispose();
    }
}
