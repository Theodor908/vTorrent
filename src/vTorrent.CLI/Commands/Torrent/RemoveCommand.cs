// src/vTorrent.CLI/Commands/Torrent/RemoveCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Torrent;

public static class RemoveCommand
{
    private static readonly Argument<string> HashArgument = new("hash") { Description = "Torrent info hash" };
    private static readonly Option<bool> DeleteFilesOption = new("--delete-files") { Description = "Delete downloaded files" };
    private static readonly Option<bool> SecureWipeOption = new("--secure-wipe") { Description = "Securely wipe downloaded files" };
    private static readonly Option<bool> WipeMetadataOption = new("--wipe-metadata") { Description = "Wipe torrent metadata" };

    public static Command Create()
    {
        var command = new Command("remove", "Remove a torrent");
        AddArgumentsOptionsAndHandler(command);
        return command;
    }

    private static void AddArgumentsOptionsAndHandler(Command command)
    {
        command.Arguments.Add(HashArgument);
        command.Options.Add(DeleteFilesOption);
        command.Options.Add(SecureWipeOption);
        command.Options.Add(WipeMetadataOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                string hash;
                try { hash = client.ResolveHashAsync(parseResult.GetValue(HashArgument)!).GetAwaiter().GetResult(); }
                catch (ApiException ex) { formatter.WriteError($"{ex.Message} ({ex.ErrorCode})"); return 1; }

                var deleteFiles = parseResult.GetValue(DeleteFilesOption);
                var secureWipe = parseResult.GetValue(SecureWipeOption);
                var wipeMetadata = parseResult.GetValue(WipeMetadataOption);

                var result = client.RemoveAsync(hash, deleteFiles, secureWipe, wipeMetadata)
                    .GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var deleteResult = result.Data;
                if (deleteResult == null)
                {
                    formatter.WriteError($"Torrent not found: {hash}");
                    return 1;
                }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { infoHash = hash, action = "removed", deleteFiles });
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(hash);
                        break;
                    default:
                        var suffix = deleteFiles ? " (files deleted)" : "";
                        formatter.WriteSuccess($"Removed: {TorrentTableFormatter.ShortHash(hash)}{suffix}");
                        if (deleteResult.HasExtraFiles == true)
                        {
                            formatter.WriteSummary($"Warning: {deleteResult.ExtraFiles.Count} extra file(s) found in torrent directory.");
                        }
                        break;
                }
            }

            return 0;
        });
    }
}
