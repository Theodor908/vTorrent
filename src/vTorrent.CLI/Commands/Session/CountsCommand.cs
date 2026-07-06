// src/vTorrent.CLI/Commands/Session/CountsCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Session;

public static class CountsCommand
{
    public static Command Create()
    {
        var command = new Command("counts", "Show torrent counts by state");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.GetSessionCountsAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var counts = result.Data!;
                var downloading = counts["downloading"]?.GetValue<int>() ?? 0;
                var seeding = counts["seeding"]?.GetValue<int>() ?? 0;
                var paused = counts["paused"]?.GetValue<int>() ?? 0;
                var completed = counts["completed"]?.GetValue<int>() ?? 0;

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(counts);
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet($"{downloading} {seeding} {paused} {completed}");
                        break;
                    default:
                        formatter.WriteQuiet($"Downloading: {downloading} | Seeding: {seeding} | Paused: {paused} | Completed: {completed}");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
