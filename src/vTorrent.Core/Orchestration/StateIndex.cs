using System;
using System.Collections.Generic;
using vTorrent.Core.Session;
using vTorrent.Core.State;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Maintains separate lists per torrent state for O(1) state-based queries.
/// Similar to libtorrent's m_torrent_lists array.
/// </summary>
public class StateIndex
{
    private readonly object _lock = new();
    private readonly Dictionary<TransferPhase, HashSet<ManagedTorrent>> _byPhase = new();
    private readonly Dictionary<UserIntent, HashSet<ManagedTorrent>> _byIntent = new();
    private readonly Dictionary<FileOperation, HashSet<ManagedTorrent>> _byFileOp = new();

    // Error torrents: those whose status.Error != null or status.MissingFiles
    private readonly HashSet<ManagedTorrent> _errorTorrents = new();

    public StateIndex()
    {
        foreach (var phase in Enum.GetValues<TransferPhase>()) _byPhase[phase] = new();
        foreach (var intent in Enum.GetValues<UserIntent>()) _byIntent[intent] = new();
        foreach (var op in Enum.GetValues<FileOperation>()) _byFileOp[op] = new();
    }

    #region Quick Access Properties

    /// <summary>
    /// Torrents currently checking/verifying
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> Checking
    {
        get
        {
            lock (_lock)
            {
                var result = new List<ManagedTorrent>(_byPhase[TransferPhase.Allocating]);
                result.AddRange(_byPhase[TransferPhase.CheckingFiles]);
                result.AddRange(_byPhase[TransferPhase.CheckingResumeData]);
                return result;
            }
        }
    }

    /// <summary>
    /// Torrents currently downloading
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> Downloading
    {
        get
        {
            lock (_lock)
            {
                return new List<ManagedTorrent>(_byPhase[TransferPhase.Downloading]);
            }
        }
    }

    /// <summary>
    /// Torrents currently seeding
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> Seeding
    {
        get
        {
            lock (_lock)
            {
                return new List<ManagedTorrent>(_byPhase[TransferPhase.Seeding]);
            }
        }
    }

    /// <summary>
    /// Paused torrents
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> Paused
    {
        get
        {
            lock (_lock)
            {
                return new List<ManagedTorrent>(_byIntent[UserIntent.Paused]);
            }
        }
    }

    /// <summary>
    /// Queued torrents (waiting for slot)
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> Queued
    {
        get
        {
            lock (_lock)
            {
                return new List<ManagedTorrent>(_byIntent[UserIntent.Queued]);
            }
        }
    }

    /// <summary>
    /// Torrents in error state
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> Error
    {
        get
        {
            lock (_lock)
            {
                return new List<ManagedTorrent>(_errorTorrents);
            }
        }
    }

    /// <summary>
    /// Stopped torrents
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> Stopped
    {
        get
        {
            lock (_lock)
            {
                return new List<ManagedTorrent>(_byPhase[TransferPhase.Idle]);
            }
        }
    }

    /// <summary>
    /// Connecting torrents (establishing connections)
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> Connecting
    {
        get
        {
            lock (_lock)
            {
                return new List<ManagedTorrent>(_byPhase[TransferPhase.Connecting]);
            }
        }
    }

    public IReadOnlyCollection<ManagedTorrent> ActiveDownloading
    {
        get { lock (_lock) return Intersect(_byPhase[TransferPhase.Downloading], _byIntent[UserIntent.Active]); }
    }

    public IReadOnlyCollection<ManagedTorrent> ActiveSeeding
    {
        get { lock (_lock) return Intersect(_byPhase[TransferPhase.Seeding], _byIntent[UserIntent.Active]); }
    }

    public IReadOnlyCollection<ManagedTorrent> QueuedByIntent
    {
        get { lock (_lock) return new List<ManagedTorrent>(_byIntent[UserIntent.Queued]); }
    }

    /// <summary>
    /// Stalled torrents — computed at query time from live rate + peer metrics
    /// sourced from <see cref="ManagedTorrent.Statistics"/> (the engine-driven bag),
    /// not from <see cref="TorrentStatus"/> which only carries orthogonal state dimensions.
    /// Paused torrents keep their phase (orthogonal model) but are not stalled.
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> StalledTorrents
    {
        get
        {
            lock (_lock)
            {
                var active = _byIntent[UserIntent.Active];
                var result = new List<ManagedTorrent>();
                foreach (var t in _byPhase[TransferPhase.Downloading])
                {
                    if (!active.Contains(t)) continue;
                    var stats = t.Statistics;
                    if (stats.PayloadDownloadRate == 0 && stats.ConnectedPeers == 0)
                        result.Add(t);
                }
                foreach (var t in _byPhase[TransferPhase.Seeding])
                {
                    if (!active.Contains(t)) continue;
                    var stats = t.Statistics;
                    if (stats.PayloadUploadRate == 0 && stats.ConnectedPeers == 0)
                        result.Add(t);
                }
                return result;
            }
        }
    }

    private static HashSet<ManagedTorrent> Intersect(HashSet<ManagedTorrent> a, HashSet<ManagedTorrent> b)
    {
        var smaller = a.Count <= b.Count ? a : b;
        var larger  = a.Count <= b.Count ? b : a;
        var result  = new HashSet<ManagedTorrent>();
        foreach (var item in smaller)
            if (larger.Contains(item)) result.Add(item);
        return result;
    }

    private static int CountIntersect(HashSet<ManagedTorrent> a, HashSet<ManagedTorrent> b)
    {
        var smaller = a.Count <= b.Count ? a : b;
        var larger  = a.Count <= b.Count ? b : a;
        var count = 0;
        foreach (var item in smaller)
            if (larger.Contains(item)) count++;
        return count;
    }

    #endregion

    #region Counts

    /// <summary>
    /// Number of actively downloading torrents. Paused torrents keep their phase
    /// under the orthogonal state model, so "downloading" here means
    /// Phase=Downloading AND Intent=Active — used for queue slot accounting.
    /// </summary>
    public int DownloadingCount
    {
        get
        {
            lock (_lock)
            {
                return CountIntersect(_byPhase[TransferPhase.Downloading], _byIntent[UserIntent.Active]);
            }
        }
    }

    /// <summary>
    /// Number of actively seeding torrents (Phase=Seeding AND Intent=Active).
    /// </summary>
    public int SeedingCount
    {
        get
        {
            lock (_lock)
            {
                return CountIntersect(_byPhase[TransferPhase.Seeding], _byIntent[UserIntent.Active]);
            }
        }
    }

    /// <summary>
    /// Number of active torrents (downloading + seeding, Intent=Active)
    /// </summary>
    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                var active = _byIntent[UserIntent.Active];
                return CountIntersect(_byPhase[TransferPhase.Downloading], active) +
                       CountIntersect(_byPhase[TransferPhase.Seeding], active);
            }
        }
    }

    /// <summary>
    /// Number of paused torrents
    /// </summary>
    public int PausedCount
    {
        get
        {
            lock (_lock)
            {
                return _byIntent[UserIntent.Paused].Count;
            }
        }
    }

    /// <summary>
    /// Number of queued torrents
    /// </summary>
    public int QueuedCount
    {
        get
        {
            lock (_lock)
            {
                return _byIntent[UserIntent.Queued].Count;
            }
        }
    }

    /// <summary>
    /// Number of checking torrents
    /// </summary>
    public int CheckingCount
    {
        get
        {
            lock (_lock)
            {
                return _byPhase[TransferPhase.Allocating].Count + _byPhase[TransferPhase.CheckingFiles].Count + _byPhase[TransferPhase.CheckingResumeData].Count;
            }
        }
    }

    /// <summary>
    /// Number of error torrents
    /// </summary>
    public int ErrorCount
    {
        get
        {
            lock (_lock)
            {
                return _errorTorrents.Count;
            }
        }
    }

    #endregion

    #region Operations

    private static bool HasError(TorrentStatus status) => status.Error.HasValue || status.MissingFiles;

    /// <summary>
    /// Add a torrent to all dimensional indexes based on its current status
    /// </summary>
    public void Add(ManagedTorrent torrent)
    {
        lock (_lock)
        {
            var status = torrent.GetStatus();
            _byPhase[status.Phase].Add(torrent);
            _byIntent[status.Intent].Add(torrent);
            _byFileOp[status.FileOp].Add(torrent);
            if (HasError(status)) _errorTorrents.Add(torrent);
        }
    }

    /// <summary>
    /// Remove a torrent from all dimensional indexes
    /// </summary>
    public void Remove(ManagedTorrent torrent)
    {
        lock (_lock)
        {
            foreach (var set in _byPhase.Values) set.Remove(torrent);
            foreach (var set in _byIntent.Values) set.Remove(torrent);
            foreach (var set in _byFileOp.Values) set.Remove(torrent);
            _errorTorrents.Remove(torrent);
        }
    }

    /// <summary>
    /// Update multi-dimensional status (moves between phase/intent/fileop/error lists)
    /// </summary>
    public void UpdateStatus(ManagedTorrent managed, TorrentStatus oldStatus, TorrentStatus newStatus)
    {
        lock (_lock)
        {
            if (oldStatus.Phase != newStatus.Phase)
            {
                _byPhase[oldStatus.Phase].Remove(managed);
                _byPhase[newStatus.Phase].Add(managed);
            }
            if (oldStatus.Intent != newStatus.Intent)
            {
                _byIntent[oldStatus.Intent].Remove(managed);
                _byIntent[newStatus.Intent].Add(managed);
            }
            if (oldStatus.FileOp != newStatus.FileOp)
            {
                _byFileOp[oldStatus.FileOp].Remove(managed);
                _byFileOp[newStatus.FileOp].Add(managed);
            }
            var oldHasError = HasError(oldStatus);
            var newHasError = HasError(newStatus);
            if (oldHasError != newHasError)
            {
                if (newHasError) _errorTorrents.Add(managed);
                else _errorTorrents.Remove(managed);
            }
        }
    }

    /// <summary>
    /// Get all active torrents (downloading + seeding + connecting, Intent=Active).
    /// Paused torrents keep their phase under the orthogonal state model and are excluded.
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> GetActiveTorrents()
    {
        lock (_lock)
        {
            var active = _byIntent[UserIntent.Active];
            var result = new List<ManagedTorrent>();
            foreach (var t in _byPhase[TransferPhase.Downloading])
                if (active.Contains(t)) result.Add(t);
            foreach (var t in _byPhase[TransferPhase.Seeding])
                if (active.Contains(t)) result.Add(t);
            foreach (var t in _byPhase[TransferPhase.Connecting])
                if (active.Contains(t)) result.Add(t);
            return result;
        }
    }

    /// <summary>
    /// Get all torrents that want peer connections (paused torrents excluded —
    /// they must not be DHT/tracker-announced).
    /// </summary>
    public IReadOnlyCollection<ManagedTorrent> GetTorrentsWantingPeers()
    {
        lock (_lock)
        {
            var active = _byIntent[UserIntent.Active];
            var result = new List<ManagedTorrent>();

            foreach (var torrent in _byPhase[TransferPhase.Downloading])
            {
                if (active.Contains(torrent) && torrent.WantsPeers)
                    result.Add(torrent);
            }

            foreach (var torrent in _byPhase[TransferPhase.Seeding])
            {
                if (active.Contains(torrent) && torrent.WantsPeers)
                    result.Add(torrent);
            }

            return result;
        }
    }

    /// <summary>
    /// Clear all state lists
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var set in _byPhase.Values) set.Clear();
            foreach (var set in _byIntent.Values) set.Clear();
            foreach (var set in _byFileOp.Values) set.Clear();
            _errorTorrents.Clear();
        }
    }

    #endregion

    #region Statistics Snapshot

    /// <summary>
    /// Get a snapshot of all state counts
    /// </summary>
    public StateCountSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            // Downloading/Seeding mean "actively transferring": paused torrents keep
            // their phase under the orthogonal state model and count only as Paused.
            var active = _byIntent[UserIntent.Active];
            return new StateCountSnapshot
            {
                Checking = _byPhase[TransferPhase.Allocating].Count
                         + _byPhase[TransferPhase.CheckingFiles].Count
                         + _byPhase[TransferPhase.CheckingResumeData].Count,
                Downloading = CountIntersect(_byPhase[TransferPhase.Downloading], active),
                Seeding = CountIntersect(_byPhase[TransferPhase.Seeding], active),
                Paused = _byIntent[UserIntent.Paused].Count,
                Queued = _byIntent[UserIntent.Queued].Count,
                Error = _errorTorrents.Count,
                Stopped = _byPhase[TransferPhase.Idle].Count,
                Connecting = _byPhase[TransferPhase.Connecting].Count
            };
        }
    }

    #endregion
}

/// <summary>
/// Snapshot of state counts
/// </summary>
public class StateCountSnapshot
{
    public int Checking { get; init; }
    public int Downloading { get; init; }
    public int Seeding { get; init; }
    public int Paused { get; init; }
    public int Queued { get; init; }
    public int Error { get; init; }
    public int Stopped { get; init; }
    public int Connecting { get; init; }

    public int Total => Checking + Downloading + Seeding + Paused + Queued + Error + Stopped + Connecting;
    public int Active => Downloading + Seeding + Connecting;
}
