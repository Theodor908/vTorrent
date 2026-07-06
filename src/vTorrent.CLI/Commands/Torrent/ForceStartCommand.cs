// src/vTorrent.CLI/Commands/Torrent/ForceStartCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class ForceStartCommand
{
    private static readonly Argument<string> HashArgument = new("hash") { Description = "Torrent info hash" };

    public static Command Create()
    {
        var command = new Command("force-start", "Force start a torrent");
        AddArgumentsAndHandler(command);
        return command;
    }

    private static void AddArgumentsAndHandler(Command command)
    {
        command.Arguments.Add(HashArgument);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                string hash;
                try { hash = client.ResolveHashAsync(parseResult.GetValue(HashArgument)!).GetAwaiter().GetResult(); }
                catch (ApiException ex) { formatter.WriteError($"{ex.Message} ({ex.ErrorCode})"); return 1; }

                var result = client.ForceStartAsync(hash).GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { infoHash = hash, action = "force-started" });
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(hash);
                        break;
                    default:
                        formatter.WriteSuccess($"Force started: {hash}");
                        break;
                }
            }

            return 0;
        });
    }
}
