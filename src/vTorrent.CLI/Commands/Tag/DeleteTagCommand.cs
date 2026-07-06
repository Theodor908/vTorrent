// src/vTorrent.CLI/Commands/Tag/DeleteTagCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Tag;

public static class DeleteTagCommand
{
    private static readonly Argument<int> IdArgument = new("id") { Description = "Tag ID to delete" };

    public static Command Create()
    {
        var command = new Command("delete", "Delete a tag");
        command.Arguments.Add(IdArgument);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var id = parseResult.GetValue(IdArgument);

                var result = client.DeleteTagAsync(id).GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { id, action = "deleted" });
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(id.ToString());
                        break;
                    default:
                        formatter.WriteSuccess($"Deleted tag {id}");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
