// src/vTorrent.CLI/Commands/Torrent/QueueCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class QueueCommand
{
    private static readonly Argument<string> HashArgument = new("hash") { Description = "Torrent info hash" };
    private static readonly Argument<string> ActionArgument = new("action") { Description = "Queue action: top, bottom, up, down" };

    public static Command Create()
    {
        var command = new Command("queue", "Change torrent queue position");
        AddArgumentsAndHandler(command);
        return command;
    }

    private static void AddArgumentsAndHandler(Command command)
    {
        command.Arguments.Add(HashArgument);
        command.Arguments.Add(ActionArgument);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                string hash;
                try { hash = client.ResolveHashAsync(parseResult.GetValue(HashArgument)!).GetAwaiter().GetResult(); }
                catch (ApiException ex) { formatter.WriteError($"{ex.Message} ({ex.ErrorCode})"); return 1; }

                var action = parseResult.GetValue(ActionArgument)!;

                var validActions = new[] { "top", "bottom", "up", "down" };
                if (Array.IndexOf(validActions, action.ToLowerInvariant()) < 0)
                {
                    formatter.WriteError($"Invalid queue action: {action}. Valid actions: top, bottom, up, down");
                    return 1;
                }

                var result = client.QueueActionAsync(hash, action.ToLowerInvariant()).GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { infoHash = hash, action = "queue", direction = action.ToLowerInvariant() });
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(hash);
                        break;
                    default:
                        formatter.WriteSuccess($"Queue: {hash} moved to {action.ToLowerInvariant()}");
                        break;
                }
            }

            return 0;
        });
    }
}
