using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Auth;

public static class SetPasswordCommand
{
    public static Command Create()
    {
        var command = new Command("set-password", "Change the server password");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                // Prompt for current password
                Console.Write("Current password: ");
                var currentPassword = ReadPassword();
                Console.WriteLine();

                // Prompt for new password
                Console.Write("New password: ");
                var newPassword = ReadPassword();
                Console.WriteLine();

                // Confirm new password
                Console.Write("Confirm new password: ");
                var confirmPassword = ReadPassword();
                Console.WriteLine();

                if (newPassword != confirmPassword)
                {
                    formatter.WriteError("Passwords do not match");
                    return 1;
                }

                if (newPassword.Length < 6)
                {
                    formatter.WriteError("Password must be at least 6 characters");
                    return 1;
                }

                var result = client.ChangePasswordAsync(currentPassword, newPassword)
                    .GetAwaiter().GetResult();

                if (!result.IsSuccess)
                {
                    formatter.WriteError($"{result.Error} ({result.ErrorCode})");
                    return 1;
                }

                formatter.WriteSuccess("Password changed. Please log in again with 'vtorrent login'.");
            }

            return 0;
        });

        return command;
    }

    private static string ReadPassword()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                password.Remove(password.Length - 1, 1);
            else if (key.Key != ConsoleKey.Backspace)
                password.Append(key.KeyChar);
        }
        return password.ToString();
    }
}
