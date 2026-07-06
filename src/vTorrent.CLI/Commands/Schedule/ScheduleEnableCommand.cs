// src/vTorrent.CLI/Commands/Schedule/ScheduleEnableCommand.cs
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Schedule;

public static class ScheduleEnableCommand
{
    public static Command Create()
    {
        var command = new Command("enable", "Enable the schedule");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.ToggleScheduleAsync(true).GetAwaiter().GetResult();
                if (!result.IsSuccess) return CommandHelper.WriteApiError(result, formatter);

                formatter.WriteSuccess("Schedule enabled");
            }

            return 0;
        });

        return command;
    }
}
