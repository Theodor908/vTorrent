// src/vTorrent.CLI/Commands/Dht/DhtToggleCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Dht;

public static class DhtToggleCommand
{
    public static Command Create()
    {
        var command = new Command("toggle", "Toggle DHT on/off");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.ToggleDhtAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { action = "toggled" });
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet("toggled");
                        break;
                    default:
                        formatter.WriteSuccess("DHT toggled");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
