// src/vTorrent.CLI/Commands/Profile/ImportProfileCommand.cs
using System.IO;
using System.CommandLine;
using System.CommandLine.Parsing;
using Spectre.Console;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Profile;

public static class ImportProfileCommand
{
    public static Command Create()
    {
        var fileArgument = new Argument<string>("file-path")
        {
            Description = "Profile file to import (.vtprofile.json)"
        };

        var command = new Command("import", "Import a profile from .vtprofile.json");
        command.Arguments.Add(fileArgument);

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
                var result = client.ImportProfileAsync(filePath).GetAwaiter().GetResult();
                if (!result.IsSuccess) return CommandHelper.WriteApiError(result, formatter);

                var importResult = result.Data!;

                if (formatter.Mode == OutputMode.Json)
                {
                    formatter.WriteJson(importResult);
                    return 0;
                }

                formatter.WriteSuccess($"Profile \"{importResult.Name}\" imported successfully.");

                if (importResult.HadNameConflict)
                    AnsiConsole.MarkupLine($"  [yellow]Name conflict — saved as \"{Markup.Escape(importResult.Name)}\"[/]");

                if (importResult.Warnings.Length > 0)
                {
                    foreach (var w in importResult.Warnings)
                        AnsiConsole.MarkupLine($"  [yellow]Warning:[/] {Markup.Escape(w)}");
                }
            }

            return 0;
        });

        return command;
    }
}
