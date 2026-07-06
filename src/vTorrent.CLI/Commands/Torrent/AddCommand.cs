// src/vTorrent.CLI/Commands/Torrent/AddCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class AddCommand
{
    private static readonly Argument<string> SourceArgument = new("source") { Description = "Torrent file path or magnet URI" };
    private static readonly Option<string?> SavePathOption = new("--save-path") { Description = "Download save path" };
    private static readonly Option<bool> PausedOption = new("--paused") { Description = "Add in paused state" };
    private static readonly Option<bool> SequentialOption = new("--sequential") { Description = "Enable sequential download" };
    private static readonly Option<bool> FirstLastOption = new("--first-last-priority") { Description = "Prioritize first and last pieces" };
    private static readonly Option<bool> TopOfQueueOption = new("--top-of-queue") { Description = "Add to top of download queue" };

    public static Command Create()
    {
        var command = new Command("add", "Add a torrent from file or magnet URI");
        AddArgumentsOptionsAndHandler(command);
        return command;
    }

    private static void AddArgumentsOptionsAndHandler(Command command)
    {
        command.Arguments.Add(SourceArgument);
        command.Options.Add(SavePathOption);
        command.Options.Add(PausedOption);
        command.Options.Add(SequentialOption);
        command.Options.Add(FirstLastOption);
        command.Options.Add(TopOfQueueOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var source = parseResult.GetValue(SourceArgument)!;
                var savePath = parseResult.GetValue(SavePathOption);
                var paused = parseResult.GetValue(PausedOption);
                var sequential = parseResult.GetValue(SequentialOption);
                var firstLast = parseResult.GetValue(FirstLastOption);
                var topOfQueue = parseResult.GetValue(TopOfQueueOption);

                ApiResult<string> result;
                if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    result = client.AddMagnetAsync(source, savePath, paused, sequential, firstLast, topOfQueue)
                        .GetAwaiter().GetResult();
                }
                else
                {
                    result = client.AddTorrentFileAsync(source, savePath, paused, sequential, firstLast, topOfQueue)
                        .GetAwaiter().GetResult();
                }

                if (!result.IsSuccess) { formatter.WriteError($"{result.Error} ({result.ErrorCode})"); return 1; }

                var hash = result.Data!;
                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { infoHash = hash });
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(hash);
                        break;
                    default:
                        formatter.WriteSuccess($"Added: {hash}");
                        break;
                }
            }

            return 0;
        });
    }
}
