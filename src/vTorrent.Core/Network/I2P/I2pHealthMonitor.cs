using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.Network.I2P;

/// <summary>
/// Monitors SAM bridge health via periodic heartbeat (HELLO handshake probe).
/// Implements exponential backoff reconnection (5s, 10s, 20s, 40s, cap 120s).
/// </summary>
public sealed class I2pHealthMonitor : IAsyncDisposable
{
    private readonly I2pSamSession _session;
    private readonly ILogger? _logger;
    private readonly TimeSpan _heartbeatInterval;

    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private int _reconnectAttempts;

    private static readonly int[] BackoffMs = { 5_000, 10_000, 20_000, 40_000, 120_000 };

    public I2pAvailability Availability { get; private set; } = I2pAvailability.Unavailable;

    public event EventHandler<I2pAvailability>? AvailabilityChanged;

    public I2pHealthMonitor(I2pSamSession session, ILogger? logger = null,
        TimeSpan? heartbeatInterval = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(60);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_session.IsConnected)
                {
                    // Heartbeat: verify SAM bridge is responsive
                    var heartbeatOk = await PerformHeartbeatAsync(ct).ConfigureAwait(false);

                    if (heartbeatOk)
                    {
                        _reconnectAttempts = 0;
                        SetAvailability(I2pAvailability.Available);
                        await Task.Delay(_heartbeatInterval, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Bridge became unresponsive
                        _logger?.LogWarning("I2P SAM bridge heartbeat failed, marking unavailable");
                        SetAvailability(I2pAvailability.Unavailable);
                        await _session.DisconnectAsync().ConfigureAwait(false);
                        await ReconnectWithBackoffAsync(ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    // Not connected — attempt reconnection
                    await ReconnectWithBackoffAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "I2P health monitor error");
                SetAvailability(I2pAvailability.Unavailable);
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<bool> PerformHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            // Create a temporary SAM client to test connectivity
            var probe = new I2pSamClient(_session.SamHostname, _session.SamPort);
            try
            {
                await probe.HandshakeAsync(ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                await probe.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task ReconnectWithBackoffAsync(CancellationToken ct)
    {
        SetAvailability(I2pAvailability.Reconnecting);

        var backoffIndex = Math.Min(_reconnectAttempts, BackoffMs.Length - 1);
        var delayMs = BackoffMs[backoffIndex];
        _reconnectAttempts++;

        _logger?.LogInformation("I2P reconnect attempt {Attempt}, backoff {Delay}ms",
            _reconnectAttempts, delayMs);

        await Task.Delay(delayMs, ct).ConfigureAwait(false);

        try
        {
            await _session.ConnectAsync(ct).ConfigureAwait(false);
            _reconnectAttempts = 0;
            SetAvailability(I2pAvailability.Available);
            _logger?.LogInformation("I2P SAM bridge reconnected successfully");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "I2P reconnect attempt {Attempt} failed", _reconnectAttempts);
            SetAvailability(I2pAvailability.Unavailable);
        }
    }

    private void SetAvailability(I2pAvailability newState)
    {
        if (Availability != newState)
        {
            Availability = newState;
            AvailabilityChanged?.Invoke(this, newState);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_monitorTask != null)
        {
            try { await _monitorTask.ConfigureAwait(false); }
            catch { /* expected */ }
        }
        _cts?.Dispose();
    }
}
