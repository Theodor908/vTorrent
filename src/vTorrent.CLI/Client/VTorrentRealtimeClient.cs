// src/vTorrent.CLI/Client/VTorrentRealtimeClient.cs
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Client;

public class VTorrentRealtimeClient : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public event Action<string>? TorrentAdded;
    public event Action<string>? TorrentRemoved;
    public event Action<string>? TorrentCompleted;
    public event Action<string>? NotificationReceived;
    public event Action? DataChanged;
    public event Action<RealtimeConnectionState, string?>? ConnectionStateChanged;

    public VTorrentRealtimeClient(ProfileEntry profile, string profileName, TokenStore tokenStore, bool insecure = false)
    {
        var token = tokenStore.Load(profileName);
        var scheme = profile.Https ? "https" : "http";
        var url = $"{scheme}://{profile.Host}/hub/torrent";

        var builder = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                // AccessTokenProvider is called before every HTTP request (negotiate, etc.)
                // and the token is sent as query string for WebSocket transport
                if (token != null)
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token.AccessToken);

                if (insecure || profile.Insecure)
                {
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler clientHandler)
                            clientHandler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                        return handler;
                    };
                }
            })
            .WithAutomaticReconnect();

        _connection = builder.Build();

        // Register event handlers
        _connection.On<System.Text.Json.JsonElement>("TorrentAdded", snapshot =>
        {
            var name = "unknown";
            try { name = snapshot.GetProperty("name").GetString() ?? name; } catch { }
            TorrentAdded?.Invoke(name);
            NotificationReceived?.Invoke($"Added: {name}");
            DataChanged?.Invoke();
        });

        _connection.On<string>("TorrentRemoved", hash =>
        {
            TorrentRemoved?.Invoke(hash);
            NotificationReceived?.Invoke($"Removed: {hash[..Math.Min(8, hash.Length)]}");
            DataChanged?.Invoke();
        });

        _connection.On<string>("TorrentCompleted", hash =>
        {
            TorrentCompleted?.Invoke(hash);
            NotificationReceived?.Invoke($"Completed: {hash[..Math.Min(8, hash.Length)]}");
            DataChanged?.Invoke();
        });

        _connection.On<System.Text.Json.JsonElement>("TorrentStatusChanged", data =>
        {
            try
            {
                var hash = data.GetProperty("infoHash").GetString() ?? "";
                var newPhase = data.GetProperty("newStatus").GetProperty("phase").GetString();
                var newIntent = data.GetProperty("newStatus").GetProperty("intent").GetString();
                NotificationReceived?.Invoke($"{hash[..Math.Min(8, hash.Length)]}: {newIntent}/{newPhase}");
            }
            catch { }
            DataChanged?.Invoke();
        });

        _connection.On<System.Text.Json.JsonElement>("TorrentError", data =>
        {
            try
            {
                var hash = data.GetProperty("infoHash").GetString() ?? "";
                var msg = data.GetProperty("errorMessage").GetString() ?? "unknown error";
                NotificationReceived?.Invoke($"Error [{hash[..Math.Min(8, hash.Length)]}]: {msg}");
            }
            catch { NotificationReceived?.Invoke("Torrent error occurred"); }
        });

        _connection.On<System.Text.Json.JsonElement>("DhtStateChanged", data =>
        {
            // DHT state changes are high-frequency infrastructure events (nodes joining/leaving).
            // Only fire DataChanged for UI refresh — don't push as user-visible notification
            // to avoid flooding the REPL with "DHT: running (N nodes)" messages.
            DataChanged?.Invoke();
        });

        _connection.Reconnecting += ex =>
        {
            ConnectionStateChanged?.Invoke(RealtimeConnectionState.Reconnecting,
                ex?.Message ?? "Connection lost, attempting to reconnect...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            ConnectionStateChanged?.Invoke(RealtimeConnectionState.Connected,
                "Real-time connection restored");
            return Task.CompletedTask;
        };

        _connection.Closed += ex =>
        {
            ConnectionStateChanged?.Invoke(RealtimeConnectionState.Disconnected,
                ex?.Message ?? "Real-time connection closed");
            return Task.CompletedTask;
        };
    }

    public async Task ConnectAsync()
    {
        await _connection.StartAsync();
    }

    public async Task SubscribeTorrentAsync(string infoHash)
    {
        await _connection.InvokeAsync("SubscribeTorrent", infoHash);
    }

    public async Task UnsubscribeTorrentAsync(string infoHash)
    {
        await _connection.InvokeAsync("UnsubscribeTorrent", infoHash);
    }

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
