// src/vTorrent.CLI/Commands/Server/ListServersCommand.cs
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using Spectre.Console;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.Server;

public static class ListServersCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List all server connections");

        command.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var noColor = parseResult.GetValue(GlobalOptions.NoColor);

            var formatter = new OutputFormatter(json, quiet, noColor);
            var configDir = Program.GetConfigDir();
            var profileManager = new ProfileManager(configDir);

            var profiles = profileManager.ListAll();
            var defaultName = profileManager.GetDefault();

            if (profiles.Count == 0)
            {
                if (json)
                    formatter.WriteJson(System.Array.Empty<object>());
                else if (!quiet)
                    formatter.WriteSummary("No servers configured. Use 'vtorrent server add' to create one.");
                return 0;
            }

            if (json)
            {
                var items = profiles.Select(p => new
                {
                    name = p.Key,
                    host = p.Value.Host,
                    https = p.Value.Https,
                    insecure = p.Value.Insecure,
                    username = p.Value.Username,
                    isDefault = p.Key == defaultName
                });
                formatter.WriteJson(items);
                return 0;
            }

            if (quiet)
            {
                formatter.WriteQuiet(profiles.Keys);
                return 0;
            }

            var table = new Table();
            table.AddColumn("Name");
            table.AddColumn("Host");
            table.AddColumn("HTTPS");
            table.AddColumn("Username");
            table.AddColumn("Default");

            foreach (var (name, profile) in profiles)
            {
                var isDefault = name == defaultName ? "*" : "";
                table.AddRow(
                    Markup.Escape(name),
                    Markup.Escape(profile.Host),
                    profile.Https ? "yes" : "no",
                    Markup.Escape(profile.Username),
                    isDefault);
            }

            formatter.WriteTable(table);
            return 0;
        });

        return command;
    }
}
