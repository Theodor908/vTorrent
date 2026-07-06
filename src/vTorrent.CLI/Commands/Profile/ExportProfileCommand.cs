// src/vTorrent.CLI/Commands/Profile/ExportProfileCommand.cs
using System.IO;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Profile;

public static class ExportProfileCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to export"
        };

        var fileArgument = new Argument<string>("file-path")
        {
            Description = "Output file path (e.g., performance.vtprofile.json)"
        };

        var command = new Command("export", "Export a profile to .vtprofile.json");
        command.Arguments.Add(nameArgument);
        command.Arguments.Add(fileArgument);

        command.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameArgument);
            var filePath = parseResult.GetValue(fileArgument)!;
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.ExportProfileAsync(name).GetAwaiter().GetResult();
                if (!result.IsSuccess) return CommandHelper.WriteApiError(result, formatter);

                File.WriteAllBytes(filePath, result.Data!);
                formatter.WriteSuccess($"Profile exported to {filePath}");
            }

            return 0;
        });

        return command;
    }
}
