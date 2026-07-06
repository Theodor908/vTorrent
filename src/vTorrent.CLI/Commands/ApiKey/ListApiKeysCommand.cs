using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Nodes;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.ApiKey;

public static class ListApiKeysCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List all API keys on the server");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null)
            {
                formatter.WriteError(error!);
                return 1;
            }

            using (client)
            {
                var result = client.GetApiKeysAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess)
                    return CommandHelper.WriteApiError(result, formatter);

                var keys = result.Data;

                if (parseResult.GetValue(GlobalOptions.Json))
                {
                    formatter.WriteJson(keys);
                    return 0;
                }

                if (keys.Count == 0)
                {
                    if (!parseResult.GetValue(GlobalOptions.Quiet))
                        AnsiConsole.MarkupLine("[dim]No API keys found.[/]");
                    return 0;
                }

                var table = new Table();
                table.AddColumn("Prefix");
                table.AddColumn("Label");
                table.AddColumn("Created");
                table.AddColumn("Last Used");
                table.AddColumn("Status");

                foreach (var key in keys)
                {
                    var prefix = key["keyPrefix"]?.GetValue<string>() ?? "";
                    var label = key["label"]?.GetValue<string>() ?? "";
                    var createdAt = key["createdAt"]?.GetValue<long>() ?? 0;
                    var lastUsed = key["lastUsed"]?.GetValue<long?>();
                    var isRevoked = key["isRevoked"]?.GetValue<bool>() ?? false;

                    var created = DateTimeOffset.FromUnixTimeSeconds(createdAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                    var used = lastUsed.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(lastUsed.Value).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                        : "[dim]never[/]";
                    var status = isRevoked ? "[red]revoked[/]" : "[green]active[/]";

                    table.AddRow(Markup.Escape(prefix), Markup.Escape(label), created, used, status);
                }

                AnsiConsole.Write(table);
            }

            return 0;
        });

        return command;
    }
}
