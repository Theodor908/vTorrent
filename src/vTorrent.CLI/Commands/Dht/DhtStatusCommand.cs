// src/vTorrent.CLI/Commands/Dht/DhtStatusCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Dht;

public static class DhtStatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "Show DHT status");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.GetDhtStatusAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var status = result.Data!;
                var isRunning = status["isRunning"]?.GetValue<bool>() ?? false;
                var isEnabled = status["isEnabled"]?.GetValue<bool>() ?? false;
                var nodeCount = status["nodeCount"]?.GetValue<int>() ?? 0;

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(status);
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(isEnabled ? $"enabled {nodeCount}" : "disabled");
                        break;
                    default:
                        if (!isEnabled)
                        {
                            formatter.WriteQuiet("DHT: Disabled");
                        }
                        else
                        {
                            var state = isRunning ? "Running" : "Stopped";
                            formatter.WriteQuiet($"DHT: {state} | Nodes: {nodeCount}");
                        }
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
