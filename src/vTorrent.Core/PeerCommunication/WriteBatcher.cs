using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.PeerCommunication;

/// <summary>
/// Collects outgoing messages during a processing cycle and flushes them
/// in batches per-peer — libtorrent's cork/uncork pattern.
/// </summary>
public class WriteBatcher : IDisposable
{
    private readonly ConcurrentDictionary<IPeerConnection, List<PeerMessage>> _pending = new();

    public int PendingPeerCount => _pending.Count;

    public void QueueMessage(IPeerConnection peer, PeerMessage message)
    {
        var list = _pending.GetOrAdd(peer, _ => new List<PeerMessage>());
        lock (list)
        {
            list.Add(message);
        }
    }

    public async Task FlushAllAsync(CancellationToken ct)
    {
        if (_pending.IsEmpty) return;

        var tasks = new List<Task>(_pending.Count);
        foreach (var kvp in _pending)
        {
            var peer = kvp.Key;
            var messages = kvp.Value;

            if (!peer.IsConnected || messages.Count == 0)
                continue;

            PeerMessage[] batch;
            lock (messages)
            {
                batch = messages.ToArray();
                messages.Clear();
            }

            tasks.Add(FlushPeerAsync(peer, batch, ct));
        }

        _pending.Clear();

        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task FlushPeerAsync(IPeerConnection peer, PeerMessage[] messages, CancellationToken ct)
    {
        try
        {
            await peer.SendMessagesAsync(messages, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Peer may have disconnected — silently ignore
        }
    }

    public void Dispose()
    {
        _pending.Clear();
    }
}
