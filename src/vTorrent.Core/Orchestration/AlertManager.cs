using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Base class for all alerts
/// </summary>
public abstract class Alert
{
    /// <summary>
    /// When the alert was created
    /// </summary>
    public DateTime Timestamp { get; } = DateTime.UtcNow;

    /// <summary>
    /// Alert category for filtering
    /// </summary>
    public abstract AlertCategory Category { get; }

    /// <summary>
    /// Alert priority (high priority alerts get more queue space)
    /// </summary>
    public abstract AlertPriority Priority { get; }

    /// <summary>
    /// Human-readable alert message
    /// </summary>
    public abstract string Message { get; }

    /// <summary>
    /// Info hash of related torrent (null for session-level alerts)
    /// </summary>
    public virtual string? InfoHash => null;
}

/// <summary>
/// Alert categories for filtering
/// </summary>
[Flags]
public enum AlertCategory
{
    None = 0,
    Status = 1 << 0,      // State changes, add/remove
    Error = 1 << 1,       // Errors and warnings
    Peer = 1 << 2,        // Peer connect/disconnect
    Tracker = 1 << 3,     // Tracker communication
    Storage = 1 << 4,     // Disk I/O events
    Stats = 1 << 5,       // Statistics updates
    Progress = 1 << 6,    // Piece completion
    Dht = 1 << 7,         // DHT events

    All = Status | Error | Peer | Tracker | Storage | Stats | Progress | Dht
}

/// <summary>
/// Alert priority levels
/// </summary>
public enum AlertPriority
{
    /// <summary>
    /// Normal priority, standard queue limits apply
    /// </summary>
    Normal = 0,

    /// <summary>
    /// High priority, gets 2x queue space (errors, completions)
    /// </summary>
    High = 1
}

/// <summary>
/// Double-buffered alert manager for lock-free alert posting.
/// Based on libtorrent's alert_manager.
///
/// Design:
/// - Two concurrent queues (A and B)
/// - Producers write to queue A
/// - Consumers read from queue B
/// - On PopAlerts(), queues swap atomically
/// - This prevents producers from blocking while consumers process alerts
/// </summary>
public class AlertManager
{
    private readonly ConcurrentQueue<Alert>[] _queues;
    private volatile int _writeIndex;
    private readonly object _swapLock = new();
    private readonly int _queueSizeLimit;

    // Track dropped alerts by category
    private readonly ConcurrentDictionary<AlertCategory, int> _droppedCounts = new();

    // Category filter (only alerts matching this mask are accepted)
    private AlertCategory _categoryMask = AlertCategory.All;

    /// <summary>
    /// Callback invoked when first alert arrives in empty queue.
    /// Use this to wake up UI thread for processing.
    /// </summary>
    public Action? OnAlertArrived { get; set; }

    /// <summary>
    /// Create alert manager with specified queue limit
    /// </summary>
    /// <param name="queueSizeLimit">Maximum alerts per queue (default 1000)</param>
    public AlertManager(int queueSizeLimit = 1000)
    {
        _queueSizeLimit = queueSizeLimit;
        _queues = new ConcurrentQueue<Alert>[2];
        _queues[0] = new ConcurrentQueue<Alert>();
        _queues[1] = new ConcurrentQueue<Alert>();
    }

    /// <summary>
    /// Set category filter (only matching alerts are accepted)
    /// </summary>
    public void SetCategoryMask(AlertCategory mask)
    {
        _categoryMask = mask;
    }

    /// <summary>
    /// Get current category filter
    /// </summary>
    public AlertCategory GetCategoryMask() => _categoryMask;

    /// <summary>
    /// Post an alert (thread-safe, non-blocking)
    /// </summary>
    public void Post(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        // Filter by category
        if ((_categoryMask & alert.Category) == 0)
            return;

        var queue = _queues[_writeIndex];

        // Check queue size with priority consideration
        // High priority alerts get 2x effective queue space
        int effectiveLimit = _queueSizeLimit * (1 + (int)alert.Priority);

        if (queue.Count >= effectiveLimit)
        {
            // Record dropped alert
            _droppedCounts.AddOrUpdate(alert.Category, 1, (_, count) => count + 1);
            return;
        }

        bool wasEmpty = queue.IsEmpty;
        queue.Enqueue(alert);

        // Notify on first alert (for UI wake-up)
        if (wasEmpty)
        {
            try
            {
                OnAlertArrived?.Invoke();
            }
            catch
            {
                // Don't let callback failures affect posting
            }
        }
    }

    /// <summary>
    /// Post a typed alert
    /// </summary>
    public void Post<T>(T alert) where T : Alert
    {
        Post((Alert)alert);
    }

    /// <summary>
    /// Get all pending alerts (swaps buffers)
    /// Call this periodically from UI thread or consumer
    /// </summary>
    public IReadOnlyList<Alert> PopAlerts()
    {
        lock (_swapLock)
        {
            // Swap write index atomically
            int readIndex = _writeIndex;
            _writeIndex = 1 - _writeIndex;

            // Drain the read queue
            var queue = _queues[readIndex];
            var alerts = new List<Alert>();

            while (queue.TryDequeue(out var alert))
            {
                alerts.Add(alert);
            }

            return alerts;
        }
    }

    /// <summary>
    /// Check if any alerts are pending without consuming them
    /// </summary>
    public bool HasAlerts => !_queues[_writeIndex].IsEmpty;

    /// <summary>
    /// Get approximate count of pending alerts
    /// </summary>
    public int PendingCount => _queues[_writeIndex].Count;

    /// <summary>
    /// Get and clear dropped alert counts by category
    /// </summary>
    public Dictionary<AlertCategory, int> GetAndClearDroppedCounts()
    {
        var result = new Dictionary<AlertCategory, int>();
        foreach (var kvp in _droppedCounts)
        {
            if (_droppedCounts.TryRemove(kvp.Key, out int count))
            {
                result[kvp.Key] = count;
            }
        }
        return result;
    }

    /// <summary>
    /// Get categories that had alerts dropped due to queue overflow
    /// </summary>
    public IReadOnlySet<AlertCategory> GetDroppedCategories()
    {
        var categories = new HashSet<AlertCategory>();
        foreach (var kvp in _droppedCounts)
        {
            categories.Add(kvp.Key);
        }
        return categories;
    }

    /// <summary>
    /// Clear all pending alerts
    /// </summary>
    public void Clear()
    {
        lock (_swapLock)
        {
            while (_queues[0].TryDequeue(out _)) { }
            while (_queues[1].TryDequeue(out _)) { }
        }
        _droppedCounts.Clear();
    }
}
