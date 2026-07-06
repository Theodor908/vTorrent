using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.ApiKey;

public static class CreateApiKeyCommand
{
    public static Command Create()
    {
        var labelArg = new Argument<string>("label") { Description = "A descriptive label for the API key" };
        var command = new Command("create", "Create a new API key on the server");
        command.Arguments.Add(labelArg);

        command.SetAction(parseResult =>
        {
            var label = parseResult.GetValue(labelArg);
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null)
            {
                formatter.WriteError(error!);
                return 1;
            }

            using (client)
            {
                var result = client.CreateApiKeyAsync(label).GetAwaiter().GetResult();
                if (!result.IsSuccess)
                    return CommandHelper.WriteApiError(result, formatter);

                var data = result.Data;
                var rawKey = data["apiKey"]?.GetValue<string>() ?? "";
                var keyPrefix = data["keyPrefix"]?.GetValue<string>() ?? "";
                var keyLabel = data["label"]?.GetValue<string>() ?? label;

                // Auto-save to TokenStore
                var configDir = Program.GetConfigDir();
                var profileManager = new ProfileManager(configDir);
                var tokenStore = new TokenStore(configDir);
                var profileName = parseResult.GetValue(GlobalOptions.Profile) ?? profileManager.GetDefault();
                if (profileName != null && !string.IsNullOrEmpty(rawKey))
                    tokenStore.SaveApiKey(profileName, rawKey);

                if (parseResult.GetValue(GlobalOptions.Json))
                {
                    formatter.WriteJson(new { apiKey = rawKey, keyPrefix, label = keyLabel, savedToProfile = profileName });
                }
                else if (!parseResult.GetValue(GlobalOptions.Quiet))
                {
                    AnsiConsole.MarkupLine("[green]API key created successfully.[/]");
                    AnsiConsole.MarkupLine($"  [bold]Key:[/]    {Markup.Escape(rawKey)}  [dim](save this — it won't be shown again)[/]");
                    AnsiConsole.MarkupLine($"  [bold]Prefix:[/] {Markup.Escape(keyPrefix)}");
                    AnsiConsole.MarkupLine($"  [bold]Label:[/]  {Markup.Escape(keyLabel)}");
                    if (profileName != null)
                        AnsiConsole.MarkupLine($"  [dim]Saved to profile \"{Markup.Escape(profileName)}\" — future requests will use this key.[/]");
                }
            }

            return 0;
        });

        return command;
    }
}
