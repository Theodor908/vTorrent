// src/vTorrent.CLI/Commands/Torrent/PiecesCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class PiecesCommand
{
    private static readonly Argument<string> HashArgument = new("hash") { Description = "Torrent info hash" };

    public static Command Create()
    {
        var command = new Command("pieces", "Show piece completion map");
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

                var result = client.GetPieceStatesAsync(hash).GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var pieces = result.Data;
                if (pieces == null)
                {
                    formatter.WriteError($"Torrent not found: {hash}");
                    return 1;
                }

                var completed = 0;
                for (int i = 0; i < pieces.Length; i++)
                    if (pieces[i]) completed++;

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(pieces);
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet($"{completed}/{pieces.Length}");
                        break;
                    default:
                        RenderPieceMap(pieces, completed, formatter);
                        break;
                }
            }

            return 0;
        });
    }

    private static void RenderPieceMap(bool[] pieces, int completed, OutputFormatter formatter)
    {
        var total = pieces.Length;
        var pct = total > 0 ? (double)completed / total * 100.0 : 0;

        AnsiConsole.MarkupLine($"[bold]Pieces:[/] {completed}/{total} ({pct:F1}%)");
        AnsiConsole.WriteLine();

        if (total == 0) return;

        // Determine terminal width for wrapping (leave margin)
        int width;
        try
        {
            width = Console.WindowWidth - 2;
        }
        catch
        {
            width = 78;
        }

        if (width < 20) width = 20;

        // If more pieces than width, compress: each char represents a group of pieces
        if (total <= width)
        {
            // 1:1 mapping
            var sb = new StringBuilder(total);
            foreach (var p in pieces)
                sb.Append(p ? "\u2588" : "\u2591");

            // Wrap at terminal width
            var map = sb.ToString();
            for (int i = 0; i < map.Length; i += width)
            {
                var line = map.Substring(i, Math.Min(width, map.Length - i));
                AnsiConsole.WriteLine(line);
            }
        }
        else
        {
            // Compress: each character = a group of pieces
            // Show proportion filled in each group
            var sb = new StringBuilder(width);
            for (int col = 0; col < width; col++)
            {
                int start = (int)((long)col * total / width);
                int end = (int)((long)(col + 1) * total / width);
                if (end > total) end = total;
                if (start >= end) { sb.Append('\u2591'); continue; }

                int groupCompleted = 0;
                for (int i = start; i < end; i++)
                    if (pieces[i]) groupCompleted++;

                double ratio = (double)groupCompleted / (end - start);
                // Use block characters for different fill levels
                char block = ratio switch
                {
                    >= 1.0 => '\u2588',   // full block
                    >= 0.75 => '\u2593',   // dark shade
                    >= 0.25 => '\u2592',   // medium shade
                    > 0.0 => '\u2591',     // light shade
                    _ => '\u2591'           // light shade (empty)
                };
                sb.Append(block);
            }

            AnsiConsole.WriteLine(sb.ToString());
        }
    }
}
