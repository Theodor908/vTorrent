// src/vTorrent.CLI/Commands/Torrent/PauseAllCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class PauseAllCommand
{
    public static Command Create()
    {
        var command = new Command("pause-all", "Pause all torrents");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.PauseAllAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { action = "pause-all" });
                        break;
                    case OutputMode.Quiet:
                        break;
                    default:
                        formatter.WriteSuccess("All torrents paused");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
