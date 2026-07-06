using Microsoft.Extensions.Options;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Thread-safe IOptionsMonitor bridge for vTorrent's SettingsManager.
/// Wraps mutable settings with change notification support.
/// </summary>
public class SettingsMonitor<T> : IOptionsMonitor<T> where T : class, new()
{
    private volatile T _currentValue = new();
    private readonly object _lock = new();
    private readonly List<Action<T, string?>> _listeners = new();

    public T CurrentValue => _currentValue;
    public T Get(string? name) => _currentValue;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        lock (_lock) _listeners.Add(listener);
        return new ChangeRegistration(() => { lock (_lock) _listeners.Remove(listener); });
    }

    /// <summary>
    /// Called by SettingsManager when settings are saved. Fires OnChange to all subscribers.
    /// </summary>
    public void Update(T newValue)
    {
        _currentValue = newValue;
        Action<T, string?>[] snapshot;
        lock (_lock) snapshot = _listeners.ToArray();
        foreach (var listener in snapshot)
        {
            try { listener(newValue, Options.DefaultName); }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class ChangeRegistration(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
