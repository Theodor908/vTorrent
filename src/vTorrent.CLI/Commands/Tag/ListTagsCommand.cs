// src/vTorrent.CLI/Commands/Tag/ListTagsCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Tag;

public static class ListTagsCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List all tags");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.GetTagsAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var tags = result.Data!;
                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(tags);
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(tags.Select(t =>
                            (t["id"]?.GetValue<int>() ?? 0).ToString()));
                        break;
                    default:
                        if (tags.Count == 0)
                        {
                            formatter.WriteSummary("No tags found.");
                        }
                        else
                        {
                            var table = new Table();
                            table.AddColumn("ID");
                            table.AddColumn("Name");
                            table.AddColumn("Color");

                            foreach (var tag in tags)
                            {
                                table.AddRow(
                                    (tag["id"]?.GetValue<int>() ?? 0).ToString(),
                                    Markup.Escape(tag["name"]?.GetValue<string>() ?? ""),
                                    Markup.Escape(tag["color"]?.GetValue<string>() ?? ""));
                            }

                            formatter.WriteTable(table);
                        }
                        formatter.WriteSummary($"{tags.Count} tag(s)");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
