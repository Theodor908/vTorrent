// src/vTorrent.CLI/Commands/Torrent/ResumeAllCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class ResumeAllCommand
{
    public static Command Create()
    {
        var command = new Command("resume-all", "Resume all torrents");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.ResumeAllAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { action = "resume-all" });
                        break;
                    case OutputMode.Quiet:
                        break;
                    default:
                        formatter.WriteSuccess("All torrents resumed");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
