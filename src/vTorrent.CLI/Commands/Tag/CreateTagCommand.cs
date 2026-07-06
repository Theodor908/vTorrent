// src/vTorrent.CLI/Commands/Tag/CreateTagCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Tag;

public static class CreateTagCommand
{
    private static readonly Argument<string> NameArgument = new("name") { Description = "Tag name" };
    private static readonly Option<string?> ColorOption = new("--color") { Description = "Tag color (hex, e.g. #FF0000)" };

    public static Command Create()
    {
        var command = new Command("create", "Create a new tag");
        command.Arguments.Add(NameArgument);
        command.Options.Add(ColorOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var name = parseResult.GetValue(NameArgument)!;
                var color = parseResult.GetValue(ColorOption);

                var result = client.CreateTagAsync(name, color).GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var created = result.Data!;
                var id = created["id"]?.GetValue<int>() ?? 0;

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(created);
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(id.ToString());
                        break;
                    default:
                        formatter.WriteSuccess($"Created tag: {name} (id: {id})");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
