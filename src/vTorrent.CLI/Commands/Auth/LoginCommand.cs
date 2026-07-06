// src/vTorrent.CLI/Commands/Auth/LoginCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.Auth;

public static class LoginCommand
{
    public static Command Create()
    {
        var userOption = new Option<string>("--user") { Description = "Username", DefaultValueFactory = _ => "admin" };

        var command = new Command("login", "Authenticate with a vTorrent server");
        command.Options.Add(userOption);

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(GlobalOptions.Profile);
            var host = parseResult.GetValue(GlobalOptions.Host);
            var insecure = parseResult.GetValue(GlobalOptions.Insecure);
            var json = parseResult.GetValue(GlobalOptions.Json);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var noColor = parseResult.GetValue(GlobalOptions.NoColor);
            var timeout = parseResult.GetValue(GlobalOptions.Timeout);
            var username = parseResult.GetValue(userOption);

            var configDir = Program.GetConfigDir();
            var profileManager = new ProfileManager(configDir);
            var tokenStore = new TokenStore(configDir);
            var formatter = new OutputFormatter(json, quiet, noColor);

            // Resolve or create profile
            var resolvedName = profileName ?? profileManager.GetDefault();
            var profileEntry = resolvedName != null ? profileManager.Get(resolvedName) : null;

            if (profileEntry == null && host != null)
            {
                resolvedName = host;
                profileEntry = new ProfileEntry { Host = host, Https = true, Insecure = insecure, Username = username };
                profileManager.Add(resolvedName, host, https: true, insecure, username);
            }

            if (profileEntry == null)
            {
                formatter.WriteError("No profile found. Use --host to specify a server, or run 'vtorrent profile add' first.");
                return 1;
            }

            // Prompt for password
            Console.Write("Password: ");
            var password = ReadPassword();
            Console.WriteLine();

            using var client = new VTorrentClient(profileEntry, resolvedName!, tokenStore, insecure, timeoutSeconds: timeout);
            var result = client.LoginAsync(username, password).GetAwaiter().GetResult();

            if (!result.IsSuccess)
            {
                formatter.WriteError($"{result.Error} ({result.ErrorCode})");
                return 1;
            }

            var (accessToken, refreshToken, expiresIn) = result.Data;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds();
            tokenStore.Save(resolvedName!, accessToken, refreshToken, expiresAt);

            if (json)
                formatter.WriteJson(new { profile = resolvedName, username, expiresIn });
            else if (!quiet)
                formatter.WriteSuccess($"Logged in to {resolvedName} as {username}");

            return 0;
        });

        return command;
    }

    private static string ReadPassword()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                password.Remove(password.Length - 1, 1);
            else if (key.Key != ConsoleKey.Backspace)
                password.Append(key.KeyChar);
        }
        return password.ToString();
    }
}
