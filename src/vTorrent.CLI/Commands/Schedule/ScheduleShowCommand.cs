// src/vTorrent.CLI/Commands/Schedule/ScheduleShowCommand.cs
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Schedule;

public static class ScheduleShowCommand
{
    private static readonly string[] DayLabels = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    public static Command Create()
    {
        var command = new Command("show", "Show the 7x24 schedule grid");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.GetScheduleAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) return CommandHelper.WriteApiError(result, formatter);

                var schedule = result.Data!;

                if (formatter.Mode == OutputMode.Json)
                {
                    formatter.WriteJson(schedule);
                    return 0;
                }

                if (formatter.Mode == OutputMode.Quiet)
                {
                    formatter.WriteQuiet(schedule.Enabled ? "enabled" : "disabled");
                    return 0;
                }

                var statusLabel = schedule.Enabled ? "[green]enabled[/]" : "[dim]disabled[/]";
                AnsiConsole.MarkupLine($"  Schedule: {statusLabel}");
                AnsiConsole.WriteLine();

                // Build abbreviation map
                var abbrevMap = BuildAbbreviations(schedule.Grid);

                // Current day/hour for highlighting
                var now = DateTime.Now;
                var currentDay = MapDayOfWeek(now.DayOfWeek);
                var currentHour = now.Hour;

                // Header row
                var header = "  Hour ";
                for (int h = 0; h < 24; h++)
                    header += $" {h:D2}";
                AnsiConsole.MarkupLine($"[dim]{header}[/]");

                // Grid rows
                for (int d = 0; d < 7; d++)
                {
                    if (d >= schedule.Grid.Length) break;
                    var row = $"  {DayLabels[d]}  ";
                    for (int h = 0; h < 24; h++)
                    {
                        if (h >= schedule.Grid[d].Length) break;
                        var cell = schedule.Grid[d][h];
                        var abbr = GetAbbreviation(cell, abbrevMap);
                        var isCurrent = d == currentDay && h == currentHour;

                        if (isCurrent)
                            row += $" [{cell.Color} bold underline]{abbr}[/]";
                        else
                            row += $" [{cell.Color}]{abbr}[/]";
                    }
                    AnsiConsole.MarkupLine(row);
                }

                // Legend
                AnsiConsole.WriteLine();
                var legendParts = new List<string>();
                foreach (var (key, abbr) in abbrevMap.OrderBy(kv => kv.Key))
                {
                    legendParts.Add($"{abbr} {key}");
                }
                AnsiConsole.MarkupLine($"  [dim]Legend: {Markup.Escape(string.Join("  ", legendParts))}[/]");
            }

            return 0;
        });

        return command;
    }

    private static Dictionary<string, string> BuildAbbreviations(ScheduleGridCell[][] grid)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in grid)
        {
            foreach (var cell in day)
            {
                var name = cell.Mode switch
                {
                    "SeedOnly" => "Seed Only",
                    "Paused" => "Paused",
                    _ => cell.ProfileName ?? "Balanced"
                };
                names.Add(name);
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedAbbrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var builtIn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Quiet"] = "QQ",
            ["Balanced"] = "BB",
            ["Performance"] = "PP",
            ["Seed Only"] = "SO",
            ["Paused"] = "PA"
        };

        foreach (var name in names)
        {
            if (builtIn.TryGetValue(name, out var abbr))
            {
                result[name] = abbr;
                usedAbbrs.Add(abbr);
            }
        }

        foreach (var name in names.Where(n => !result.ContainsKey(n)).OrderBy(n => n))
        {
            var candidate = name.Length >= 2
                ? name.Substring(0, 2).ToUpperInvariant()
                : (name + "X").Substring(0, 2).ToUpperInvariant();

            if (usedAbbrs.Contains(candidate))
            {
                for (int i = 1; i <= 9; i++)
                {
                    var alt = candidate[0].ToString() + i;
                    if (!usedAbbrs.Contains(alt))
                    {
                        candidate = alt;
                        break;
                    }
                }
            }

            result[name] = candidate;
            usedAbbrs.Add(candidate);
        }

        return result;
    }

    private static string GetAbbreviation(ScheduleGridCell cell, Dictionary<string, string> map)
    {
        var name = cell.Mode switch
        {
            "SeedOnly" => "Seed Only",
            "Paused" => "Paused",
            _ => cell.ProfileName ?? "Balanced"
        };
        return map.TryGetValue(name, out var abbr) ? abbr : "??";
    }

    private static int MapDayOfWeek(DayOfWeek dow) =>
        dow == DayOfWeek.Sunday ? 6 : (int)dow - 1;
}
