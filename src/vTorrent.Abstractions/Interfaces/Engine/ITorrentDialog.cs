using System;

namespace vTorrent.Abstractions.Interfaces.Engine;

public interface ITorrentDialog
{
    void Publish<TEvent>(TEvent @event) where TEvent : notnull;
    void Subscribe<TEvent>(Action<object?, TEvent> handler);
    void Unsubscribe<TEvent>(Action<object?, TEvent> handler);
}