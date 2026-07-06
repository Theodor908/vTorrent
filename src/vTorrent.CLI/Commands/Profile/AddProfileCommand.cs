// src/vTorrent.CLI/Commands/Profile/AddProfileCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.Profile;

public static class AddProfileCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Profile name"
        };

        var hostOption = new Option<string>("--host")
        {
            Description = "Server host:port (e.g., 127.0.0.1:8080)",
            Required = true
        };

        var noHttpsOption = new Option<bool>("--no-https")
        {
            Description = "Use HTTP instead of HTTPS"
        };

        var insecureOption = new Option<bool>("--insecure")
        {
            Description = "Skip TLS certificate validation"
        };

        var usernameOption = new Option<string>("--username")
        {
            Description = "Username for this profile",
            DefaultValueFactory = _ => "admin"
        };

        var command = new Command("add", "Add a connection profile");
        command.Arguments.Add(nameArgument);
        command.Options.Add(hostOption);
        command.Options.Add(noHttpsOption);
        command.Options.Add(insecureOption);
        command.Options.Add(usernameOption);

        command.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameArgument);
            var host = parseResult.GetValue(hostOption);
            var noHttps = parseResult.GetValue(noHttpsOption);
            var insecure = parseResult.GetValue(insecureOption);
            var username = parseResult.GetValue(usernameOption);
            var json = parseResult.GetValue(GlobalOptions.Json);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var noColor = parseResult.GetValue(GlobalOptions.NoColor);

            var formatter = new OutputFormatter(json, quiet, noColor);
            var configDir = Program.GetConfigDir();
            var profileManager = new ProfileManager(configDir);

            profileManager.Add(name, host, https: !noHttps, insecure, username);

            if (json)
                formatter.WriteJson(new { profile = name, host, https = !noHttps, insecure, username });
            else
                formatter.WriteSuccess($"Profile '{name}' added");

            return 0;
        });

        return command;
    }
}
