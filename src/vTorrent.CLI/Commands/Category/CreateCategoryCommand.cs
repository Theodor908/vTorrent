// src/vTorrent.CLI/Commands/Category/CreateCategoryCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Category;

public static class CreateCategoryCommand
{
    private static readonly Argument<string> NameArgument = new("name") { Description = "Category name" };
    private static readonly Option<string?> ColorOption = new("--color") { Description = "Category color (hex, e.g. #FF0000)" };
    private static readonly Option<string?> SavePathOption = new("--save-path") { Description = "Default save path for this category" };

    public static Command Create()
    {
        var command = new Command("create", "Create a new category");
        command.Arguments.Add(NameArgument);
        command.Options.Add(ColorOption);
        command.Options.Add(SavePathOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var name = parseResult.GetValue(NameArgument)!;
                var color = parseResult.GetValue(ColorOption);
                var savePath = parseResult.GetValue(SavePathOption);

                var result = client.CreateCategoryAsync(name, color, savePath).GetAwaiter().GetResult();
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
                        formatter.WriteSuccess($"Created category: {name} (id: {id})");
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
