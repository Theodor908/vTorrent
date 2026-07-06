// src/vTorrent.CLI/Commands/Schedule/ScheduleDisableCommand.cs
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Schedule;

public static class ScheduleDisableCommand
{
    public static Command Create()
    {
        var command = new Command("disable", "Disable the schedule");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.ToggleScheduleAsync(false).GetAwaiter().GetResult();
                if (!result.IsSuccess) return CommandHelper.WriteApiError(result, formatter);

                formatter.WriteSuccess("Schedule disabled");
            }

            return 0;
        });

        return command;
    }
}
