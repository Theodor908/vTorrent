// src/vTorrent.CLI/Commands/Category/UpdateCategoryCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Category;

public static class UpdateCategoryCommand
{
    private static readonly Argument<int> IdArgument = new("id") { Description = "Category ID" };
    private static readonly Argument<string> NameArgument = new("name") { Description = "New category name" };
    private static readonly Option<string?> ColorOption = new("--color") { Description = "Category color (hex, e.g. #FF0000)" };
    private static readonly Option<string?> SavePathOption = new("--save-path") { Description = "Default save path for this category" };

    public static Command Create()
    {
        var command = new Command("update", "Update a category");
        command.Arguments.Add(IdArgument);
        command.Arguments.Add(NameArgument);
        command.Options.Add(ColorOption);
        command.Options.Add(SavePathOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var id = parseResult.GetValue(IdArgument);
                var name = parseResult.GetValue(NameArgument)!;
                var color = parseResult.GetValue(ColorOption);
                var savePath = parseResult.GetValue(SavePathOption);

                var result = client.UpdateCategoryAsync(id, name, color, savePath).GetAwaiter().GetResult();
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
                        formatter.WriteSuccess($"Updated category: {name}");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
