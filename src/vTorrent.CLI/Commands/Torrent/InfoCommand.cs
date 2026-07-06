// src/vTorrent.CLI/Commands/Torrent/InfoCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using Spectre.Console;
using Spectre.Console.Rendering;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Commands.Torrent;

public static class InfoCommand
{
    private static readonly Argument<string> HashArgument = new("hash") { Description = "Torrent info hash" };
    private static readonly Option<bool> FollowOption = new("--follow") { Description = "Live updating display (Ctrl+C to exit)" };

    public static Command Create()
    {
        var command = new Command("info", "Show detailed torrent information");
        AddArgumentsAndHandler(command);
        return command;
    }

    private static void AddArgumentsAndHandler(Command command)
    {
        command.Arguments.Add(HashArgument);
        command.Options.Add(FollowOption);

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                string hash;
                try { hash = client.ResolveHashAsync(parseResult.GetValue(HashArgument)!).GetAwaiter().GetResult(); }
                catch (ApiException ex) { formatter.WriteError($"{ex.Message} ({ex.ErrorCode})"); return 1; }

                var follow = parseResult.GetValue(FollowOption);

                if (follow)
                {
                    // Create SignalR connection for real-time triggers
                    VTorrentRealtimeClient? rtClient = null;
                    try
                    {
                        var configDir = Program.GetConfigDir();
                        var profileManager = new ProfileManager(configDir);
                        var tokenStore = new TokenStore(configDir);
                        var profileName = parseResult.GetValue(GlobalOptions.Profile) ?? profileManager.GetDefault();
                        var profile = profileName != null ? profileManager.Get(profileName) : null;

                        if (profile != null)
                        {
                            rtClient = new VTorrentRealtimeClient(profile, profileName!, tokenStore,
                                parseResult.GetValue(GlobalOptions.Insecure));
                            rtClient.ConnectAsync().GetAwaiter().GetResult();
                            rtClient.SubscribeTorrentAsync(hash).GetAwaiter().GetResult();
                        }
                    }
                    catch { /* SignalR optional — REST polling still works */ }

                    AnsiConsole.MarkupLine("[dim]Live display — Ctrl+C to exit[/]");
                    AnsiConsole.WriteLine();

                    try
                    {
                        LiveTorrentDisplay.Run(
                            buildDisplay: () =>
                            {
                                var r = client.GetTorrentDetailsAsync(hash).GetAwaiter().GetResult();
                                if (!r.IsSuccess || r.Data == null) return new Text("Failed to fetch torrent data");
                                return BuildLiveInfoPanel(r.Data);
                            },
                            realtimeClient: rtClient);
                    }
                    finally
                    {
                        if (rtClient != null)
                        {
                            try { rtClient.UnsubscribeTorrentAsync(hash).GetAwaiter().GetResult(); } catch { }
                            rtClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                    }

                    return 0;
                }

                var result = client.GetTorrentDetailsAsync(hash).GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var details = result.Data;
                if (details == null)
                {
                    formatter.WriteError($"Torrent not found: {hash}");
                    return 1;
                }

                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(details);
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(details.InfoHash);
                        break;
                    default:
                        RenderDetailView(details, formatter);
                        break;
                }
            }

            return 0;
        });
    }

    private static void RenderDetailView(Abstractions.Models.ManagedTorrentView t, OutputFormatter formatter)
    {
        // Header
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(t.Name)}[/]");
        AnsiConsole.WriteLine();

        // Key-value grid
        var grid = new Grid();
        grid.AddColumn(new GridColumn().PadRight(2));
        grid.AddColumn();

        grid.AddRow("[dim]Hash:[/]", Markup.Escape(t.InfoHash));
        if (t.InfoHashV2 != null)
            grid.AddRow("[dim]Hash v2:[/]", Markup.Escape(t.InfoHashV2));
        grid.AddRow("[dim]Status:[/]", FormatStatus(t.Status, (int)t.PayloadDownloadRate, (int)t.PayloadUploadRate, t.ConnectedPeers));
        grid.AddRow("[dim]Size:[/]", HumanUnits.FormatBytes(t.TotalSize));
        grid.AddRow("[dim]Progress:[/]", HumanUnits.FormatProgress(t.Progress));
        grid.AddRow("[dim]Downloaded:[/]", HumanUnits.FormatBytes(t.Downloaded));
        grid.AddRow("[dim]Uploaded:[/]", HumanUnits.FormatBytes(t.Uploaded));
        grid.AddRow("[dim]Ratio:[/]", HumanUnits.FormatRatio(t.Ratio));
        grid.AddRow("[dim]Down Rate:[/]", HumanUnits.FormatSpeed(t.DownloadRate));
        grid.AddRow("[dim]Up Rate:[/]", HumanUnits.FormatSpeed(t.UploadRate));
        grid.AddRow("[dim]Pieces:[/]", $"{t.PiecesCompleted}/{t.TotalPieces} ({HumanUnits.FormatBytes(t.PieceSize)} each)");
        grid.AddRow("[dim]Availability:[/]", t.Availability.ToString("F2"));
        grid.AddRow("[dim]Seeds:[/]", $"{t.ConnectedSeeds} connected ({t.TrackerSeeders} tracker)");
        grid.AddRow("[dim]Peers:[/]", $"{t.ConnectedPeers} connected ({t.TrackerLeechers} tracker)");
        grid.AddRow("[dim]Save Path:[/]", Markup.Escape(t.SavePath));
        grid.AddRow("[dim]Added:[/]", HumanUnits.FormatDateTime(t.AddedTime));
        if (t.CompletedTime.HasValue)
            grid.AddRow("[dim]Completed:[/]", HumanUnits.FormatDateTime(t.CompletedTime.Value));
        grid.AddRow("[dim]Active:[/]", HumanUnits.FormatDuration(t.ActiveDuration));
        if (t.CategoryName != null)
            grid.AddRow("[dim]Category:[/]", Markup.Escape(t.CategoryName));
        if (t.Tags.Count > 0)
            grid.AddRow("[dim]Tags:[/]", Markup.Escape(string.Join(", ", t.Tags)));
        if (t.Comment != null)
            grid.AddRow("[dim]Comment:[/]", Markup.Escape(t.Comment));
        if (t.Creator != null)
            grid.AddRow("[dim]Creator:[/]", Markup.Escape(t.Creator));
        if (t.Status.Error != null)
            grid.AddRow("[red]Error:[/]", Markup.Escape(t.Status.Error.Value.Message));

        AnsiConsole.Write(grid);

        // Peers table
        if (t.Peers.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Peers[/]");

            var peerTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn(new TableColumn("Address").NoWrap())
                .AddColumn(new TableColumn("Client"))
                .AddColumn(new TableColumn("Down").RightAligned())
                .AddColumn(new TableColumn("Up").RightAligned())
                .AddColumn(new TableColumn("Progress").RightAligned())
                .AddColumn(new TableColumn("Flags"));

            foreach (var p in t.Peers)
            {
                peerTable.AddRow(
                    $"{Markup.Escape(p.IpAddress)}:{p.Port}",
                    Markup.Escape(p.Client),
                    p.DownloadRateFormatted,
                    p.UploadRateFormatted,
                    HumanUnits.FormatProgress(p.Progress),
                    Markup.Escape(p.Flags)
                );
            }

            AnsiConsole.Write(peerTable);
        }

        // Trackers table
        if (t.Trackers.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Trackers[/]");

            var trackerTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn(new TableColumn("Tier").RightAligned())
                .AddColumn(new TableColumn("URL"))
                .AddColumn(new TableColumn("Status"))
                .AddColumn(new TableColumn("Seeds").RightAligned())
                .AddColumn(new TableColumn("Peers").RightAligned());

            foreach (var tr in t.Trackers)
            {
                trackerTable.AddRow(
                    tr.Tier.ToString(),
                    Markup.Escape(tr.Url),
                    Markup.Escape(tr.Status),
                    tr.Seeds.ToString(),
                    tr.Peers.ToString()
                );
            }

            AnsiConsole.Write(trackerTable);
        }

        // Web seeds table
        if (t.WebSeeds.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Web Seeds[/]");

            var wsTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn(new TableColumn("URL"))
                .AddColumn(new TableColumn("Type"))
                .AddColumn(new TableColumn("Status"))
                .AddColumn(new TableColumn("Down").RightAligned());

            foreach (var ws in t.WebSeeds)
            {
                wsTable.AddRow(
                    Markup.Escape(ws.Url),
                    Markup.Escape(ws.Type),
                    Markup.Escape(ws.Status),
                    ws.DownloadRateFormatted
                );
            }

            AnsiConsole.Write(wsTable);
        }
    }

    private static IRenderable BuildLiveInfoPanel(Abstractions.Models.ManagedTorrentView t)
    {
        var rows = new Rows(
            new Markup($"[bold]{Markup.Escape(t.Name)}[/]  {FormatStatus(t.Status, (int)t.PayloadDownloadRate, (int)t.PayloadUploadRate, t.ConnectedPeers)}"),
            new Text(""),
            new Columns(
                new Markup($"[dim]Progress:[/] {HumanUnits.FormatProgress(t.Progress)}"),
                new Markup($"[dim]Down:[/] {HumanUnits.FormatSpeed(t.DownloadRate)}"),
                new Markup($"[dim]Up:[/] {HumanUnits.FormatSpeed(t.UploadRate)}")
            ),
            new Columns(
                new Markup($"[dim]Size:[/] {HumanUnits.FormatBytes(t.TotalSize)}"),
                new Markup($"[dim]Seeds:[/] {t.ConnectedSeeds} ({t.TrackerSeeders})"),
                new Markup($"[dim]Peers:[/] {t.ConnectedPeers} ({t.TrackerLeechers})")
            ),
            new Columns(
                new Markup($"[dim]Ratio:[/] {HumanUnits.FormatRatio(t.Ratio)}"),
                new Markup($"[dim]Availability:[/] {(float.IsInfinity(t.Availability) ? "\u221e" : t.Availability.ToString("F2"))}"),
                new Markup($"[dim]Active:[/] {HumanUnits.FormatDuration(t.ActiveDuration)}")
            )
        );

        var panel = new Panel(rows)
            .Header($"[dim]Live: {Markup.Escape(TorrentTableFormatter.ShortHash(t.InfoHash))} (Ctrl+C to exit)[/]")
            .Border(BoxBorder.Rounded)
            .Expand();

        return panel;
    }

    private static string FormatStatus(
        Abstractions.Models.TorrentStatus status,
        int downloadRate, int uploadRate, int connectedPeers)
    {
        string text;
        string color;

        // Priority 1 — Error states
        if (status.Error != null)
        {
            text = "Error"; color = "red";
        }
        else if (status.MissingFiles)
        {
            text = "Missing Files"; color = "red";
        }
        // Priority 2 — User intent
        else if (status.Intent == Abstractions.Enums.UserIntent.Paused)
        {
            text = "Paused"; color = "dim";
        }
        else if (status.Intent == Abstractions.Enums.UserIntent.Queued)
        {
            text = "Queued"; color = "yellow";
        }
        // Priority 3 — File operations
        else if (status.FileOp == Abstractions.Enums.FileOperation.Moving)
        {
            text = "Moving"; color = "yellow";
        }
        else if (status.FileOp == Abstractions.Enums.FileOperation.Rechecking)
        {
            text = "Rechecking"; color = "yellow";
        }
        // Priority 4 — Phase + health
        else
        {
            (text, color) = status.Phase switch
            {
                Abstractions.Enums.TransferPhase.Downloading when downloadRate == 0 && connectedPeers == 0
                    => ("Stalled", "yellow"),
                Abstractions.Enums.TransferPhase.Downloading => ("Downloading", "green"),
                Abstractions.Enums.TransferPhase.Seeding when uploadRate == 0 && connectedPeers == 0
                    => ("Seeding (Stalled)", "yellow"),
                Abstractions.Enums.TransferPhase.Seeding => ("Seeding", "blue"),
                Abstractions.Enums.TransferPhase.Connecting => ("Connecting", "yellow"),
                Abstractions.Enums.TransferPhase.CheckingFiles or Abstractions.Enums.TransferPhase.CheckingResumeData
                    => ("Checking", "yellow"),
                Abstractions.Enums.TransferPhase.Allocating => ("Allocating", "yellow"),
                Abstractions.Enums.TransferPhase.FetchingMetadata => ("Fetching Metadata", "yellow"),
                Abstractions.Enums.TransferPhase.Stopping => ("Stopping", "dim"),
                Abstractions.Enums.TransferPhase.Idle => ("Stopped", "dim"),
                _ => ("Unknown", "white")
            };
        }

        return $"[{color}]{Markup.Escape(text)}[/]";
    }
}
