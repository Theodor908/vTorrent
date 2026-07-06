// src/vTorrent.CLI/Commands/Torrent/MoveCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class MoveCommand
{
    private static readonly Argument<string> HashArgument = new("hash") { Description = "Torrent info hash" };
    private static readonly Argument<string> PathArgument = new("path") { Description = "Destination path" };

    public static Command Create()
    {
        var command = new Command("move", "Move torrent data to a new location");
        AddArgumentsAndHandler(command);
        return command;
    }

    private static void AddArgumentsAndHandler(Command command)
    {
        command.Arguments.Add(HashArgument);
        command.Arguments.Add(PathArgument);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                string hash;
                try { hash = client.ResolveHashAsync(parseResult.GetValue(HashArgument)!).GetAwaiter().GetResult(); }
                catch (ApiException ex) { formatter.WriteError($"{ex.Message} ({ex.ErrorCode})"); return 1; }

                var path = parseResult.GetValue(PathArgument)!;

                var result = client.MoveAsync(hash, path).GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                if (!result.Data)
                {
                    switch (formatter.Mode)
                    {
                        case OutputMode.Json:
                            formatter.WriteJson(new { infoHash = hash, action = "move", success = false, error = "conflict" });
                            break;
                        case OutputMode.Quiet:
                            break;
                        default:
                            formatter.WriteError($"Move conflict for {hash} (torrent may already be at that location)");
                            break;
                    }
                    return 1;
                }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { infoHash = hash, action = "move", savePath = path, success = true });
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(hash);
                        break;
                    default:
                        formatter.WriteSuccess($"Moving: {hash} \u2192 {path}");
                        break;
                }
            }

            return 0;
        });
    }
}
