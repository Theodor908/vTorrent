using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace vTorrent.Bench.Settings;

public sealed class MutableSettingsMonitor<T> : IOptionsMonitor<T> where T : class, new()
{
    private volatile T _current;
    private readonly List<Action<T, string?>> _listeners = new();
    private readonly object _listenerLock = new();

    public MutableSettingsMonitor() : this(new T()) { }
    public MutableSettingsMonitor(T initial) => _current = initial;

    public T CurrentValue => _current;
    public T Get(string? name) => _current;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        lock (_listenerLock)
            _listeners.Add(listener);
        return new ChangeDisposable(this, listener);
    }

    public void Update(Action<T> mutator)
    {
        var json = JsonSerializer.Serialize(_current);
        var clone = JsonSerializer.Deserialize<T>(json)!;
        mutator(clone);
        _current = clone;

        List<Action<T, string?>> snapshot;
        lock (_listenerLock)
            snapshot = new List<Action<T, string?>>(_listeners);

        foreach (var listener in snapshot)
            listener(clone, Options.DefaultName);
    }

    public void Set(T value)
    {
        _current = value;
        List<Action<T, string?>> snapshot;
        lock (_listenerLock)
            snapshot = new List<Action<T, string?>>(_listeners);
        foreach (var listener in snapshot)
            listener(value, Options.DefaultName);
    }

    private sealed class ChangeDisposable : IDisposable
    {
        private readonly MutableSettingsMonitor<T> _monitor;
        private readonly Action<T, string?> _listener;
        public ChangeDisposable(MutableSettingsMonitor<T> monitor, Action<T, string?> listener)
        { _monitor = monitor; _listener = listener; }
        public void Dispose()
        {
            lock (_monitor._listenerLock)
                _monitor._listeners.Remove(_listener);
        }
    }
}
