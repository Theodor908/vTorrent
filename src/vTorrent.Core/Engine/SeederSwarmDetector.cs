using System;
using Microsoft.Extensions.Logging;
using vTorrent.Core.Session;

namespace vTorrent.Core.Engine;

/// <summary>
/// Detects seeder swarm conditions based on libtorrent's criteria.
///
/// A seeder swarm is detected when:
/// - At least 10 peers connected
/// - At least 10 seeds connected
/// - Seeds >= 10 * Leechers (overwhelming majority are seeds)
///
/// Reference: libtorrent torrent.cpp is_seed_swarm()
/// </summary>
public class SeederSwarmDetector
{
    private readonly ILogger<SeederSwarmDetector> _logger;
    private readonly TorrentStatistics _statistics;
    private readonly Action<bool> _onStateChanged;

    private bool _isSeederSwarm;
    private DateTime _lastCheck = DateTime.MinValue;
    private int _consecutiveSeederChecks;

    // Configuration (matching libtorrent defaults)
    private const int MinPeersForSeederSwarm = 10;
    private const int MinSeedsForSeederSwarm = 10;
    private const int SeedToLeecherRatio = 10;
    private const int ConsecutiveChecksRequired = 3; // Require stability before declaring swarm
    private static readonly TimeSpan MinCheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether we're currently in a seeder swarm.
    /// </summary>
    public bool IsSeederSwarm => _isSeederSwarm;

    /// <summary>
    /// Number of seeds needed for the swarm to be considered a seeder swarm
    /// given the current number of leechers.
    /// </summary>
    public int SeedsNeeded
    {
        get
        {
            int leechers = Math.Max(0, _statistics.ConnectedPeers - _statistics.ConnectedSeeds);
            return Math.Max(MinSeedsForSeederSwarm, leechers * SeedToLeecherRatio);
        }
    }

    /// <summary>
    /// Event raised when seeder swarm state changes.
    /// </summary>
    public event EventHandler<SeederSwarmStateChangedEventArgs> SeederSwarmStateChanged;

    public SeederSwarmDetector(
        TorrentStatistics statistics,
        ILogger<SeederSwarmDetector> logger = null,
        Action<bool> onStateChanged = null)
    {
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        _logger = logger;
        _onStateChanged = onStateChanged;
    }

    /// <summary>
    /// Updates seeder swarm detection based on current peer statistics.
    /// Call this periodically (e.g., every 10-30 seconds).
    /// </summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCheck < MinCheckInterval)
            return;

        _lastCheck = now;

        int totalPeers = _statistics.ConnectedPeers;
        int seeds = _statistics.ConnectedSeeds;
        int leechers = Math.Max(0, totalPeers - seeds);

        // Check if current state matches seeder swarm criteria
        bool meetsSeederCriteria = totalPeers >= MinPeersForSeederSwarm
            && seeds >= MinSeedsForSeederSwarm
            && seeds >= leechers * SeedToLeecherRatio;

        if (meetsSeederCriteria)
        {
            _consecutiveSeederChecks++;
        }
        else
        {
            _consecutiveSeederChecks = 0;
        }

        // Require consistent seeder swarm state before declaring
        bool shouldBeSeederSwarm = _consecutiveSeederChecks >= ConsecutiveChecksRequired;

        // Check for state change
        if (shouldBeSeederSwarm != _isSeederSwarm)
        {
            _isSeederSwarm = shouldBeSeederSwarm;
            _statistics.IsSeederSwarm = _isSeederSwarm;

            if (_isSeederSwarm)
            {
                _logger?.LogInformation(
                    "Seeder swarm detected: {Seeds} seeds, {Leechers} leechers, {Total} total peers",
                    seeds, leechers, totalPeers);
            }
            else
            {
                _logger?.LogInformation(
                    "Seeder swarm ended: {Seeds} seeds, {Leechers} leechers, {Total} total peers",
                    seeds, leechers, totalPeers);
            }

            // Raise event
            SeederSwarmStateChanged?.Invoke(this, new SeederSwarmStateChangedEventArgs(_isSeederSwarm, seeds, leechers));
            _onStateChanged?.Invoke(_isSeederSwarm);
        }
    }

    /// <summary>
    /// Forces a check regardless of time interval.
    /// </summary>
    public void ForceCheck()
    {
        _lastCheck = DateTime.MinValue;
        Update();
    }

    /// <summary>
    /// Resets the detector state.
    /// </summary>
    public void Reset()
    {
        _isSeederSwarm = false;
        _consecutiveSeederChecks = 0;
        _lastCheck = DateTime.MinValue;
        _statistics.IsSeederSwarm = false;
    }

    /// <summary>
    /// Gets diagnostic information about current state.
    /// </summary>
    public string GetDiagnostics()
    {
        int totalPeers = _statistics.ConnectedPeers;
        int seeds = _statistics.ConnectedSeeds;
        int leechers = Math.Max(0, totalPeers - seeds);
        int seedsNeeded = SeedsNeeded;

        return $"IsSeederSwarm={_isSeederSwarm}, Peers={totalPeers}, Seeds={seeds}/{seedsNeeded}, " +
               $"Leechers={leechers}, Ratio={seeds}:{leechers}, Checks={_consecutiveSeederChecks}/{ConsecutiveChecksRequired}";
    }
}

/// <summary>
/// Event arguments for seeder swarm state changes.
/// </summary>
public class SeederSwarmStateChangedEventArgs : EventArgs
{
    public bool IsSeederSwarm { get; }
    public int SeedCount { get; }
    public int LeecherCount { get; }

    public SeederSwarmStateChangedEventArgs(bool isSeederSwarm, int seedCount, int leecherCount)
    {
        IsSeederSwarm = isSeederSwarm;
        SeedCount = seedCount;
        LeecherCount = leecherCount;
    }
}
