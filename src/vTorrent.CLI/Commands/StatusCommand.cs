using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands;

public static class StatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "Show connection, auth, and server health status");

        command.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var noColor = parseResult.GetValue(GlobalOptions.NoColor);
            var insecure = parseResult.GetValue(GlobalOptions.Insecure);
            var timeout = parseResult.GetValue(GlobalOptions.Timeout);

            var configDir = Program.GetConfigDir();
            var profileManager = new ProfileManager(configDir);
            var tokenStore = new TokenStore(configDir);
            var formatter = new OutputFormatter(json, quiet, noColor);

            var profileName = parseResult.GetValue(GlobalOptions.Profile) ?? profileManager.GetDefault();
            var profile = profileName != null ? profileManager.Get(profileName) : null;

            var profileStatus = profile != null ? $"{profileName} ({profile.Host})" : "No profile configured";
            var authStatus = "Not authenticated";
            var serverStatus = "Unknown";
            var signalrStatus = "Unknown";
            var tokenExpiry = "";

            if (profile != null)
            {
                var token = profileName != null ? tokenStore.Load(profileName) : null;
                if (token == null)
                    authStatus = "Not logged in";
                else if (token.IsExpired)
                    authStatus = "Token expired";
                else
                {
                    var expiresIn = DateTimeOffset.FromUnixTimeSeconds(token.ExpiresAt) - DateTimeOffset.UtcNow;
                    authStatus = "Authenticated";
                    tokenExpiry = expiresIn.TotalMinutes < 60
                        ? $"{(int)expiresIn.TotalMinutes}m remaining"
                        : $"{(int)expiresIn.TotalHours}h remaining";
                }

                if (token != null && !token.IsExpired)
                {
                    try
                    {
                        using var client = new VTorrentClient(profile, profileName!, tokenStore, insecure,
                            timeoutSeconds: Math.Min(timeout, 5));
                        var statsResult = client.GetStatsAsync().GetAwaiter().GetResult();
                        serverStatus = statsResult.IsSuccess ? "Running" : $"Error: {statsResult.ErrorCode}";
                    }
                    catch
                    {
                        serverStatus = "Unreachable";
                    }

                    try
                    {
                        var rtClient = new VTorrentRealtimeClient(profile, profileName!, tokenStore, insecure);
                        rtClient.ConnectAsync().GetAwaiter().GetResult();
                        signalrStatus = rtClient.IsConnected ? "Connected" : "Failed";
                        rtClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch
                    {
                        signalrStatus = "Unreachable";
                    }
                }
                else
                {
                    serverStatus = "Skipped (not authenticated)";
                    signalrStatus = "Skipped (not authenticated)";
                }
            }

            if (json)
            {
                formatter.WriteJson(new
                {
                    profile = profileName,
                    host = profile?.Host,
                    auth = authStatus,
                    tokenExpiry,
                    server = serverStatus,
                    signalr = signalrStatus
                });
            }
            else if (quiet)
            {
                var ok = serverStatus == "Running" && authStatus == "Authenticated";
                formatter.WriteQuiet(ok ? "ok" : "error");
                return ok ? 0 : 1;
            }
            else
            {
                var grid = new Grid();
                grid.AddColumn(new GridColumn().PadRight(2));
                grid.AddColumn();

                grid.AddRow("[dim]Profile:[/]", Markup.Escape(profileStatus));
                grid.AddRow("[dim]Auth:[/]", FormatStatus(authStatus, "Authenticated"));
                if (!string.IsNullOrEmpty(tokenExpiry))
                    grid.AddRow("[dim]Token:[/]", Markup.Escape(tokenExpiry));
                grid.AddRow("[dim]Server:[/]", FormatStatus(serverStatus, "Running"));
                grid.AddRow("[dim]SignalR:[/]", FormatStatus(signalrStatus, "Connected"));

                AnsiConsole.Write(grid);
            }

            return 0;
        });

        return command;
    }

    private static string FormatStatus(string status, string goodValue)
    {
        if (status == goodValue)
            return $"[green]{Markup.Escape(status)}[/]";
        if (status.StartsWith("Skip"))
            return $"[dim]{Markup.Escape(status)}[/]";
        return $"[red]{Markup.Escape(status)}[/]";
    }
}
