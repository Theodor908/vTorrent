// src/vTorrent.CLI/Commands/Profile/ListProfilesCommand.cs
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using Spectre.Console;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Profile;

public static class ListProfilesCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List performance profiles");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var profilesResult = client.GetProfilesAsync().GetAwaiter().GetResult();
                if (!profilesResult.IsSuccess) return CommandHelper.WriteApiError(profilesResult, formatter);

                var activeResult = client.GetActiveProfileAsync().GetAwaiter().GetResult();
                var activeName = activeResult.IsSuccess ? activeResult.Data!.Name : null;

                var profiles = profilesResult.Data!;

                if (formatter.Mode == OutputMode.Json)
                {
                    var items = profiles.Select(p => new
                    {
                        name = p.Name,
                        color = p.Color,
                        scope = p.Scope,
                        isActive = string.Equals(p.Name, activeName, System.StringComparison.OrdinalIgnoreCase)
                    });
                    formatter.WriteJson(items);
                    return 0;
                }

                if (formatter.Mode == OutputMode.Quiet)
                {
                    formatter.WriteQuiet(profiles.Select(p => p.Name));
                    return 0;
                }

                var table = new Table();
                table.AddColumn("Name");
                table.AddColumn("Color");
                table.AddColumn("Active");

                foreach (var p in profiles)
                {
                    var isActive = string.Equals(p.Name, activeName, System.StringComparison.OrdinalIgnoreCase);
                    var colorSwatch = $"[{p.Color}]██[/]";
                    table.AddRow(
                        Markup.Escape(p.Name),
                        colorSwatch,
                        isActive ? "[green]●[/]" : "");
                }

                formatter.WriteTable(table);
            }

            return 0;
        });

        return command;
    }
}
