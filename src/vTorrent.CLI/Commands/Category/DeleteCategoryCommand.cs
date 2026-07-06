// src/vTorrent.CLI/Commands/Category/DeleteCategoryCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Category;

public static class DeleteCategoryCommand
{
    private static readonly Argument<int> IdArgument = new("id") { Description = "Category ID to delete" };

    public static Command Create()
    {
        var command = new Command("delete", "Delete a category");
        command.Arguments.Add(IdArgument);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var id = parseResult.GetValue(IdArgument);

                var result = client.DeleteCategoryAsync(id).GetAwaiter().GetResult();
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
                        formatter.WriteSuccess($"Deleted category {id}");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
