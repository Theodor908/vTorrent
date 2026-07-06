using System;
using vTorrent.Core.Settings;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Unified interface for resource allocation across torrents.
/// Combines connection limiting and bandwidth management.
/// </summary>
public class ResourceAllocator : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Connection slot allocator
    /// </summary>
    public ConnectionAllocator Connections { get; }

    /// <summary>
    /// Bandwidth token bucket allocator
    /// </summary>
    public BandwidthAllocator Bandwidth { get; }

    /// <summary>
    /// Unchoke slot allocator
    /// </summary>
    public UnchokeAllocator Unchoke { get; } = new();

    public ResourceAllocator()
    {
        Connections = new ConnectionAllocator();
        Bandwidth = new BandwidthAllocator();
    }

    /// <summary>
    /// Apply settings from GlobalSettings
    /// </summary>
    public void ApplySettings(GlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Connection settings
        Connections.MaxGlobalConnections = settings.Connection.MaxGlobalConnections;
        Connections.MaxHalfOpenConnections = settings.Connection.MaxHalfOpenConnections;
        Connections.MaxConnectionsPerTorrent = settings.Connection.MaxConnectionsPerTorrent;
        Connections.MaxUploadsPerTorrent = settings.Connection.MaxUploadsPerTorrent;

        // Bandwidth settings (global)
        Bandwidth.GlobalDownloadLimit = settings.Bandwidth.GlobalDownloadLimit;
        Bandwidth.GlobalUploadLimit = settings.Bandwidth.GlobalUploadLimit;

        // Per-torrent bandwidth defaults
        Bandwidth.DefaultPerTorrentDownloadLimit = settings.Bandwidth.PerTorrentDownloadLimit;
        Bandwidth.DefaultPerTorrentUploadLimit = settings.Bandwidth.PerTorrentUploadLimit;

        // Unchoke slot settings
        Unchoke.MaxGlobalUnchokeSlots = settings.Behavior.UnchokeSlots;
    }

    /// <summary>
    /// Check if resources are available for a new connection to a torrent
    /// </summary>
    public bool CanConnect(ManagedTorrent torrent)
    {
        return Connections.CanAcceptConnection(torrent);
    }

    /// <summary>
    /// Check if half-open connections are available
    /// </summary>
    public bool CanOpenConnection()
    {
        return Connections.CanOpenHalfOpen();
    }

    /// <summary>
    /// Get combined resource statistics
    /// </summary>
    public ResourceStats GetStats()
    {
        return new ResourceStats
        {
            Connections = Connections.GetStats(),
            Bandwidth = Bandwidth.GetStats()
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Bandwidth.Dispose();
    }
}

/// <summary>
/// Combined resource statistics
/// </summary>
public readonly struct ResourceStats
{
    public ConnectionStats Connections { get; init; }
    public BandwidthStats Bandwidth { get; init; }
}
