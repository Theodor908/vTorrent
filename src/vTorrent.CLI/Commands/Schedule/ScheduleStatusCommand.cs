// src/vTorrent.CLI/Commands/Schedule/ScheduleStatusCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using Spectre.Console;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Schedule;

public static class ScheduleStatusCommand
{
    private static readonly string[] DayLabels = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    public static Command Create()
    {
        var command = new Command("status", "Show schedule and active profile status");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var activeResult = client.GetActiveProfileAsync().GetAwaiter().GetResult();
                if (!activeResult.IsSuccess) return CommandHelper.WriteApiError(activeResult, formatter);

                var active = activeResult.Data!;

                if (formatter.Mode == OutputMode.Json)
                {
                    formatter.WriteJson(active);
                    return 0;
                }

                if (formatter.Mode == OutputMode.Quiet)
                {
                    formatter.WriteQuiet(active.ScheduleEnabled ? "enabled" : "disabled");
                    return 0;
                }

                var statusLabel = active.ScheduleEnabled ? "[green]enabled[/]" : "[dim]disabled[/]";
                AnsiConsole.MarkupLine($"  Schedule: {statusLabel}");

                if (active.ScheduleEnabled)
                {
                    var scheduleResult = client.GetScheduleAsync().GetAwaiter().GetResult();
                    if (scheduleResult.IsSuccess)
                    {
                        var now = DateTime.Now;
                        var dayIndex = MapDayOfWeek(now.DayOfWeek);
                        var hour = now.Hour;
                        var grid = scheduleResult.Data!.Grid;

                        if (dayIndex < grid.Length && hour < grid[dayIndex].Length)
                        {
                            var cell = grid[dayIndex][hour];
                            var cellLabel = cell.Mode switch
                            {
                                "SeedOnly" => "Seed Only",
                                "Paused" => "Paused",
                                _ => $"Profile \"{cell.ProfileName ?? "Balanced"}\""
                            };
                            AnsiConsole.MarkupLine($"  Current cell: {DayLabels[dayIndex]} {hour:D2}:00 → {Markup.Escape(cellLabel)}");
                        }
                    }
                }

                AnsiConsole.MarkupLine($"  Active profile: {Markup.Escape(active.Name)} [{active.Color}]██[/]");
            }

            return 0;
        });

        return command;
    }

    private static int MapDayOfWeek(DayOfWeek dow) =>
        dow == DayOfWeek.Sunday ? 6 : (int)dow - 1;
}
