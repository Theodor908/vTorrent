using System;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.PeerCommunication.Transport;

/// <summary>
/// Monitors system network changes and signals listener rebind when enabled.
/// libtorrent parallel: enable_ip_notifier.
/// </summary>
public sealed class NetworkChangeNotifier : IDisposable
{
    private readonly IOptionsMonitor<ConnectionSettings> _connectionMonitor;
    private readonly ILogger<NetworkChangeNotifier> _logger;
    private Action? _onNetworkChanged;

    public NetworkChangeNotifier(
        IOptionsMonitor<ConnectionSettings> connectionMonitor,
        ILogger<NetworkChangeNotifier> logger)
    {
        _connectionMonitor = connectionMonitor;
        _logger = logger;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>Register a callback to invoke when network changes are detected and EnableIpNotifier is true.</summary>
    public void SetRebindCallback(Action onNetworkChanged)
    {
        _onNetworkChanged = onNetworkChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (!_connectionMonitor.CurrentValue.EnableIpNotifier)
            return;

        _logger.LogInformation("Network address change detected, triggering listener rebind");
        _onNetworkChanged?.Invoke();
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }
}
