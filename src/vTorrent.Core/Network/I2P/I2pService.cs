using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Transport.I2P;

namespace vTorrent.Core.Network.I2P;

/// <summary>
/// Top-level I2P service lifecycle manager. Manages SAM session,
/// health monitor, and transport availability.
/// </summary>
public sealed class I2pService : IAsyncDisposable
{
    private readonly IOptionsMonitor<I2pSettings> _settingsMonitor;
    private readonly string _dataDirectory;
    private readonly ILoggerFactory? _loggerFactory;

    private I2pSamSession? _session;
    private I2pHealthMonitor? _healthMonitor;
    private I2pTransportConnector? _transportConnector;
    private I2pTransportListener? _transportListener;

    public bool IsConnected => _session?.IsConnected == true;
    public I2pAvailability Availability => _healthMonitor?.Availability ?? I2pAvailability.NotApplicable;
    public I2pSamSession? Session => _session;
    public I2pTransportConnector? TransportConnector => _transportConnector;
    public I2pTransportListener? TransportListener => _transportListener;

    public event EventHandler<I2pAvailability>? AvailabilityChanged;

    public I2pService(IOptionsMonitor<I2pSettings> settingsMonitor, string dataDirectory,
        ILoggerFactory? loggerFactory = null)
    {
        _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var settings = _settingsMonitor.CurrentValue;
        if (!settings.Enabled) return;

        _session = new I2pSamSession(settings, _dataDirectory);

        try
        {
            await _session.ConnectAsync(ct).ConfigureAwait(false);

            _transportConnector = new I2pTransportConnector(_session);
            _transportListener = new I2pTransportListener(_session);

            _healthMonitor = new I2pHealthMonitor(
                _session,
                _loggerFactory?.CreateLogger<I2pHealthMonitor>());
            _healthMonitor.AvailabilityChanged += (s, a) => AvailabilityChanged?.Invoke(this, a);
            _healthMonitor.Start();
        }
        catch (Exception)
        {
            // SAM bridge not available — start health monitor anyway for reconnection
            _healthMonitor = new I2pHealthMonitor(
                _session,
                _loggerFactory?.CreateLogger<I2pHealthMonitor>());
            _healthMonitor.AvailabilityChanged += (s, a) =>
            {
                // Create transport components on first successful connection
                if (a == I2pAvailability.Available && _transportConnector == null)
                {
                    _transportConnector = new I2pTransportConnector(_session);
                    _transportListener = new I2pTransportListener(_session);
                }
                AvailabilityChanged?.Invoke(this, a);
            };
            _healthMonitor.Start();
        }
    }

    public async Task StopAsync()
    {
        if (_healthMonitor != null)
            await _healthMonitor.DisposeAsync().ConfigureAwait(false);

        if (_transportListener != null)
            await _transportListener.DisposeAsync().ConfigureAwait(false);

        if (_session != null)
            await _session.DisconnectAsync().ConfigureAwait(false);

        _transportConnector = null;
        _transportListener = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
