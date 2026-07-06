using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.ApiKey;

public static class SetApiKeyCommand
{
    public static Command Create()
    {
        var keyArg = new Argument<string>("key")
        {
            Description = "The API key to save"
        };
        var command = new Command("set", "Save an API key for the current profile (local only)");
        command.Arguments.Add(keyArg);

        command.SetAction(parseResult =>
        {
            var apiKey = parseResult.GetValue(keyArg);
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
                formatter.WriteError("No profile configured. Run 'vtorrent profile add' first.");
                return 1;
            }

            tokenStore.SaveApiKey(profileName, apiKey);

            if (json)
                formatter.WriteJson(new { profile = profileName, apiKeySet = true });
            else if (!quiet)
                formatter.WriteSuccess($"API key saved for profile '{profileName}'. Future requests will use this key.");

            return 0;
        });

        return command;
    }
}
