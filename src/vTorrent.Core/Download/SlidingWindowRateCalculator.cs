using System;
using System.Collections.Concurrent;
using System.Threading;

namespace vTorrent.Core.Download;

public class SlidingWindowRateCalculator
{
    private readonly ConcurrentQueue<(long timestampTicks, int bytes)> _samples = new();
    private readonly long _windowTicks;
    private long _windowTotal;

    // Coalescing: accumulate small samples before enqueuing
    private int _pendingBytes;
    private long _lastPruneTimestamp;
    private const int CoalesceThreshold = 4096;
    private const long PruneIntervalTicks = 500 * TimeSpan.TicksPerMillisecond;

    // Smoothed rate state for ETA calculations - prevents jumpy display during network gaps
    private double _smoothedRate;
    private long _lastSampleTimestamp;
    private long _lastDecayTimestamp;
    private bool _smoothedRateInitialized;
    private const double BlendAlpha = 0.3;     // Blend factor when active (higher = more responsive)
    private const double DecayFactor = 0.92;   // Decay multiplier per second when inactive (slower decay)

    // Pause state - when paused, rate should immediately go to 0 (libtorrent behavior)
    private volatile bool _isPaused;

    public SlidingWindowRateCalculator(TimeSpan window)
    {
        _windowTicks = window.Ticks;
        _lastSampleTimestamp = DateTime.UtcNow.Ticks;
        _lastDecayTimestamp = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Set pause state. When paused, CurrentRate returns 0 immediately (libtorrent behavior).
    /// This prevents UI graphs from showing stale rates after pausing.
    /// </summary>
    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        if (paused)
        {
            // Clear smoothed rate immediately on pause
            _smoothedRate = 0;
            _smoothedRateInitialized = false;
        }
    }

    /// <summary>
    /// Clear all samples and reset rate to 0. Called on pause for immediate rate drop.
    /// Follows libtorrent's pattern where paused torrents show 0 rate immediately.
    /// </summary>
    public void Reset()
    {
        // Drain the queue
        while (_samples.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _windowTotal, 0);
        Interlocked.Exchange(ref _pendingBytes, 0);
        _smoothedRate = 0;
        _smoothedRateInitialized = false;
    }

    public double CurrentRate
    {
        get
        {
            // Return 0 immediately when paused (libtorrent behavior)
            if (_isPaused)
                return 0;

            FlushPending();
            PruneOldSamples();
            var windowSeconds = _windowTicks / (double)TimeSpan.TicksPerSecond;
            var total = Interlocked.Read(ref _windowTotal);
            return windowSeconds > 0 ? total / windowSeconds : 0;
        }
    }

    /// <summary>
    /// Smoothed rate that decays exponentially rather than dropping immediately to 0.
    /// Use this for ETA calculations to prevent jumpy display during network gaps.
    /// When active: blends current rate with previous smoothed rate.
    /// When inactive: decays exponentially over time.
    /// When paused: returns 0 immediately (libtorrent behavior).
    /// </summary>
    public double SmoothedRate
    {
        get
        {
            // Return 0 immediately when paused (libtorrent behavior)
            if (_isPaused)
                return 0;

            var currentRate = CurrentRate;
            var now = DateTime.UtcNow.Ticks;

            if (currentRate > 0)
            {
                if (!_smoothedRateInitialized)
                {
                    // First non-zero rate - initialize directly without blending
                    _smoothedRate = currentRate;
                    _smoothedRateInitialized = true;
                }
                else
                {
                    // Active transfer - blend current rate with smoothed using EMA
                    _smoothedRate = BlendAlpha * currentRate + (1 - BlendAlpha) * _smoothedRate;
                }
                _lastDecayTimestamp = now;
            }
            else if (_smoothedRate > 0)
            {
                // No current samples - apply exponential decay based on time since last decay
                var lastDecay = Interlocked.Read(ref _lastDecayTimestamp);
                var elapsedSeconds = (now - lastDecay) / (double)TimeSpan.TicksPerSecond;

                if (elapsedSeconds >= 1.0)
                {
                    // Only decay once per second to avoid rapid collapse
                    var decayMultiplier = Math.Pow(DecayFactor, elapsedSeconds);
                    _smoothedRate *= decayMultiplier;
                    Interlocked.Exchange(ref _lastDecayTimestamp, now);

                    // Floor at 1 KB/s to eventually reach 0 (but not too aggressively)
                    if (_smoothedRate < 1000) _smoothedRate = 0;
                }
            }

            return _smoothedRate;
        }
    }

    public void AddSample(int bytes)
    {
        if (bytes <= 0) return;

        var pending = Interlocked.Add(ref _pendingBytes, bytes);
        Interlocked.Exchange(ref _lastSampleTimestamp, DateTime.UtcNow.Ticks);

        if (pending >= CoalesceThreshold)
        {
            FlushPending();
        }
    }

    /// <summary>
    /// Commits any pending bytes to the sample queue and prunes old samples.
    /// Called periodically by BackgroundTaskManager.OnStatsUpdateTimer.
    /// </summary>
    public void Flush()
    {
        FlushPending();
        PruneOldSamples();
    }

    private void FlushPending()
    {
        var bytes = Interlocked.Exchange(ref _pendingBytes, 0);
        if (bytes > 0)
        {
            var now = DateTime.UtcNow.Ticks;
            _samples.Enqueue((now, bytes));
            Interlocked.Add(ref _windowTotal, bytes);

            // Prune only if enough time has passed
            if (now - Interlocked.Read(ref _lastPruneTimestamp) > PruneIntervalTicks)
            {
                PruneOldSamples();
                Interlocked.Exchange(ref _lastPruneTimestamp, now);
            }
        }
    }

    private void PruneOldSamples()
    {
        var cutoff = DateTime.UtcNow.Ticks - _windowTicks;
        while (_samples.TryPeek(out var sample) && sample.timestampTicks < cutoff)
        {
            if (_samples.TryDequeue(out var removed))
            {
                Interlocked.Add(ref _windowTotal, -removed.bytes);
            }
        }
    }
}

public class PeerTransferStats
{
    // Total traffic (includes protocol overhead)
    private long _downloaded;
    private long _uploaded;
    private readonly SlidingWindowRateCalculator _downloadRate;
    private readonly SlidingWindowRateCalculator _uploadRate;

    // Payload only (actual file data)
    private long _payloadDownloaded;
    private long _payloadUploaded;
    private readonly SlidingWindowRateCalculator _payloadDownloadRate;
    private readonly SlidingWindowRateCalculator _payloadUploadRate;

    public long Downloaded => Interlocked.Read(ref _downloaded);
    public long Uploaded => Interlocked.Read(ref _uploaded);
    public double DownloadRate => _downloadRate.CurrentRate;
    public double UploadRate => _uploadRate.CurrentRate;

    public long PayloadDownloaded => Interlocked.Read(ref _payloadDownloaded);
    public long PayloadUploaded => Interlocked.Read(ref _payloadUploaded);
    public double PayloadDownloadRate => _payloadDownloadRate.CurrentRate;
    public double PayloadUploadRate => _payloadUploadRate.CurrentRate;

    public PeerTransferStats()
    {
        _downloadRate = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        _uploadRate = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        _payloadDownloadRate = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        _payloadUploadRate = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
    }

    public void AddDownload(int bytes)
    {
        Interlocked.Add(ref _downloaded, bytes);
        _downloadRate.AddSample(bytes);
    }

    public void AddUpload(int bytes)
    {
        Interlocked.Add(ref _uploaded, bytes);
        _uploadRate.AddSample(bytes);
    }

    public void AddPayloadDownload(int bytes)
    {
        Interlocked.Add(ref _payloadDownloaded, bytes);
        _payloadDownloadRate.AddSample(bytes);
    }

    public void AddPayloadUpload(int bytes)
    {
        Interlocked.Add(ref _payloadUploaded, bytes);
        _payloadUploadRate.AddSample(bytes);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _downloaded, 0);
        Interlocked.Exchange(ref _uploaded, 0);
        Interlocked.Exchange(ref _payloadDownloaded, 0);
        Interlocked.Exchange(ref _payloadUploaded, 0);
    }
}
