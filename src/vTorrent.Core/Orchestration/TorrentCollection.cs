using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Hybrid storage for torrents: List for iteration + Dictionary for O(1) lookup.
/// Similar to libtorrent's torrent_list&lt;T&gt;.
/// Thread-safe for concurrent read access.
/// </summary>
public class TorrentCollection : IEnumerable<ManagedTorrent>
{
    private readonly object _lock = new();
    private readonly List<ManagedTorrent> _list = new();
    private readonly Dictionary<string, ManagedTorrent> _byInfoHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ManagedTorrent> _byObfuscatedHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ManagedTorrent> _byReq2Hash = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Number of torrents in the collection
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _list.Count;
            }
        }
    }

    /// <summary>
    /// Find a torrent by info hash - O(1)
    /// </summary>
    public ManagedTorrent? Find(string infoHash)
    {
        lock (_lock)
        {
            return _byInfoHash.TryGetValue(infoHash, out var torrent) ? torrent : null;
        }
    }

    /// <summary>
    /// Try to get a torrent by info hash
    /// </summary>
    public bool TryGet(string infoHash, [MaybeNullWhen(false)] out ManagedTorrent torrent)
    {
        lock (_lock)
        {
            return _byInfoHash.TryGetValue(infoHash, out torrent);
        }
    }

    /// <summary>
    /// Find a torrent by obfuscated hash (for encrypted handshakes)
    /// </summary>
    public ManagedTorrent? FindByObfuscatedHash(string hash)
    {
        lock (_lock)
        {
            return _byObfuscatedHash.TryGetValue(hash, out var torrent) ? torrent : null;
        }
    }

    /// <summary>
    /// Find a torrent by req2 hash (for MSE inbound identification) - O(1)
    /// </summary>
    public ManagedTorrent? FindByReq2Hash(string hash)
    {
        lock (_lock)
        {
            return _byReq2Hash.TryGetValue(hash, out var torrent) ? torrent : null;
        }
    }

    /// <summary>
    /// Check if torrent exists
    /// </summary>
    public bool Exists(string infoHash)
    {
        lock (_lock)
        {
            return _byInfoHash.ContainsKey(infoHash);
        }
    }

    /// <summary>
    /// Add a torrent to the collection
    /// </summary>
    public void Add(ManagedTorrent torrent)
    {
        if (torrent == null)
            throw new ArgumentNullException(nameof(torrent));

        lock (_lock)
        {
            if (_byInfoHash.ContainsKey(torrent.InfoHash))
                throw new InvalidOperationException($"Torrent {torrent.InfoHash} already exists");

            // Compute MSE/BEP 8 hashes if not already set
            if (string.IsNullOrEmpty(torrent.Req2Hash) || string.IsNullOrEmpty(torrent.ObfuscatedHash))
            {
                var infoHashBytes = Convert.FromHexString(torrent.InfoHash);
                if (string.IsNullOrEmpty(torrent.Req2Hash))
                    torrent.Req2Hash = Convert.ToHexString(
                        PeerCommunication.Encryption.Primitives.MseKeyDerivation.ComputeReq2Hash(infoHashBytes));
                if (string.IsNullOrEmpty(torrent.ObfuscatedHash))
                    torrent.ObfuscatedHash = Convert.ToHexString(
                        PeerCommunication.Encryption.Primitives.MseKeyDerivation.ComputeTrackerObfuscatedHash(infoHashBytes));
            }

            _list.Add(torrent);
            _byInfoHash[torrent.InfoHash] = torrent;

            if (!string.IsNullOrEmpty(torrent.ObfuscatedHash))
            {
                _byObfuscatedHash[torrent.ObfuscatedHash] = torrent;
            }

            if (!string.IsNullOrEmpty(torrent.Req2Hash))
            {
                _byReq2Hash[torrent.Req2Hash] = torrent;
            }
        }
    }

    /// <summary>
    /// Remove a torrent from the collection
    /// </summary>
    public bool Remove(string infoHash)
    {
        lock (_lock)
        {
            if (!_byInfoHash.TryGetValue(infoHash, out var torrent))
                return false;

            _list.Remove(torrent);
            _byInfoHash.Remove(infoHash);

            if (!string.IsNullOrEmpty(torrent.ObfuscatedHash))
            {
                _byObfuscatedHash.Remove(torrent.ObfuscatedHash);
            }

            if (!string.IsNullOrEmpty(torrent.Req2Hash))
            {
                _byReq2Hash.Remove(torrent.Req2Hash);
            }

            return true;
        }
    }

    /// <summary>
    /// Remove a torrent from the collection
    /// </summary>
    public bool Remove(ManagedTorrent torrent)
    {
        return Remove(torrent.InfoHash);
    }

    /// <summary>
    /// Get all torrents as a list (snapshot)
    /// </summary>
    public IReadOnlyList<ManagedTorrent> ToList()
    {
        lock (_lock)
        {
            return new List<ManagedTorrent>(_list);
        }
    }

    /// <summary>
    /// Get enumerator (creates snapshot to avoid locking during iteration)
    /// </summary>
    public IEnumerator<ManagedTorrent> GetEnumerator()
    {
        List<ManagedTorrent> snapshot;
        lock (_lock)
        {
            snapshot = new List<ManagedTorrent>(_list);
        }
        return snapshot.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Clear all torrents
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _list.Clear();
            _byInfoHash.Clear();
            _byObfuscatedHash.Clear();
            _byReq2Hash.Clear();
        }
    }

    /// <summary>
    /// Execute an action for each torrent (with lock held)
    /// </summary>
    public void ForEach(Action<ManagedTorrent> action)
    {
        lock (_lock)
        {
            foreach (var torrent in _list)
            {
                action(torrent);
            }
        }
    }

    /// <summary>
    /// Find torrents matching a predicate
    /// </summary>
    public IReadOnlyList<ManagedTorrent> Where(Func<ManagedTorrent, bool> predicate)
    {
        lock (_lock)
        {
            var result = new List<ManagedTorrent>();
            foreach (var torrent in _list)
            {
                if (predicate(torrent))
                    result.Add(torrent);
            }
            return result;
        }
    }
}
