// src/vTorrent.CLI/Commands/Session/SessionSettingsCommand.cs
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Nodes;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Session;

public static class SessionSettingsCommand
{
    private static readonly Argument<string?> KeyArgument = new("key") { Description = "Setting key to read or write", Arity = ArgumentArity.ZeroOrOne };
    private static readonly Argument<string?> ValueArgument = new("value") { Description = "New value to set", Arity = ArgumentArity.ZeroOrOne };

    public static Command Create()
    {
        var command = new Command("settings", "View or modify session settings");
        command.Arguments.Add(KeyArgument);
        command.Arguments.Add(ValueArgument);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var key = parseResult.GetValue(KeyArgument);
                var value = parseResult.GetValue(ValueArgument);

                if (value != null && key != null)
                {
                    // SET mode
                    var settings = new JsonObject { [key] = JsonValue.Create(ParseValue(value)) };
                    var result = client.UpdateSessionSettingsAsync(settings).GetAwaiter().GetResult();
                    if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                    switch (formatter.Mode)
                    {
                        case OutputMode.Json:
                            formatter.WriteJson(new { action = "set", key, value });
                            break;
                        case OutputMode.Quiet:
                            formatter.WriteQuiet(value);
                            break;
                        default:
                            formatter.WriteSuccess($"Set {key} = {value}");
                            break;
                    }
                }
                else
                {
                    // GET mode
                    var result = client.GetSessionSettingsAsync().GetAwaiter().GetResult();
                    if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                    var settings = result.Data!;
                    if (key != null)
                    {
                        // Single key
                        if (settings.ContainsKey(key))
                        {
                            var val = settings[key];
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
                                formatter.WriteJson(settings);
                                break;
                            case OutputMode.Quiet:
                                foreach (var kv in settings)
                                    formatter.WriteQuiet($"{kv.Key}={kv.Value}");
                                break;
                            default:
                                var grid = new Grid();
                                grid.AddColumn(new GridColumn().PadRight(2));
                                grid.AddColumn();
                                foreach (var kv in settings)
                                    grid.AddRow($"[dim]{Markup.Escape(kv.Key)}:[/]", Markup.Escape(kv.Value?.ToString() ?? "(null)"));
                                AnsiConsole.Write(grid);
                                break;
                        }
                    }
                }
            }

            return 0;
        });

        return command;
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
