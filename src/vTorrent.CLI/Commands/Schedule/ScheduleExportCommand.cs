// src/vTorrent.CLI/Commands/Schedule/ScheduleExportCommand.cs
using System.IO;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Schedule;

public static class ScheduleExportCommand
{
    public static Command Create()
    {
        var fileArgument = new Argument<string>("file-path")
        {
            Description = "Output file path (e.g., schedule.vtschedule.json)"
        };

        var command = new Command("export", "Export the current schedule with profiles")
        {
            fileArgument
        };

        command.SetAction(parseResult =>
        {
            var filePath = parseResult.GetValue(fileArgument)!;
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.ExportScheduleAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) return CommandHelper.WriteApiError(result, formatter);

                File.WriteAllBytes(filePath, result.Data!);
                formatter.WriteSuccess($"Schedule exported to {filePath}");
            }

            return 0;
        });

        return command;
    }
}
