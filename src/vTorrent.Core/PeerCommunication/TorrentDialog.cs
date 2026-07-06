using System;
using System.Collections.Generic;
using vTorrent.Abstractions.Interfaces.Engine;

namespace vTorrent.Core.PeerCommunication;

public class TorrentDialog : ITorrentDialog
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, Delegate> _handlers = new();

    public void Publish<TEvent>(TEvent @event) where TEvent : notnull
    {
        var eventType = typeof(TEvent);
        lock (_lock)
        {
            if (_handlers.TryGetValue(eventType, out var delegateHandlers))
            {
                // Make a copy to avoid mutation during enumeration
                foreach (Action<object?, TEvent> handler in delegateHandlers.GetInvocationList())
                {
                    try { handler(null, @event); }
                    catch (Exception ex) { /* log if you want */ }
                }
            }
        }
    }

    public void Subscribe<TEvent>(Action<object?, TEvent> handler) where TEvent : notnull
    {
        lock (_lock)
        {
            var eventType = typeof(TEvent);
            if (_handlers.TryGetValue(eventType, out var existing))
                _handlers[eventType] = Delegate.Combine(existing, handler);
            else
                _handlers[eventType] = handler;
        }
    }

    public void Unsubscribe<TEvent>(Action<object?, TEvent> handler) where TEvent : notnull
    {
        lock (_lock)
        {
            var eventType = typeof(TEvent);
            if (_handlers.TryGetValue(eventType, out var existing))
            {
                var newDelegate = Delegate.Remove(existing, handler);
                if (newDelegate == null)
                    _handlers.Remove(eventType);
                else
                    _handlers[eventType] = newDelegate;
            }
        }
    }
}