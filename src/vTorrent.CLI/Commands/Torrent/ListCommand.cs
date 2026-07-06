// src/vTorrent.CLI/Commands/Torrent/ListCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using Spectre.Console;
using Spectre.Console.Rendering;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.Torrent;

public static class ListCommand
{
    private static readonly Option<string?> PhaseOption = new("--phase") { Description = "Filter by transfer phase (downloading, seeding, idle, etc.)" };
    private static readonly Option<string?> CategoryOption = new("--category") { Description = "Filter by category name" };
    private static readonly Option<string?> TagOption = new("--tag") { Description = "Filter by tag name" };
    private static readonly Option<string?> SortOption = new("--sort") { Description = "Sort field and direction (e.g. name:asc, size:desc)" };
    private static readonly Option<int?> LimitOption = new("--limit") { Description = "Maximum number of results" };
    private static readonly Option<int?> OffsetOption = new("--offset") { Description = "Number of results to skip" };
    private static readonly Option<bool> FollowOption = new("--follow") { Description = "Live updating display (Ctrl+C to exit)" };
    private static readonly Option<int?> CountOption = new("-c") { Description = "Number of torrents to show in live mode" };

    public static Command Create()
    {
        var command = new Command("list", "List torrents");
        AddOptionsAndHandler(command);
        return command;
    }

    private static void AddOptionsAndHandler(Command command)
    {
        command.Options.Add(PhaseOption);
        command.Options.Add(CategoryOption);
        command.Options.Add(TagOption);
        command.Options.Add(SortOption);
        command.Options.Add(LimitOption);
        command.Options.Add(OffsetOption);
        command.Options.Add(FollowOption);
        command.Options.Add(CountOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var phase = parseResult.GetValue(PhaseOption);
                var category = parseResult.GetValue(CategoryOption);
                var tag = parseResult.GetValue(TagOption);
                var sort = parseResult.GetValue(SortOption);
                var limit = parseResult.GetValue(LimitOption);
                var offset = parseResult.GetValue(OffsetOption);

                var follow = parseResult.GetValue(FollowOption);
                var count = parseResult.GetValue(CountOption);

                if (follow)
                {
                    VTorrentRealtimeClient? rtClient = null;
                    try
                    {
                        var configDir = Program.GetConfigDir();
                        var profileManager = new ProfileManager(configDir);
                        var tokenStore = new TokenStore(configDir);
                        var profileName = parseResult.GetValue(GlobalOptions.Profile) ?? profileManager.GetDefault();
                        var profileEntry = profileName != null ? profileManager.Get(profileName) : null;

                        if (profileEntry != null)
                        {
                            rtClient = new VTorrentRealtimeClient(profileEntry, profileName!, tokenStore,
                                parseResult.GetValue(GlobalOptions.Insecure));
                            rtClient.ConnectAsync().GetAwaiter().GetResult();
                        }
                    }
                    catch
                    {
                        AnsiConsole.MarkupLine("[dim yellow]Real-time connection failed -- display will refresh on timer only[/]");
                        rtClient = null;
                    }

                    var countLabel = count.HasValue ? $"{count.Value} shown, " : "";
                    AnsiConsole.MarkupLine($"[dim]Live torrent list ({countLabel}Ctrl+C to exit)[/]");
                    AnsiConsole.WriteLine();

                    try
                    {
                        LiveTorrentDisplay.Run(
                            buildDisplay: () =>
                            {
                                var r = client.ListTorrentsAsync(phase, category, tag, sort, count, offset)
                                    .GetAwaiter().GetResult();
                                if (!r.IsSuccess) return new Text("Failed to fetch torrent list");

                                var torrents = r.Data!;
                                var table = TorrentTableFormatter.FormatList(torrents);
                                var summary = new Markup($"\n[dim]{Markup.Escape(TorrentTableFormatter.FormatListSummary(torrents))}[/]");
                                return new Rows(table, summary);
                            },
                            realtimeClient: rtClient);
                    }
                    finally
                    {
                        if (rtClient != null)
                            rtClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }

                    return 0;
                }

                var result = client.ListTorrentsAsync(phase, category, tag, sort, limit, offset)
                    .GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var torrents = result.Data!;
                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(torrents);
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(torrents.Select(t => t.InfoHash));
                        break;
                    default:
                        if (torrents.Count == 0)
                        {
                            formatter.WriteSummary("No torrents found.");
                        }
                        else
                        {
                            var table = TorrentTableFormatter.FormatList(torrents);
                            formatter.WriteTable(table);
                        }
                        formatter.WriteSummary(TorrentTableFormatter.FormatListSummary(torrents));
                        break;
                }
            }

            return 0;
        });
    }
}
