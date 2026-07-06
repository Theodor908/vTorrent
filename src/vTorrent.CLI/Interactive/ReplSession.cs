// src/vTorrent.CLI/Interactive/ReplSession.cs
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Interactive;

public class ReplSession
{
    private readonly RootCommand _rootCommand;
    private readonly ConnectionManager _connectionManager;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<ReplNotification> _notificationQueue = new();

    public ReplSession(RootCommand rootCommand)
    {
        _rootCommand = rootCommand;
        var configDir = Program.GetConfigDir();
        var profileManager = new ProfileManager(configDir);
        var tokenStore = new TokenStore(configDir);
        _connectionManager = new ConnectionManager(profileManager, tokenStore);

        _connectionManager.OnNotification += msg =>
            _notificationQueue.Enqueue(new ReplNotification(msg, IsMarkup: false));
        _connectionManager.OnConnectionLost += () =>
            _notificationQueue.Enqueue(new ReplNotification(
                "[dim red]Connection lost. Use 'login' to re-authenticate or 'connect' to reconnect.[/]", IsMarkup: true));
    }

    public async Task RunAsync()
    {
        // Print banner
        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev";
        AnsiConsole.MarkupLine($"[bold]vTorrent CLI[/] v{version}");

        // Try to connect using default profile
        var defaultProfile = _connectionManager.ProfileManager.GetDefault();
        var connectResult = ConnectResult.ProfileNotFound;

        if (defaultProfile != null)
        {
            AnsiConsole.MarkupLine($"[dim]Checking server...[/]");
            connectResult = await _connectionManager.ConnectAsync(defaultProfile);
        }

        switch (connectResult)
        {
            case ConnectResult.Connected:
                var active = _connectionManager.ActiveProfile!.Value;
                AnsiConsole.MarkupLine(
                    $"[green]Connected to {Markup.Escape(active.Name)} ({Markup.Escape(active.Entry.Host)})[/]");
                break;
            case ConnectResult.TokenMissingOrExpired:
                AnsiConsole.MarkupLine(
                    $"[yellow]Server reachable but not logged in. Run 'login' to authenticate.[/]");
                await _connectionManager.ShowMenuAsync();
                break;
            default:
                await _connectionManager.ShowMenuAsync();
                break;
        }

        Console.WriteLine();

        // REPL loop
        while (!_cts.IsCancellationRequested)
        {
            var prompt = _connectionManager.IsConnected ? "vtorrent> " : "vtorrent (offline)> ";
            Console.Write(prompt);
            var line = Console.ReadLine();

            if (line == null) break; // EOF (Ctrl+D)

            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line is "exit" or "quit") break;

            // REPL built-ins (checked before RootCommand dispatch)
            if (line is "help" or "?")
            {
                _rootCommand.Parse(new[] { "--help" }).Invoke();
                DrainNotifications();
                continue;
            }

            if (line == "connect" || line.StartsWith("connect "))
            {
                await HandleConnectAsync(line);
                DrainNotifications();
                continue;
            }

            if (line == "clear")
            {
                Console.Clear();
                continue;
            }

            // Dispatch to command tree
            var args = SplitArgs(line);

            try
            {
                _rootCommand.Parse(args).Invoke();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            }

            // Sync ConnectionManager state after auth-changing commands.
            // Commands modify TokenStore on disk independently — the REPL must
            // re-evaluate connection state so the prompt reflects reality.
            await SyncConnectionStateAsync(args);

            DrainNotifications();
            Console.WriteLine();
        }

        // Cleanup
        await _connectionManager.DisposeAsync();
        _cts.Dispose();
    }

    private async Task HandleConnectAsync(string line)
    {
        var parts = SplitArgs(line);
        if (parts.Length == 1)
        {
            // Just "connect" — show menu
            await _connectionManager.ShowMenuAsync();
        }
        else
        {
            var target = parts[1];
            // Check if it's a saved profile name
            var profile = _connectionManager.ProfileManager.Get(target);
            if (profile != null)
            {
                AnsiConsole.MarkupLine($"[dim]Connecting to {Markup.Escape(target)}...[/]");
                var result = await _connectionManager.ConnectAsync(target);
                switch (result)
                {
                    case ConnectResult.Connected:
                        AnsiConsole.MarkupLine(
                            $"[green]Connected to {Markup.Escape(target)} ({Markup.Escape(profile.Host)})[/]");
                        break;
                    case ConnectResult.TokenMissingOrExpired:
                        AnsiConsole.MarkupLine(
                            $"[yellow]Server reachable but not logged in. Run 'login --server {Markup.Escape(target)}'.[/]");
                        break;
                    default:
                        AnsiConsole.MarkupLine($"[red]Could not connect to {Markup.Escape(target)}[/]");
                        break;
                }
            }
            else if (target.Contains(':'))
            {
                // Treat as host:port
                AnsiConsole.MarkupLine($"[dim]Connecting to {Markup.Escape(target)}...[/]");
                var saveName = AnsiConsole.Ask<string>("[bold]Save as server name:[/]");
                var result = await _connectionManager.ConnectToHostAsync(target, saveName);
                if (result == ConnectResult.Connected)
                    AnsiConsole.MarkupLine($"[green]Connected to {Markup.Escape(target)}[/]");
                else if (result == ConnectResult.TokenMissingOrExpired)
                    AnsiConsole.MarkupLine($"[yellow]Server reachable but not logged in. Run 'login'.[/]");
                else
                    AnsiConsole.MarkupLine($"[red]Could not connect to {Markup.Escape(target)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Unknown server '{Markup.Escape(target)}'. Use 'connect' for the menu.[/]");
            }
        }
    }

    /// <summary>
    /// After a command that changes auth state (login, logout, api-key set/clear),
    /// re-evaluate ConnectionManager state so the REPL prompt stays accurate.
    /// Only runs for known auth-changing commands — not on every command.
    /// </summary>
    private async Task SyncConnectionStateAsync(string[] args)
    {
        if (args.Length == 0) return;

        var cmd = args[0].ToLowerInvariant();
        var isLogin = cmd == "login";
        var isLogout = cmd == "logout";
        var isApiKeySet = cmd == "api-key" && args.Length >= 2
            && (args[1].Equals("set", StringComparison.OrdinalIgnoreCase)
                || args[1].Equals("clear", StringComparison.OrdinalIgnoreCase));

        if (isLogout)
        {
            // Token was removed on disk by logout command's TokenStore — reload and disconnect
            _connectionManager.TokenStore.Reload();
            if (_connectionManager.IsConnected)
                await _connectionManager.DisconnectAsync();
        }
        else if (isLogin || isApiKeySet)
        {
            // Auth state may have changed — reload tokens from disk since commands
            // write via their own TokenStore instance, then try to connect
            _connectionManager.TokenStore.Reload();
            if (!_connectionManager.IsConnected)
            {
                var result = await _connectionManager.ConnectAsync();
                if (result == ConnectResult.Connected)
                {
                    var active = _connectionManager.ActiveProfile!.Value;
                    AnsiConsole.MarkupLine(
                        $"[green]Connected to {Markup.Escape(active.Name)} ({Markup.Escape(active.Entry.Host)})[/]");
                }
            }
        }
    }

    private void DrainNotifications()
    {
        while (_notificationQueue.TryDequeue(out var notification))
        {
            if (notification.IsMarkup)
                AnsiConsole.MarkupLine($"  {notification.Text}");
            else
                AnsiConsole.MarkupLine($"  [dim cyan]> {Markup.Escape(notification.Text)}[/]");
        }
    }

    /// <summary>
    /// Simple arg splitter that handles quoted strings.
    /// </summary>
    private static string[] SplitArgs(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '"';

        foreach (var c in line)
        {
            if (inQuotes)
            {
                if (c == quoteChar)
                    inQuotes = false;
                else
                    current.Append(c);
            }
            else if (c == '"' || c == '\'')
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args.ToArray();
    }
}
