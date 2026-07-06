using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.ApiKey;

public static class ClearApiKeyCommand
{
    public static Command Create()
    {
        var command = new Command("clear", "Remove the saved API key for the current profile (does not revoke on server)");

        command.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var noColor = parseResult.GetValue(GlobalOptions.NoColor);
            var formatter = new OutputFormatter(json, quiet, noColor);

            var configDir = Program.GetConfigDir();
            var profileManager = new ProfileManager(configDir);
            var tokenStore = new TokenStore(configDir);

            var profileName = parseResult.GetValue(GlobalOptions.Profile) ?? profileManager.GetDefault();
            if (profileName == null)
            {
                formatter.WriteError("No profile configured.");
                return 1;
            }

            tokenStore.ClearApiKey(profileName);

            if (json)
                formatter.WriteJson(new { profile = profileName, apiKeyCleared = true });
            else if (!quiet)
                formatter.WriteSuccess($"API key removed for profile '{profileName}'. Will use JWT authentication.");

            return 0;
        });

        return command;
    }
}
