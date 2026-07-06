using System;
using System.Collections.Generic;
using System.Linq;
using vTorrent.Core.Session;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Manages torrent queue positions and ordering.
/// Torrents are ordered by queue position for auto-management.
/// </summary>
public class QueueManager
{
    private readonly object _lock = new();
    private readonly List<ManagedTorrent> _downloadQueue = new();
    private readonly List<ManagedTorrent> _seedQueue = new();

    /// <summary>
    /// Event raised when queue order changes
    /// </summary>
    public event EventHandler<QueueChangedEventArgs>? QueueChanged;

    #region Properties

    /// <summary>
    /// Number of torrents in download queue
    /// </summary>
    public int DownloadQueueCount
    {
        get
        {
            lock (_lock)
            {
                return _downloadQueue.Count;
            }
        }
    }

    /// <summary>
    /// Number of torrents in seed queue
    /// </summary>
    public int SeedQueueCount
    {
        get
        {
            lock (_lock)
            {
                return _seedQueue.Count;
            }
        }
    }

    #endregion

    #region Add/Remove

    /// <summary>
    /// Add a torrent to the appropriate queue
    /// </summary>
    public void Add(ManagedTorrent torrent)
    {
        lock (_lock)
        {
            if (torrent.IsFinished)
            {
                AddToSeedQueue(torrent);
            }
            else
            {
                AddToDownloadQueue(torrent);
            }
        }
    }

    private void AddToDownloadQueue(ManagedTorrent torrent)
    {
        // Insert at correct position based on QueuePosition
        if (torrent.QueuePosition < 0)
        {
            torrent.QueuePosition = _downloadQueue.Count;
            _downloadQueue.Add(torrent);
        }
        else
        {
            int insertAt = 0;
            for (int i = 0; i < _downloadQueue.Count; i++)
            {
                if (_downloadQueue[i].QueuePosition > torrent.QueuePosition)
                    break;
                insertAt = i + 1;
            }

            _downloadQueue.Insert(insertAt, torrent);
        }
    }

    private void AddToSeedQueue(ManagedTorrent torrent)
    {
        if (torrent.QueuePosition < 0)
        {
            torrent.QueuePosition = _seedQueue.Count;
            _seedQueue.Add(torrent);
        }
        else
        {
            int insertAt = 0;
            for (int i = 0; i < _seedQueue.Count; i++)
            {
                if (_seedQueue[i].QueuePosition > torrent.QueuePosition)
                    break;
                insertAt = i + 1;
            }

            _seedQueue.Insert(insertAt, torrent);
        }
    }

    /// <summary>
    /// Remove a torrent from queues
    /// </summary>
    public void Remove(ManagedTorrent torrent)
    {
        lock (_lock)
        {
            int downloadIndex = _downloadQueue.IndexOf(torrent);
            if (downloadIndex >= 0)
            {
                _downloadQueue.RemoveAt(downloadIndex);
                // Reorder remaining torrents
                for (int i = downloadIndex; i < _downloadQueue.Count; i++)
                {
                    _downloadQueue[i].QueuePosition = i;
                }
            }

            int seedIndex = _seedQueue.IndexOf(torrent);
            if (seedIndex >= 0)
            {
                _seedQueue.RemoveAt(seedIndex);
                for (int i = seedIndex; i < _seedQueue.Count; i++)
                {
                    _seedQueue[i].QueuePosition = i;
                }
            }
        }
    }

    #endregion

    #region Queue Position Operations

    /// <summary>
    /// Set queue position for a torrent
    /// </summary>
    public void SetQueuePosition(ManagedTorrent torrent, int position)
    {
        lock (_lock)
        {
            var queue = torrent.IsFinished ? _seedQueue : _downloadQueue;
            int currentIndex = queue.IndexOf(torrent);
            if (currentIndex < 0) return;

            position = Math.Clamp(position, 0, queue.Count - 1);

            if (currentIndex == position) return;

            queue.RemoveAt(currentIndex);
            queue.Insert(position, torrent);

            // Update all queue positions
            for (int i = 0; i < queue.Count; i++)
            {
                queue[i].QueuePosition = i;
            }

            OnQueueChanged(torrent);
        }
    }

    /// <summary>
    /// Move torrent up in queue (lower position number = higher priority)
    /// </summary>
    public void QueueUp(ManagedTorrent torrent)
    {
        lock (_lock)
        {
            var queue = torrent.IsFinished ? _seedQueue : _downloadQueue;
            int index = queue.IndexOf(torrent);
            if (index <= 0) return;

            // Swap with previous
            (queue[index], queue[index - 1]) = (queue[index - 1], queue[index]);
            queue[index].QueuePosition = index;
            queue[index - 1].QueuePosition = index - 1;

            OnQueueChanged(torrent);
        }
    }

    /// <summary>
    /// Move torrent down in queue
    /// </summary>
    public void QueueDown(ManagedTorrent torrent)
    {
        lock (_lock)
        {
            var queue = torrent.IsFinished ? _seedQueue : _downloadQueue;
            int index = queue.IndexOf(torrent);
            if (index < 0 || index >= queue.Count - 1) return;

            // Swap with next
            (queue[index], queue[index + 1]) = (queue[index + 1], queue[index]);
            queue[index].QueuePosition = index;
            queue[index + 1].QueuePosition = index + 1;

            OnQueueChanged(torrent);
        }
    }

    /// <summary>
    /// Move torrent to top of queue
    /// </summary>
    public void QueueTop(ManagedTorrent torrent)
    {
        SetQueuePosition(torrent, 0);
    }

    /// <summary>
    /// Move torrent to bottom of queue
    /// </summary>
    public void QueueBottom(ManagedTorrent torrent)
    {
        lock (_lock)
        {
            var queue = torrent.IsFinished ? _seedQueue : _downloadQueue;
            SetQueuePosition(torrent, queue.Count - 1);
        }
    }

    #endregion

    #region Queue Queries

    /// <summary>
    /// Get download queue in order
    /// </summary>
    public IReadOnlyList<ManagedTorrent> GetDownloadQueue()
    {
        lock (_lock)
        {
            return new List<ManagedTorrent>(_downloadQueue);
        }
    }

    /// <summary>
    /// Get seed queue in order
    /// </summary>
    public IReadOnlyList<ManagedTorrent> GetSeedQueue()
    {
        lock (_lock)
        {
            return new List<ManagedTorrent>(_seedQueue);
        }
    }

    /// <summary>
    /// Get next torrents to start downloading (not user-paused, in queued state)
    /// </summary>
    public IEnumerable<ManagedTorrent> GetNextDownloads(int count)
    {
        lock (_lock)
        {
            return _downloadQueue
                .Where(t => !t.UserPaused && t.GetStatus().Intent == UserIntent.Queued)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>
    /// Get next torrents to start seeding
    /// </summary>
    public IEnumerable<ManagedTorrent> GetNextSeeds(int count)
    {
        lock (_lock)
        {
            return _seedQueue
                .Where(t => !t.UserPaused && t.GetStatus().Intent == UserIntent.Queued)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>
    /// Get queued download candidates (auto-managed, not user-paused)
    /// </summary>
    public IReadOnlyList<ManagedTorrent> GetQueuedDownloadCandidates()
    {
        lock (_lock)
        {
            return _downloadQueue
                .Where(t => t.IsAutoManaged && !t.UserPaused &&
                           t.GetStatus().Intent is UserIntent.Queued or UserIntent.Paused)
                .ToList();
        }
    }

    /// <summary>
    /// Get queued seed candidates
    /// </summary>
    public IReadOnlyList<ManagedTorrent> GetQueuedSeedCandidates()
    {
        lock (_lock)
        {
            return _seedQueue
                .Where(t => t.IsAutoManaged && !t.UserPaused &&
                           t.GetStatus().Intent is UserIntent.Queued or UserIntent.Paused)
                .ToList();
        }
    }

    #endregion

    #region State Transitions

    /// <summary>
    /// Move torrent from download queue to seed queue (when finished)
    /// </summary>
    public void MoveToSeedQueue(ManagedTorrent torrent)
    {
        lock (_lock)
        {
            if (_downloadQueue.Remove(torrent))
            {
                // Reorder download queue
                for (int i = 0; i < _downloadQueue.Count; i++)
                {
                    _downloadQueue[i].QueuePosition = i;
                }

                // Add to seed queue
                torrent.QueuePosition = _seedQueue.Count;
                _seedQueue.Add(torrent);

                OnQueueChanged(torrent);
            }
        }
    }

    #endregion

    #region Persistence Support

    /// <summary>
    /// Get all queue position updates for persistence
    /// </summary>
    public IReadOnlyList<(string InfoHash, int Position)> GetQueuePositionUpdates()
    {
        lock (_lock)
        {
            var updates = new List<(string, int)>();

            for (int i = 0; i < _downloadQueue.Count; i++)
            {
                updates.Add((_downloadQueue[i].InfoHash, i));
            }

            for (int i = 0; i < _seedQueue.Count; i++)
            {
                updates.Add((_seedQueue[i].InfoHash, i));
            }

            return updates;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Clear all queues
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _downloadQueue.Clear();
            _seedQueue.Clear();
        }
    }

    private void OnQueueChanged(ManagedTorrent torrent)
    {
        QueueChanged?.Invoke(this, new QueueChangedEventArgs(torrent.InfoHash, torrent.QueuePosition));
    }

    #endregion
}

/// <summary>
/// Event args for queue changes
/// </summary>
public class QueueChangedEventArgs : EventArgs
{
    public string InfoHash { get; }
    public int NewPosition { get; }

    public QueueChangedEventArgs(string infoHash, int newPosition)
    {
        InfoHash = infoHash;
        NewPosition = newPosition;
    }
}
