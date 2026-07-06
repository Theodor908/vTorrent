// src/vTorrent.CLI/Commands/Category/ListCategoriesCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Category;

public static class ListCategoriesCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List all categories");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.GetCategoriesAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var categories = result.Data!;
                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(categories);
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(categories.Select(c =>
                            (c["id"]?.GetValue<int>() ?? 0).ToString()));
                        break;
                    default:
                        if (categories.Count == 0)
                        {
                            formatter.WriteSummary("No categories found.");
                        }
                        else
                        {
                            var table = new Table();
                            table.AddColumn("ID");
                            table.AddColumn("Name");
                            table.AddColumn("Color");
                            table.AddColumn("Save Path");

                            foreach (var cat in categories)
                            {
                                table.AddRow(
                                    (cat["id"]?.GetValue<int>() ?? 0).ToString(),
                                    Markup.Escape(cat["name"]?.GetValue<string>() ?? ""),
                                    Markup.Escape(cat["color"]?.GetValue<string>() ?? ""),
                                    Markup.Escape(cat["savePath"]?.GetValue<string>() ?? ""));
                            }

                            formatter.WriteTable(table);
                        }
                        formatter.WriteSummary($"{categories.Count} category(ies)");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
