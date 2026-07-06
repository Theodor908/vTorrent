using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Interactive;

/// <summary>
/// Discriminated result from ConnectAsync so callers can distinguish
/// server-unreachable from token-missing/expired.
/// </summary>
public enum ConnectResult
{
    Connected,
    ServerUnreachable,
    ProfileNotFound,
    TokenMissingOrExpired,
}

public class ConnectionManager : IAsyncDisposable
{
    private readonly ProfileManager _profileManager;
    private readonly TokenStore _tokenStore;

    private VTorrentClient? _client;
    private VTorrentRealtimeClient? _realtimeClient;
    private string? _activeProfileName;
    private ProfileEntry? _activeProfile;
    private volatile bool _isConnected;

    public event Action<string>? OnNotification;
    public event Action? OnConnectionLost;

    public bool IsConnected => _isConnected;
    public VTorrentClient? Client => _client;
    public (string Name, ProfileEntry Entry)? ActiveProfile =>
        _activeProfileName != null && _activeProfile != null
            ? (_activeProfileName, _activeProfile)
            : null;

    public ConnectionManager(ProfileManager profileManager, TokenStore tokenStore)
    {
        _profileManager = profileManager;
        _tokenStore = tokenStore;
    }

    public async Task<bool> CheckHealthAsync(string? profileName)
    {
        var profile = ResolveProfile(profileName);
        if (profile == null) return false;
        return await CheckHealthAtHostAsync(profile);
    }

    public static async Task<bool> CheckHealthAtHostAsync(ProfileEntry profile)
    {
        // Try preferred scheme first, then fallback (server may not have HTTPS cert)
        var schemes = profile.Https ? new[] { "https", "http" } : new[] { "http" };
        foreach (var scheme in schemes)
        {
            try
            {
                var handler = new HttpClientHandler();
                if (scheme == "https")
                    handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                using var probe = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                var response = await probe.GetAsync($"{scheme}://{profile.Host}/api/v1/health");
                if (response.IsSuccessStatusCode) return true;
            }
            catch
            {
                // Try next scheme
            }
        }
        return false;
    }

    public async Task<ConnectResult> ConnectAsync(string? profileName = null)
    {
        profileName ??= _profileManager.GetDefault();
        var profile = ResolveProfile(profileName);
        if (profile == null || profileName == null) return ConnectResult.ProfileNotFound;

        if (!await CheckHealthAtHostAsync(profile))
            return ConnectResult.ServerUnreachable;

        var token = _tokenStore.Load(profileName);
        if (token == null || token.IsExpired)
            return ConnectResult.TokenMissingOrExpired;

        await DisconnectAsync();
        _client = new VTorrentClient(profile, profileName, _tokenStore);
        _activeProfileName = profileName;
        _activeProfile = profile;
        _isConnected = true;

        try
        {
            _realtimeClient = new VTorrentRealtimeClient(profile, profileName, _tokenStore);
            WireRealtimeEvents(_realtimeClient);
            await _realtimeClient.ConnectAsync();
        }
        catch
        {
            if (_realtimeClient != null)
            {
                await _realtimeClient.DisposeAsync();
                _realtimeClient = null;
            }
        }

        _profileManager.SetDefault(profileName);
        return ConnectResult.Connected;
    }

    public async Task<ConnectResult> ConnectToHostAsync(string host, string profileName)
    {
        _profileManager.Add(profileName, host, https: true, insecure: false, "admin");
        return await ConnectAsync(profileName);
    }

    public async Task DisconnectAsync()
    {
        _isConnected = false;

        if (_realtimeClient != null)
        {
            await _realtimeClient.DisposeAsync();
            _realtimeClient = null;
        }

        if (_client != null)
        {
            _client.Dispose();
            _client = null;
        }

        _activeProfileName = null;
        _activeProfile = null;
    }

    private void WireRealtimeEvents(VTorrentRealtimeClient rtClient)
    {
        rtClient.NotificationReceived += msg => OnNotification?.Invoke(msg);
        rtClient.ConnectionStateChanged += (state, message) =>
        {
            var isAuthFailure = message != null &&
                (message.Contains("401") || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase));

            var notification = state switch
            {
                RealtimeConnectionState.Reconnecting when isAuthFailure =>
                    "Connection lost: auth token expired. Run 'login' to re-authenticate.",
                RealtimeConnectionState.Reconnecting =>
                    $"Connection lost: {message ?? "reconnecting..."}",
                RealtimeConnectionState.Connected =>
                    "Real-time connection restored",
                RealtimeConnectionState.Disconnected when isAuthFailure =>
                    "Disconnected: auth token expired. Run 'login' to re-authenticate.",
                RealtimeConnectionState.Disconnected =>
                    $"Real-time connection closed: {message ?? "unknown reason"}",
                _ => null
            };
            if (notification != null)
                OnNotification?.Invoke(notification);

            if (state == RealtimeConnectionState.Disconnected)
            {
                _isConnected = false;
                OnConnectionLost?.Invoke();
            }
        };
    }

    /// <summary>
    /// Shows the interactive menu when no server is reachable. Returns true if user connected.
    /// </summary>
    /// <param name="showHeader">Print the "no server reachable" header. False on re-entries from failed attempts.</param>
    public async Task<bool> ShowMenuAsync(bool showHeader = true)
    {
        if (showHeader)
        {
            var defaultProfileName = _profileManager.GetDefault();
            var defaultProfile = defaultProfileName != null ? _profileManager.Get(defaultProfileName) : null;

            if (defaultProfile != null)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ No server reachable[/]");
                AnsiConsole.MarkupLine(
                    $"  [dim]Server \"{Markup.Escape(defaultProfileName!)}\" ({Markup.Escape(defaultProfile.Host)}) is not responding[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠ No server configured[/]");
            }

            Console.WriteLine();
        }

        var choices = new List<string>();
        choices.Add("Start local server");

        var profileDict = _profileManager.ListAll();
        if (profileDict.Count > 0)
            choices.Add("Connect to saved server");

        choices.Add("Connect to different server");
        choices.Add("Continue offline");

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]What would you like to do?[/]")
                .AddChoices(choices));

        if (choice == "Start local server")
        {
            return await StartDaemonAsync();
        }
        else if (choice == "Connect to saved server")
        {
            var profiles = profileDict.Select(kv => (name: kv.Key, entry: kv.Value)).ToList();
            return await ShowProfileSelectionAsync(profiles);
        }
        else if (choice == "Connect to different server")
        {
            return await PromptNewConnectionAsync();
        }
        // Continue offline
        AnsiConsole.MarkupLine("[dim]Offline mode — use 'connect' to connect later[/]");
        return false;
    }

    private async Task<bool> ShowProfileSelectionAsync(List<(string name, ProfileEntry entry)> profiles)
    {
        var choices = profiles.Select(p => $"{p.name} ({p.entry.Host})").ToList();
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select a server:[/]")
                .AddChoices(choices));

        var index = choices.IndexOf(selected);
        var (name, _) = profiles[index];

        AnsiConsole.MarkupLine($"[dim]Checking {Markup.Escape(name)}...[/]");
        var result = await ConnectAsync(name);
        if (result == ConnectResult.Connected)
        {
            AnsiConsole.MarkupLine(
                $"[green]Connected to {Markup.Escape(name)} ({Markup.Escape(profiles[index].entry.Host)})[/]");
            return true;
        }

        if (result == ConnectResult.TokenMissingOrExpired)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Server reachable but not logged in. Run 'login --server {Markup.Escape(name)}' to authenticate.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Could not connect to {Markup.Escape(name)}[/]");
        }
        return await ShowMenuAsync(showHeader: false);
    }

    private async Task<bool> PromptNewConnectionAsync()
    {
        var host = AnsiConsole.Ask<string>("[bold]Host:port[/] (e.g. myserver:8080):");
        var tempProfile = new ProfileEntry { Host = host, Https = true };

        AnsiConsole.MarkupLine($"[dim]Checking {Markup.Escape(host)}...[/]");
        if (!await CheckHealthAtHostAsync(tempProfile))
        {
            AnsiConsole.MarkupLine($"[red]Server at {Markup.Escape(host)} is not responding[/]");
            return await ShowMenuAsync(showHeader: false);
        }

        var saveName = AnsiConsole.Ask<string>("[bold]Save as server name:[/]");
        var connectResult = await ConnectToHostAsync(host, saveName);
        return connectResult == ConnectResult.Connected;
    }

    /// <summary>
    /// Spawns vtorrent serve as a detached process, polls health until ready.
    /// </summary>
    public async Task<bool> StartDaemonAsync()
    {
        var exePath = System.Environment.ProcessPath;
        if (exePath == null)
        {
            AnsiConsole.MarkupLine("[red]Cannot determine executable path.[/]");
            return false;
        }

        AnsiConsole.MarkupLine("[dim]Starting local server...[/]");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start daemon: {Markup.Escape(ex.Message)}[/]");
            return false;
        }

        // Poll health endpoint with capped backoff
        var defaultProfile = _profileManager.GetDefault();
        var profile = defaultProfile != null
            ? _profileManager.Get(defaultProfile) ?? new ProfileEntry { Host = "localhost:8080", Https = true }
            : new ProfileEntry { Host = "localhost:8080", Https = true };

        int[] delays = [500, 1000, 2000, 2000, 2000];
        foreach (var delay in delays)
        {
            await Task.Delay(delay);
            if (await CheckHealthAtHostAsync(profile))
            {
                AnsiConsole.MarkupLine("[green]Local server started successfully[/]");
                if (defaultProfile != null)
                {
                    var connectResult = await ConnectAsync(defaultProfile);
                    if (connectResult == ConnectResult.Connected) return true;
                    if (connectResult == ConnectResult.TokenMissingOrExpired)
                    {
                        AnsiConsole.MarkupLine(
                            "[yellow]Server started but not logged in. Run 'login' to authenticate.[/]");
                    }
                    return false;
                }
                AnsiConsole.MarkupLine(
                    "[yellow]Server started. Run 'login --host localhost:8080' to authenticate.[/]");
                return false;
            }
        }

        AnsiConsole.MarkupLine("[red]Server did not become ready in time. Entering offline mode.[/]");
        return false;
    }

    private ProfileEntry? ResolveProfile(string? profileName)
    {
        if (profileName == null) return null;
        return _profileManager.Get(profileName);
    }

    public ProfileManager ProfileManager => _profileManager;
    public TokenStore TokenStore => _tokenStore;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
