// src/vTorrent.CLI/Commands/Server/SetDefaultServerCommand.cs
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.Server;

public static class SetDefaultServerCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Server name to set as default"
        };

        var command = new Command("set-default", "Set the default server connection");
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

            if (profileManager.Get(name) == null)
            {
                formatter.WriteError($"Server '{name}' not found");
                return 1;
            }

            profileManager.SetDefault(name);

            if (json)
                formatter.WriteJson(new { @default = name });
            else
                formatter.WriteSuccess($"Default server set to '{name}'");

            return 0;
        });

        return command;
    }
}
