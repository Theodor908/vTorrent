// src/vTorrent.CLI/Commands/Schedule/ScheduleImportCommand.cs
using System.IO;
using System.Linq;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Schedule;

public static class ScheduleImportCommand
{
    public static Command Create()
    {
        var fileArgument = new Argument<string>("file-path")
        {
            Description = "Schedule file to import (.vtschedule.json)"
        };

        var command = new Command("import", "Import a schedule with profiles")
        {
            fileArgument
        };

        command.SetAction(parseResult =>
        {
            var filePath = parseResult.GetValue(fileArgument)!;
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            if (!File.Exists(filePath))
            {
                formatter.WriteError($"File not found: {filePath}");
                return 1;
            }

            using (client)
            {
                var result = client.ImportScheduleAsync(filePath).GetAwaiter().GetResult();
                if (!result.IsSuccess) return CommandHelper.WriteApiError(result, formatter);

                var importResult = result.Data!;

                if (!importResult.Success)
                {
                    formatter.WriteError($"Import failed: {string.Join("; ", importResult.Warnings)}");
                    return 1;
                }

                formatter.WriteSuccess("Schedule imported successfully.");

                if (formatter.Mode == OutputMode.Json)
                {
                    formatter.WriteJson(importResult);
                }
                else if (formatter.Mode == OutputMode.Table)
                {
                    if (importResult.ImportedProfiles.Count > 0)
                        Spectre.Console.AnsiConsole.MarkupLine($"  Imported profiles: {Spectre.Console.Markup.Escape(string.Join(", ", importResult.ImportedProfiles))}");
                    if (importResult.RenamedProfiles.Count > 0)
                    {
                        foreach (var (old, newName) in importResult.RenamedProfiles)
                            Spectre.Console.AnsiConsole.MarkupLine($"  Renamed: {Spectre.Console.Markup.Escape(old)} -> {Spectre.Console.Markup.Escape(newName)}");
                    }
                    if (importResult.SkippedProfiles.Count > 0)
                        Spectre.Console.AnsiConsole.MarkupLine($"  Skipped (identical): {Spectre.Console.Markup.Escape(string.Join(", ", importResult.SkippedProfiles))}");
                    if (importResult.Warnings.Count > 0)
                    {
                        foreach (var w in importResult.Warnings)
                            Spectre.Console.AnsiConsole.MarkupLine($"  [yellow]Warning:[/] {Spectre.Console.Markup.Escape(w)}");
                    }
                }
            }

            return 0;
        });

        return command;
    }
}
