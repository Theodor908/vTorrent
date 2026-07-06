// src/vTorrent.CLI/Commands/Torrent/TagsCommand.cs
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using System.Text.Json.Nodes;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class TagsCommand
{
    private static readonly Argument<string> HashArgument = new("hash") { Description = "Torrent info hash" };
    private static readonly Option<string?> SetOption = new("--set") { Description = "Comma-separated tag IDs to assign" };

    public static Command Create()
    {
        var command = new Command("tags", "View or set torrent tags");
        AddArgumentsAndHandler(command);
        return command;
    }

    private static void AddArgumentsAndHandler(Command command)
    {
        command.Arguments.Add(HashArgument);
        command.Options.Add(SetOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                string hash;
                try { hash = client.ResolveHashAsync(parseResult.GetValue(HashArgument)!).GetAwaiter().GetResult(); }
                catch (ApiException ex) { formatter.WriteError($"{ex.Message} ({ex.ErrorCode})"); return 1; }

                var setVal = parseResult.GetValue(SetOption);

                if (setVal != null)
                {
                    // SET mode
                    var tagIds = setVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => int.Parse(s))
                        .ToList();

                    var result = client.SetTorrentTagsAsync(hash, tagIds).GetAwaiter().GetResult();
                    if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                    switch (formatter.Mode)
                    {
                        case OutputMode.Json:
                            formatter.WriteJson(new { infoHash = hash, action = "set-tags", tagIds });
                            break;
                        case OutputMode.Quiet:
                            formatter.WriteQuiet(hash);
                            break;
                        default:
                            formatter.WriteSuccess($"Tags updated for {hash}");
                            break;
                    }
                }
                else
                {
                    // GET mode
                    var result = client.GetTorrentTagsAsync(hash).GetAwaiter().GetResult();
                    if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                    var tags = result.Data!;
                    switch (formatter.Mode)
                    {
                        case OutputMode.Json:
                            formatter.WriteJson(tags);
                            break;
                        case OutputMode.Quiet:
                            foreach (var tag in tags)
                            {
                                var id = tag["id"]?.GetValue<int>();
                                if (id.HasValue) formatter.WriteQuiet(id.Value.ToString());
                            }
                            break;
                        default:
                            if (tags.Count == 0)
                            {
                                AnsiConsole.MarkupLine("[dim]No tags assigned.[/]");
                            }
                            else
                            {
                                var table = new Table().Border(TableBorder.Simple);
                                table.AddColumn("ID");
                                table.AddColumn("Name");
                                foreach (var tag in tags)
                                {
                                    table.AddRow(
                                        tag["id"]?.ToString() ?? "-",
                                        Markup.Escape(tag["name"]?.GetValue<string>() ?? "-")
                                    );
                                }
                                AnsiConsole.Write(table);
                            }
                            break;
                    }
                }
            }

            return 0;
        });
    }
}
