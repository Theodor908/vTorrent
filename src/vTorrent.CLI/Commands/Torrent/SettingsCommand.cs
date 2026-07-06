// src/vTorrent.CLI/Commands/Torrent/SettingsCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class SettingsCommand
{
    private static readonly Argument<string> HashArgument = new("hash") { Description = "Torrent info hash" };
    private static readonly Argument<string?> KeyArgument = new("key") { Description = "Setting key to read or write", Arity = ArgumentArity.ZeroOrOne };
    private static readonly Argument<string?> ValueArgument = new("value") { Description = "New value to set", Arity = ArgumentArity.ZeroOrOne };

    public static Command Create()
    {
        var command = new Command("settings", "View or modify per-torrent settings");
        AddArgumentsAndHandler(command);
        return command;
    }

    private static void AddArgumentsAndHandler(Command command)
    {
        command.Arguments.Add(HashArgument);
        command.Arguments.Add(KeyArgument);
        command.Arguments.Add(ValueArgument);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                string hash;
                try { hash = client.ResolveHashAsync(parseResult.GetValue(HashArgument)!).GetAwaiter().GetResult(); }
                catch (ApiException ex) { formatter.WriteError($"{ex.Message} ({ex.ErrorCode})"); return 1; }

                var key = parseResult.GetValue(KeyArgument);
                var value = parseResult.GetValue(ValueArgument);

                if (value != null && key != null)
                {
                    // SET mode: torrent settings <hash> <key> <value>
                    var settings = new Dictionary<string, object> { [key] = ParseValue(value) };
                    var result = client.SetTorrentSettingsAsync(hash, settings).GetAwaiter().GetResult();
                    if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                    switch (formatter.Mode)
                    {
                        case OutputMode.Json:
                            formatter.WriteJson(new { infoHash = hash, action = "set", key, value });
                            break;
                        case OutputMode.Quiet:
                            formatter.WriteQuiet(value);
                            break;
                        default:
                            formatter.WriteSuccess($"Set {key} = {value} for {hash}");
                            break;
                    }
                }
                else
                {
                    // GET mode: read from details endpoint
                    var detailsResult = client.GetTorrentDetailsAsync(hash).GetAwaiter().GetResult();
                    if (!detailsResult.IsSuccess) { return CommandHelper.WriteApiError(detailsResult, formatter); }

                    var details = detailsResult.Data;
                    if (details == null)
                    {
                        formatter.WriteError($"Torrent not found: {hash}");
                        return 1;
                    }

                    // Extract settings-like properties from the detail view
                    var settingsObj = new JsonObject
                    {
                        ["downloadBandwidthLimit"] = details.DownloadBandwidthLimit,
                        ["uploadBandwidthLimit"] = details.UploadBandwidthLimit,
                        ["maxConnections"] = details.MaxConnections,
                        ["sequentialDownload"] = details.SequentialDownload,
                        ["firstLastPiecePriority"] = details.FirstLastPiecePriority,
                        ["isAutoManaged"] = details.IsAutoManaged
                    };

                    if (key != null)
                    {
                        // Single key
                        if (settingsObj.ContainsKey(key))
                        {
                            var val = settingsObj[key];
                            switch (formatter.Mode)
                            {
                                case OutputMode.Json:
                                    formatter.WriteJson(new Dictionary<string, object?> { [key] = val?.GetValue<object>() });
                                    break;
                                case OutputMode.Quiet:
                                    formatter.WriteQuiet(val?.ToString() ?? "");
                                    break;
                                default:
                                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(key)}:[/] {Markup.Escape(val?.ToString() ?? "(null)")}");
                                    break;
                            }
                        }
                        else
                        {
                            formatter.WriteError($"Unknown setting key: {key}");
                            return 1;
                        }
                    }
                    else
                    {
                        // All settings
                        switch (formatter.Mode)
                        {
                            case OutputMode.Json:
                                formatter.WriteJson(settingsObj);
                                break;
                            case OutputMode.Quiet:
                                foreach (var kv in settingsObj)
                                    formatter.WriteQuiet($"{kv.Key}={kv.Value}");
                                break;
                            default:
                                var grid = new Grid();
                                grid.AddColumn(new GridColumn().PadRight(2));
                                grid.AddColumn();
                                foreach (var kv in settingsObj)
                                    grid.AddRow($"[dim]{Markup.Escape(kv.Key)}:[/]", Markup.Escape(kv.Value?.ToString() ?? "(null)"));
                                AnsiConsole.Write(grid);
                                break;
                        }
                    }
                }
            }

            return 0;
        });
    }

    private static object ParseValue(string value)
    {
        if (bool.TryParse(value, out var b)) return b;
        if (long.TryParse(value, out var l)) return l;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return value;
    }
}
