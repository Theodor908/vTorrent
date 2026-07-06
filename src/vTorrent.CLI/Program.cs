using System;
using System.CommandLine;
using System.IO;

namespace vTorrent.Cli;

public class Program
{
    public static int Main(string[] args)
    {
        var rootCommand = BuildRootCommand();

        // If no args and stdin is a TTY, enter interactive mode
        if (args.Length == 0 && Console.IsInputRedirected == false)
        {
            var repl = new Interactive.ReplSession(rootCommand);
            repl.RunAsync().GetAwaiter().GetResult();
            return 0;
        }

        return rootCommand.Parse(args).Invoke();
    }

    /// <summary>
    /// Builds the complete command tree for the CLI.
    /// Extracted for testability.
    /// </summary>
    public static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("vTorrent - BitTorrent client CLI");

        // Register global options
        rootCommand.Options.Add(GlobalOptions.Profile);
        rootCommand.Options.Add(GlobalOptions.Host);
        rootCommand.Options.Add(GlobalOptions.Token);
        rootCommand.Options.Add(GlobalOptions.Insecure);
        rootCommand.Options.Add(GlobalOptions.CaCert);
        rootCommand.Options.Add(GlobalOptions.Json);
        rootCommand.Options.Add(GlobalOptions.Quiet);
        rootCommand.Options.Add(GlobalOptions.NoColor);
        rootCommand.Options.Add(GlobalOptions.Verbose);
        rootCommand.Options.Add(GlobalOptions.Timeout);

        // Make global options recursive (available to all subcommands)
        GlobalOptions.Profile.Recursive = true;
        GlobalOptions.Host.Recursive = true;
        GlobalOptions.Token.Recursive = true;
        GlobalOptions.Insecure.Recursive = true;
        GlobalOptions.CaCert.Recursive = true;
        GlobalOptions.Json.Recursive = true;
        GlobalOptions.Quiet.Recursive = true;
        GlobalOptions.NoColor.Recursive = true;
        GlobalOptions.Verbose.Recursive = true;
        GlobalOptions.Timeout.Recursive = true;

        // Check environment variables
        GlobalOptions.Profile.DefaultValueFactory = _ => Environment.GetEnvironmentVariable("VTORRENT_SERVER");
        GlobalOptions.Host.DefaultValueFactory = _ => Environment.GetEnvironmentVariable("VTORRENT_HOST");
        GlobalOptions.Token.DefaultValueFactory = _ => Environment.GetEnvironmentVariable("VTORRENT_TOKEN");

        // Respect NO_COLOR env var
        if (Environment.GetEnvironmentVariable("NO_COLOR") != null)
            GlobalOptions.NoColor.DefaultValueFactory = _ => true;

        rootCommand.SetAction(parseResult =>
        {
            var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev";
            Console.WriteLine($"vTorrent CLI v{version}");
            Console.WriteLine();
            Console.WriteLine("Quick start:");
            Console.WriteLine("  vtorrent serve              Start the daemon");
            Console.WriteLine("  vtorrent login --host HOST  Authenticate with a server");
            Console.WriteLine("  vtorrent list               List torrents");
            Console.WriteLine("  vtorrent status             Check connection health");
            Console.WriteLine("  vtorrent --help             Show all commands");
            Console.WriteLine();
            Console.WriteLine("Run with no arguments in a terminal for interactive mode.");
        });

        // Auth commands
        rootCommand.Subcommands.Add(Commands.Auth.LoginCommand.Create());
        rootCommand.Subcommands.Add(Commands.Auth.LogoutCommand.Create());
        rootCommand.Subcommands.Add(Commands.Auth.SetPasswordCommand.Create());

        // Status command
        rootCommand.Subcommands.Add(Commands.StatusCommand.Create());

        // Torrent parent command with subcommands
        var torrentCommand = new Command("torrent", "Torrent management commands");
        torrentCommand.Subcommands.Add(Commands.Torrent.ListCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.AddCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.PauseCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.ResumeCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.RemoveCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.InfoCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.ForceStartCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.RecheckCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.SuperSeedCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.MoveCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.PauseAllCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.ResumeAllCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.SettingsCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.FilePriorityCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.QueueCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.CategoryCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.TagsCommand.Create());
        torrentCommand.Subcommands.Add(Commands.Torrent.PiecesCommand.Create());
        rootCommand.Subcommands.Add(torrentCommand);

        // Root-level shortcuts for all torrent commands
        rootCommand.Subcommands.Add(Commands.Torrent.ListCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.AddCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.PauseCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.ResumeCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.RemoveCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.InfoCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.ForceStartCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.RecheckCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.SuperSeedCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.MoveCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.PauseAllCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.ResumeAllCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.PiecesCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.QueueCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.SettingsCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.FilePriorityCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.CategoryCommand.Create());
        rootCommand.Subcommands.Add(Commands.Torrent.TagsCommand.Create());

        // Session parent command with subcommands
        var sessionCommand = new Command("session", "Session management commands");
        sessionCommand.Subcommands.Add(Commands.Session.StatsCommand.Create());
        sessionCommand.Subcommands.Add(Commands.Session.CountsCommand.Create());
        sessionCommand.Subcommands.Add(Commands.Session.SessionSettingsCommand.Create());
        rootCommand.Subcommands.Add(sessionCommand);

        // Category parent command with subcommands
        var categoryCommand = new Command("category", "Category management commands");
        categoryCommand.Subcommands.Add(Commands.Category.ListCategoriesCommand.Create());
        categoryCommand.Subcommands.Add(Commands.Category.CreateCategoryCommand.Create());
        categoryCommand.Subcommands.Add(Commands.Category.UpdateCategoryCommand.Create());
        categoryCommand.Subcommands.Add(Commands.Category.DeleteCategoryCommand.Create());
        rootCommand.Subcommands.Add(categoryCommand);

        // Tag parent command with subcommands
        var tagCommand = new Command("tag", "Tag management commands");
        tagCommand.Subcommands.Add(Commands.Tag.ListTagsCommand.Create());
        tagCommand.Subcommands.Add(Commands.Tag.CreateTagCommand.Create());
        tagCommand.Subcommands.Add(Commands.Tag.UpdateTagCommand.Create());
        tagCommand.Subcommands.Add(Commands.Tag.DeleteTagCommand.Create());
        rootCommand.Subcommands.Add(tagCommand);

        // DHT parent command with subcommands
        var dhtCommand = new Command("dht", "DHT management commands");
        dhtCommand.Subcommands.Add(Commands.Dht.DhtStatusCommand.Create());
        dhtCommand.Subcommands.Add(Commands.Dht.DhtToggleCommand.Create());
        rootCommand.Subcommands.Add(dhtCommand);

        // Serve command (daemon mode)
        rootCommand.Subcommands.Add(Commands.Serve.ServeCommand.Create());

        // Shell completion
        rootCommand.Subcommands.Add(Completion.ShellCompletionCommand.Create());

        // Server parent command with subcommands
        var serverCommand = new Command("server", "Manage server connections");
        serverCommand.Subcommands.Add(Commands.Server.AddServerCommand.Create());
        serverCommand.Subcommands.Add(Commands.Server.ListServersCommand.Create());
        serverCommand.Subcommands.Add(Commands.Server.RemoveServerCommand.Create());
        serverCommand.Subcommands.Add(Commands.Server.SetDefaultServerCommand.Create());
        rootCommand.Subcommands.Add(serverCommand);

        // Profile parent command (performance profiles)
        var profileCommand = new Command("profile", "Manage performance profiles");
        profileCommand.Subcommands.Add(Commands.Profile.ListProfilesCommand.Create());
        profileCommand.Subcommands.Add(Commands.Profile.ShowProfileCommand.Create());
        profileCommand.Subcommands.Add(Commands.Profile.ActivateProfileCommand.Create());
        profileCommand.Subcommands.Add(Commands.Profile.ExportProfileCommand.Create());
        profileCommand.Subcommands.Add(Commands.Profile.ImportProfileCommand.Create());
        rootCommand.Subcommands.Add(profileCommand);

        // Schedule parent command with subcommands
        var scheduleCommand = new Command("schedule", "Manage performance schedule");
        scheduleCommand.Subcommands.Add(Commands.Schedule.ScheduleShowCommand.Create());
        scheduleCommand.Subcommands.Add(Commands.Schedule.ScheduleStatusCommand.Create());
        scheduleCommand.Subcommands.Add(Commands.Schedule.ScheduleEnableCommand.Create());
        scheduleCommand.Subcommands.Add(Commands.Schedule.ScheduleDisableCommand.Create());
        scheduleCommand.Subcommands.Add(Commands.Schedule.ScheduleExportCommand.Create());
        scheduleCommand.Subcommands.Add(Commands.Schedule.ScheduleImportCommand.Create());
        rootCommand.Subcommands.Add(scheduleCommand);

        // API Key parent command with subcommands
        var apiKeyCommand = new Command("api-key", "Manage API keys");
        apiKeyCommand.Subcommands.Add(Commands.ApiKey.CreateApiKeyCommand.Create());
        apiKeyCommand.Subcommands.Add(Commands.ApiKey.ListApiKeysCommand.Create());
        apiKeyCommand.Subcommands.Add(Commands.ApiKey.RevokeApiKeyCommand.Create());
        apiKeyCommand.Subcommands.Add(Commands.ApiKey.SetApiKeyCommand.Create());
        apiKeyCommand.Subcommands.Add(Commands.ApiKey.ClearApiKeyCommand.Create());
        rootCommand.Subcommands.Add(apiKeyCommand);

        // Configure help to hide inherited global options from subcommand help
        Help.HelpConfigurator.Configure(rootCommand);

        return rootCommand;
    }

    /// <summary>
    /// Resolves the config directory (~/.vtorrent).
    /// </summary>
    public static string GetConfigDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vtorrent");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
