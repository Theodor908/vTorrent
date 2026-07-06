using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.Engine;

/// <summary>
/// libtorrent on_tick equivalent. Fires every TickIntervalMs to:
/// 1. Wake the download loop if pipeline slots are available (stall recovery)
/// 2. Act as defense-in-depth — even if every callback is correct, the tick
///    guarantees bounded pipeline refill time.
///
/// This is NOT a replacement for the download loop's own dispatch logic.
/// It's a safety net that detects when the pipeline has stalled (in-progress
/// pieces exist but no pending block requests) and wakes the loop.
/// </summary>
public sealed class PipelineTick : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<int> _getPendingRequests;
    private readonly Func<int> _getInProgressPieces;
    private readonly Func<bool> _isComplete;
    private Timer? _timer;
    private bool _disposed;
    private int _ticksSinceLastDispatch = 0;

    /// <summary>Tick interval in milliseconds. libtorrent uses 100ms; we use 250ms
    /// as a compromise between responsiveness and overhead.</summary>
    public const int TickIntervalMs = 100;

    /// <summary>Fired when the tick detects the pipeline needs attention.</summary>
    public event Action? PipelineStalled;

    /// <summary>
    /// Called by the download loop each tick that dispatched at least one block.
    /// Resets the stall counter.
    /// </summary>
    public void ReportDispatch()
    {
        Interlocked.Exchange(ref _ticksSinceLastDispatch, 0);
    }

    public PipelineTick(
        ILogger logger,
        Func<int> getPendingRequests,
        Func<int> getInProgressPieces,
        Func<bool> isComplete)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _getPendingRequests = getPendingRequests ?? throw new ArgumentNullException(nameof(getPendingRequests));
        _getInProgressPieces = getInProgressPieces ?? throw new ArgumentNullException(nameof(getInProgressPieces));
        _isComplete = isComplete ?? throw new ArgumentNullException(nameof(isComplete));
    }

    public void Start()
    {
        _timer?.Dispose();
        _timer = new Timer(OnTick, null, TickIntervalMs, TickIntervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTick(object? state)
    {
        try
        {
            if (_isComplete()) return;

            // Stall detection: in-progress pieces exist but no pending block requests.
            // This means the download loop failed to dispatch requests — wake it.
            int pending = _getPendingRequests();
            int inProgress = _getInProgressPieces();

            int ticksSinceDispatch = Interlocked.Increment(ref _ticksSinceLastDispatch);
            bool classicStall = inProgress > 0 && pending == 0;
            bool prolongedStall = inProgress > 0 && ticksSinceDispatch >= 5; // 500ms with no dispatch at 100ms interval
            if (classicStall || prolongedStall)
            {
                _logger.LogDebug("PipelineTick: stall detected (in-progress={InProgress}, pending={Pending}, ticksSinceDispatch={Ticks}), waking download loop",
                    inProgress, pending, ticksSinceDispatch);
                PipelineStalled?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PipelineTick: error in tick handler");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
