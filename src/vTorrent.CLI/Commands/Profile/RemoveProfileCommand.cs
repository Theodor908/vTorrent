// src/vTorrent.CLI/Commands/Profile/RemoveProfileCommand.cs
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.Profile;

public static class RemoveProfileCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to remove"
        };

        var command = new Command("remove", "Remove a connection profile");
        command.Arguments.Add(nameArgument);

        command.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameArgument);
            var json = parseResult.GetValue(GlobalOptions.Json);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var noColor = parseResult.GetValue(GlobalOptions.NoColor);

            var formatter = new OutputFormatter(json, quiet, noColor);
            var configDir = Program.GetConfigDir();
            var profileManager = new ProfileManager(configDir);
            var tokenStore = new TokenStore(configDir);

            if (profileManager.Get(name) == null)
            {
                formatter.WriteError($"Profile '{name}' not found");
                return 1;
            }

            profileManager.Remove(name);
            tokenStore.Remove(name);

            if (json)
                formatter.WriteJson(new { removed = name });
            else
                formatter.WriteSuccess($"Profile '{name}' removed");

            return 0;
        });

        return command;
    }
}
