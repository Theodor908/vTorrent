// src/vTorrent.CLI/Commands/Auth/LogoutCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.Auth;

public static class LogoutCommand
{
    public static Command Create()
    {
        var command = new Command("logout", "Revoke token and clear local credentials");

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(GlobalOptions.Profile);
            var json = parseResult.GetValue(GlobalOptions.Json);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var noColor = parseResult.GetValue(GlobalOptions.NoColor);
            var insecure = parseResult.GetValue(GlobalOptions.Insecure);
            var timeout = parseResult.GetValue(GlobalOptions.Timeout);

            var configDir = Program.GetConfigDir();
            var profileManager = new ProfileManager(configDir);
            var tokenStore = new TokenStore(configDir);
            var formatter = new OutputFormatter(json, quiet, noColor);

            var resolved = profileName ?? profileManager.GetDefault();
            var profileEntry = resolved != null ? profileManager.Get(resolved) : null;
            var storedToken = resolved != null ? tokenStore.Load(resolved) : null;

            if (profileEntry == null || storedToken == null)
            {
                formatter.WriteError("Not logged in.");
                return 1;
            }

            using var client = new VTorrentClient(profileEntry, resolved!, tokenStore, insecure, timeoutSeconds: timeout);
            var logoutResult = client.LogoutAsync(storedToken.RefreshToken).GetAwaiter().GetResult();
            if (!logoutResult.IsSuccess && !quiet && !json)
            {
                formatter.WriteError(
                    $"Server-side session could not be revoked ({logoutResult.ErrorCode}).\n" +
                    "  Local credentials cleared, but the server token remains active until it expires.\n" +
                    "  If the server is running, try 'vtorrent login' followed by 'vtorrent logout' again.");
            }

            tokenStore.Remove(resolved!);

            if (!quiet && !json)
                formatter.WriteSuccess($"Logged out from {resolved}");

            return 0;
        });

        return command;
    }
}
