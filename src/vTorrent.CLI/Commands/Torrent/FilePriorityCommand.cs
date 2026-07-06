// src/vTorrent.CLI/Commands/Torrent/FilePriorityCommand.cs
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class FilePriorityCommand
{
    private static readonly Argument<string> HashArgument = new("hash") { Description = "Torrent info hash" };
    private static readonly Option<int[]> FileOption = new("--file") { Description = "File index (can be repeated)", AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.OneOrMore };
    private static readonly Option<string[]> PriorityOption = new("--priority") { Description = "Priority: skip, low, normal, high (can be repeated)", AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.OneOrMore };

    public static Command Create()
    {
        var command = new Command("file-priority", "Set file download priority");
        AddArgumentsAndHandler(command);
        return command;
    }

    private static void AddArgumentsAndHandler(Command command)
    {
        command.Arguments.Add(HashArgument);
        command.Options.Add(FileOption);
        command.Options.Add(PriorityOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                string hash;
                try { hash = client.ResolveHashAsync(parseResult.GetValue(HashArgument)!).GetAwaiter().GetResult(); }
                catch (ApiException ex) { formatter.WriteError($"{ex.Message} ({ex.ErrorCode})"); return 1; }

                var files = parseResult.GetValue(FileOption);
                var priorities = parseResult.GetValue(PriorityOption);

                if (files == null || files.Length == 0)
                {
                    formatter.WriteError("--file is required");
                    return 1;
                }

                if (priorities == null || priorities.Length == 0)
                {
                    formatter.WriteError("--priority is required");
                    return 1;
                }

                if (files.Length != priorities.Length && priorities.Length != 1)
                {
                    formatter.WriteError("Number of --file and --priority values must match, or provide a single --priority for all files");
                    return 1;
                }

                var priorityList = new List<object>();
                for (int i = 0; i < files.Length; i++)
                {
                    var pri = priorities.Length == 1 ? priorities[0] : priorities[i];
                    var normalized = NormalizePriority(pri);
                    if (normalized == null)
                    {
                        formatter.WriteError($"Invalid priority: {pri}. Valid values: skip, low, normal, high");
                        return 1;
                    }
                    priorityList.Add(new { fileIndex = files[i], priority = normalized });
                }

                var result = client.SetFilePrioritiesAsync(hash, priorityList).GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { infoHash = hash, action = "file-priority", priorities = priorityList });
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(hash);
                        break;
                    default:
                        for (int i = 0; i < files.Length; i++)
                        {
                            var pri = priorities.Length == 1 ? priorities[0] : priorities[i];
                            formatter.WriteSuccess($"Set file {files[i]} priority to {NormalizePriority(pri)}");
                        }
                        break;
                }
            }

            return 0;
        });
    }

    private static string? NormalizePriority(string input)
    {
        return input.ToLowerInvariant() switch
        {
            "skip" => "Skip",
            "low" => "Low",
            "normal" => "Normal",
            "high" => "High",
            _ => null
        };
    }
}
