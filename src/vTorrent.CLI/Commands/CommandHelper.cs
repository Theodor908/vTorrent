// src/vTorrent.CLI/Commands/CommandHelper.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http;
using System.Threading.Tasks;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands;

public static class CommandHelper
{
    private static readonly HttpClient s_healthProbe = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    /// <summary>
    /// Quick unauthenticated liveness check against /api/v1/health.
    /// </summary>
    public static async Task<bool> CheckServerHealthAsync(ProfileEntry profile)
    {
        try
        {
            var scheme = profile.Https ? "https" : "http";
            var response = await s_healthProbe.GetAsync($"{scheme}://{profile.Host}/api/v1/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Async version with server liveness pre-check. Use in commands that support async.
    /// Falls back to the sync version if health check passes.
    /// </summary>
    public static async Task<(VTorrentClient? client, OutputFormatter formatter, string? error)>
        CreateClientAndFormatterAsync(ParseResult parseResult)
    {
        var configDir = Program.GetConfigDir();
        var profileManager = new ProfileManager(configDir);
        var hostOverride = parseResult.GetValue(GlobalOptions.Host);
        var profileName = parseResult.GetValue(GlobalOptions.Profile) ?? profileManager.GetDefault();
        var insecure = parseResult.GetValue(GlobalOptions.Insecure);

        ProfileEntry? profile;
        if (hostOverride != null)
            profile = new ProfileEntry { Host = hostOverride, Https = true, Insecure = insecure };
        else
            profile = profileName != null ? profileManager.Get(profileName) : null;

        if (profile != null && !await CheckServerHealthAsync(profile))
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var noColor = parseResult.GetValue(GlobalOptions.NoColor);
            var formatter = new OutputFormatter(json, quiet, noColor);
            return (null, formatter, "Not connected. Use 'connect' to connect to a server.");
        }

        return CreateClientAndFormatter(parseResult);
    }

    public static (VTorrentClient? client, OutputFormatter formatter, string? error)
        CreateClientAndFormatter(ParseResult parseResult)
    {
        var configDir = Program.GetConfigDir();
        var profileManager = new ProfileManager(configDir);
        var tokenStore = new TokenStore(configDir);

        var profileName = parseResult.GetValue(GlobalOptions.Profile) ?? profileManager.GetDefault();
        var hostOverride = parseResult.GetValue(GlobalOptions.Host);
        var tokenOverride = parseResult.GetValue(GlobalOptions.Token);
        var insecure = parseResult.GetValue(GlobalOptions.Insecure);
        var timeout = parseResult.GetValue(GlobalOptions.Timeout);
        var json = parseResult.GetValue(GlobalOptions.Json);
        var quiet = parseResult.GetValue(GlobalOptions.Quiet);
        var noColor = parseResult.GetValue(GlobalOptions.NoColor);

        var formatter = new OutputFormatter(json, quiet, noColor);

        ProfileEntry? profile;
        if (hostOverride != null)
        {
            profile = new ProfileEntry { Host = hostOverride, Https = true, Insecure = insecure };
            profileName ??= hostOverride;
        }
        else
        {
            profile = profileName != null ? profileManager.Get(profileName) : null;
        }

        if (profile == null)
            return (null, formatter, "Not connected. Run 'vtorrent server add' or use --host.");

        if (tokenOverride != null)
        {
            tokenStore.Save(profileName!, tokenOverride, "", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        }
        else
        {
            var storedToken = tokenStore.Load(profileName!);
            if (storedToken == null)
                return (null, formatter, $"Not logged in to '{profileName}'. Run 'vtorrent login' first.");
            if (storedToken.IsExpired)
                return (null, formatter, $"Session expired for '{profileName}'. Run 'vtorrent logout' then 'vtorrent login' to re-authenticate.");

            if (storedToken.IsExpiringSoon && !json && !quiet)
            {
                var stderr = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
                stderr.MarkupLine("[yellow]Session expiring soon. Consider running 'vtorrent login' to refresh.[/]");
            }
        }

        var client = new VTorrentClient(profile, profileName!, tokenStore, insecure, timeoutSeconds: timeout, forceJwt: tokenOverride != null);

        return (client, formatter, null);
    }

    public static string EnrichErrorMessage(string error, string? errorCode, int statusCode = 0)
    {
        return errorCode switch
        {
            "CONNECTION_ERROR" =>
                $"{error}\n" +
                "  Hint: Is the daemon running? Start it with 'vtorrent serve'.\n" +
                "  Check your servers with 'vtorrent server list'.",
            "TIMEOUT" =>
                $"{error}\n" +
                "  Hint: The server didn't respond in time. Try --timeout <seconds> for a longer wait.",
            "IP_BANNED" =>
                $"{error}\n" +
                "  Hint: Your IP is temporarily banned after too many failed login attempts.\n" +
                "  Wait for the ban to expire, or connect from a different network.",
            "SECURITY_VIOLATION" =>
                $"{error}\n" +
                "  Hint: Request blocked by server security policy.",
            _ when statusCode == 401 =>
                $"{error}\n" +
                "  Hint: Your session may have expired. Run 'vtorrent login' to re-authenticate.",
            _ when statusCode == 403 =>
                $"{error}\n" +
                "  Hint: Permission denied. Check your credentials with 'vtorrent login'.",
            _ => $"{error} ({errorCode})"
        };
    }

    public static int WriteApiError<T>(ApiResult<T> result, OutputFormatter formatter)
    {
        formatter.WriteError(EnrichErrorMessage(result.Error!, result.ErrorCode, result.StatusCode));
        return 1;
    }
}
