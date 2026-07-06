using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.ApiKey;

public static class RevokeApiKeyCommand
{
    public static Command Create()
    {
        var prefixArg = new Argument<string>("prefix") { Description = "The 8-character key prefix to revoke" };
        var command = new Command("revoke", "Revoke an API key on the server");
        command.Arguments.Add(prefixArg);

        command.SetAction(parseResult =>
        {
            var prefix = parseResult.GetValue(prefixArg);
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null)
            {
                formatter.WriteError(error!);
                return 1;
            }

            using (client)
            {
                var result = client.RevokeApiKeyAsync(prefix).GetAwaiter().GetResult();
                if (!result.IsSuccess)
                    return CommandHelper.WriteApiError(result, formatter);

                if (parseResult.GetValue(GlobalOptions.Json))
                    formatter.WriteJson(new { revoked = prefix });
                else if (!parseResult.GetValue(GlobalOptions.Quiet))
                    formatter.WriteSuccess($"API key '{prefix}' revoked.");
            }

            return 0;
        });

        return command;
    }
}
