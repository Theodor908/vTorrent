using System;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Distributes connection slots across torrents.
/// Based on libtorrent's connection_limit enforcement.
/// </summary>
public class ConnectionAllocator
{
    private readonly object _lock = new();
    private int _totalConnections;
    private int _halfOpenConnections;

    // Global limits
    public int MaxGlobalConnections { get; set; } = 500;
    public int MaxHalfOpenConnections { get; set; } = 50;
    public int MaxConnectionsPerTorrent { get; set; } = 50;
    public int MaxUploadsPerTorrent { get; set; } = 4;

    // Current state
    public int TotalConnections
    {
        get { lock (_lock) return _totalConnections; }
    }

    public int HalfOpenConnections
    {
        get { lock (_lock) return _halfOpenConnections; }
    }

    public int AvailableSlots
    {
        get { lock (_lock) return Math.Max(0, MaxGlobalConnections - _totalConnections); }
    }

    /// <summary>
    /// Request permission to open a new connection
    /// </summary>
    /// <param name="torrent">The torrent requesting the connection</param>
    /// <param name="isHalfOpen">Whether this is a half-open (outgoing) connection</param>
    /// <returns>True if connection is allowed</returns>
    public bool TryAcquireConnection(ManagedTorrent torrent, bool isHalfOpen = true)
    {
        lock (_lock)
        {
            // Check global limit
            if (_totalConnections >= MaxGlobalConnections)
                return false;

            // Check half-open limit (prevents SYN flood)
            if (isHalfOpen && _halfOpenConnections >= MaxHalfOpenConnections)
                return false;

            // Check per-torrent limit
            if (torrent.ConnectedPeers >= GetAllowedConnections(torrent))
                return false;

            _totalConnections++;
            if (isHalfOpen)
                _halfOpenConnections++;

            return true;
        }
    }

    /// <summary>
    /// Try to acquire without a specific torrent (for incoming connections)
    /// </summary>
    public bool TryAcquireIncoming()
    {
        lock (_lock)
        {
            if (_totalConnections >= MaxGlobalConnections)
                return false;

            _totalConnections++;
            return true;
        }
    }

    /// <summary>
    /// Connection established (no longer half-open)
    /// </summary>
    public void ConnectionEstablished()
    {
        lock (_lock)
        {
            if (_halfOpenConnections > 0)
                _halfOpenConnections--;
        }
    }

    /// <summary>
    /// Release a connection slot
    /// </summary>
    /// <param name="wasHalfOpen">Whether the connection was still in half-open state</param>
    public void ReleaseConnection(bool wasHalfOpen = false)
    {
        lock (_lock)
        {
            if (_totalConnections > 0)
                _totalConnections--;

            if (wasHalfOpen && _halfOpenConnections > 0)
                _halfOpenConnections--;
        }
    }

    /// <summary>
    /// Calculate allowed connections for a torrent based on fair share
    /// </summary>
    public int GetAllowedConnections(ManagedTorrent torrent)
    {
        return MaxConnectionsPerTorrent;
    }

    /// <summary>
    /// Check if a torrent can accept more connections
    /// </summary>
    public bool CanAcceptConnection(ManagedTorrent torrent)
    {
        lock (_lock)
        {
            if (_totalConnections >= MaxGlobalConnections)
                return false;

            return torrent.ConnectedPeers < GetAllowedConnections(torrent);
        }
    }

    /// <summary>
    /// Check if half-open connections are available
    /// </summary>
    public bool CanOpenHalfOpen()
    {
        lock (_lock)
        {
            return _halfOpenConnections < MaxHalfOpenConnections &&
                   _totalConnections < MaxGlobalConnections;
        }
    }

    /// <summary>
    /// Get statistics snapshot
    /// </summary>
    public ConnectionStats GetStats()
    {
        lock (_lock)
        {
            return new ConnectionStats
            {
                TotalConnections = _totalConnections,
                HalfOpenConnections = _halfOpenConnections,
                MaxGlobalConnections = MaxGlobalConnections,
                MaxHalfOpenConnections = MaxHalfOpenConnections,
                AvailableSlots = Math.Max(0, MaxGlobalConnections - _totalConnections)
            };
        }
    }
}

/// <summary>
/// Connection statistics snapshot
/// </summary>
public readonly struct ConnectionStats
{
    public int TotalConnections { get; init; }
    public int HalfOpenConnections { get; init; }
    public int MaxGlobalConnections { get; init; }
    public int MaxHalfOpenConnections { get; init; }
    public int AvailableSlots { get; init; }
    public float Utilization => MaxGlobalConnections > 0
        ? (float)TotalConnections / MaxGlobalConnections
        : 0f;
}
