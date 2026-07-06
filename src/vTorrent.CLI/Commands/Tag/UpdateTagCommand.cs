// src/vTorrent.CLI/Commands/Tag/UpdateTagCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Tag;

public static class UpdateTagCommand
{
    private static readonly Argument<int> IdArgument = new("id") { Description = "Tag ID" };
    private static readonly Argument<string> NameArgument = new("name") { Description = "New tag name" };
    private static readonly Option<string?> ColorOption = new("--color") { Description = "Tag color (hex, e.g. #FF0000)" };

    public static Command Create()
    {
        var command = new Command("update", "Update a tag");
        command.Arguments.Add(IdArgument);
        command.Arguments.Add(NameArgument);
        command.Options.Add(ColorOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var id = parseResult.GetValue(IdArgument);
                var name = parseResult.GetValue(NameArgument)!;
                var color = parseResult.GetValue(ColorOption);

                var result = client.UpdateTagAsync(id, name, color).GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(new { id, name, action = "updated" });
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(id.ToString());
                        break;
                    default:
                        formatter.WriteSuccess($"Updated tag: {name}");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
