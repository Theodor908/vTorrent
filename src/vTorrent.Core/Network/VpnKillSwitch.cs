using System;
using System.Net.NetworkInformation;
using System.Threading;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Interfaces;

namespace vTorrent.Core.Network;

/// <summary>
/// Monitors a VPN network interface. Blocks all connections when the interface goes down.
/// Hybrid approach: NetworkChange events + polling as safety net.
/// </summary>
public class VpnKillSwitch : IVpnStatus, IDisposable
{
    private readonly ILogger? _logger;
    private string _interfaceName = "";
    private Timer? _pollTimer;
    private bool _isBlocking;
    private bool _isVpnUp = true; // Assume up before first check — so first CheckInterface detects "went down"
    private bool _isRunning;

    public bool IsBlocking => _isBlocking;
    public bool IsVpnInterfaceUp => _isVpnUp;
    public bool IsMonitoring => _isRunning;
    public string MonitoredInterface => _interfaceName;
    public event Action<bool>? BlockingStateChanged;

    public VpnKillSwitch(ILogger? logger = null)
    {
        _logger = logger;
    }

    private const int PollIntervalSeconds = 30;

    public void Start(string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
            throw new ArgumentException("Interface name required for kill-switch", nameof(interfaceName));

        _interfaceName = interfaceName;
        _isRunning = true;

        // Initial check
        CheckInterface();

        // Subscribe to network change events
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        // Start polling timer as backup
        _pollTimer = new Timer(_ => CheckInterface(), null,
            TimeSpan.FromSeconds(PollIntervalSeconds),
            TimeSpan.FromSeconds(PollIntervalSeconds));

        _logger?.LogInformation("[VPN_KILLSWITCH] Started monitoring interface '{Interface}'", interfaceName);
    }

    public void Stop()
    {
        _isRunning = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _pollTimer?.Dispose();
        _pollTimer = null;

        // Do NOT clear _isBlocking or fire BlockingStateChanged.
        // Stop() is a lifecycle operation (cleanup), not a state change.
        // Only CheckInterface() should manage blocking state transitions.

        _logger?.LogInformation("[VPN_KILLSWITCH] Stopped");
    }

    public bool ShouldBlock() => _isBlocking;

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (_isRunning)
            CheckInterface();
    }

    private void CheckInterface()
    {
        var wasUp = _isVpnUp;
        _isVpnUp = InterfaceResolver.IsInterfaceUp(_interfaceName);

        if (wasUp && !_isVpnUp)
        {
            _isBlocking = true;
            _logger?.LogWarning("[VPN_KILLSWITCH] Interface '{Interface}' went DOWN — blocking all connections",
                _interfaceName);
            BlockingStateChanged?.Invoke(true);
        }
        else if (!wasUp && _isVpnUp)
        {
            _isBlocking = false;
            _logger?.LogInformation("[VPN_KILLSWITCH] Interface '{Interface}' is UP — unblocking connections",
                _interfaceName);
            BlockingStateChanged?.Invoke(false);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
