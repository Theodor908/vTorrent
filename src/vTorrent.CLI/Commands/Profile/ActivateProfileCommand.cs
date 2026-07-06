// src/vTorrent.CLI/Commands/Profile/ActivateProfileCommand.cs
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Profile;

public static class ActivateProfileCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to activate"
        };

        var command = new Command("activate", "Activate a performance profile");
        command.Arguments.Add(nameArgument);

        command.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameArgument);
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.ActivateProfileAsync(name).GetAwaiter().GetResult();

                if (!result.IsSuccess)
                {
                    if (result.StatusCode == 409)
                    {
                        formatter.WriteError("Schedule is enabled — disable it first with 'vtorrent schedule disable'");
                        return 1;
                    }
                    return CommandHelper.WriteApiError(result, formatter);
                }

                formatter.WriteSuccess($"Activated profile \"{name}\"");
            }

            return 0;
        });

        return command;
    }
}
