// src/vTorrent.CLI/Commands/Session/StatsCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using Spectre.Console;
using vTorrent.Cli.Client;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Session;

public static class StatsCommand
{
    public static Command Create()
    {
        var command = new Command("stats", "Show session statistics");

        command.SetAction(parseResult =>
        {
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var result = client.GetStatsAsync().GetAwaiter().GetResult();
                if (!result.IsSuccess) { return CommandHelper.WriteApiError(result, formatter); }

                var stats = result.Data!;
                switch (formatter.Mode)
                {
                    case OutputMode.Json:
                        formatter.WriteJson(stats);
                        break;
                    case OutputMode.Quiet:
                        formatter.WriteQuiet(
                            $"\u2193 {HumanUnits.FormatSpeed(stats.GlobalDownloadRate)} \u2191 {HumanUnits.FormatSpeed(stats.GlobalUploadRate)} | " +
                            $"{stats.TotalPeersConnected} peers | " +
                            $"{stats.DownloadingTorrents} downloading, {stats.SeedingTorrents} seeding");
                        break;
                    default:
                        var grid = new Grid();
                        grid.AddColumn(new GridColumn().PadRight(2));
                        grid.AddColumn();

                        grid.AddRow("[bold]Transfer[/]", "");
                        grid.AddRow("[dim]Download Rate:[/]", HumanUnits.FormatSpeed(stats.GlobalDownloadRate));
                        grid.AddRow("[dim]Upload Rate:[/]", HumanUnits.FormatSpeed(stats.GlobalUploadRate));
                        grid.AddRow("[dim]Total Downloaded:[/]", HumanUnits.FormatBytes(stats.TotalBytesReceived));
                        grid.AddRow("[dim]Total Uploaded:[/]", HumanUnits.FormatBytes(stats.TotalBytesSent));
                        grid.AddRow("", "");

                        grid.AddRow("[bold]Torrents[/]", "");
                        grid.AddRow("[dim]Downloading:[/]", stats.DownloadingTorrents.ToString());
                        grid.AddRow("[dim]Seeding:[/]", stats.SeedingTorrents.ToString());
                        grid.AddRow("[dim]Paused:[/]", stats.PausedTorrents.ToString());
                        grid.AddRow("[dim]Checking:[/]", stats.CheckingTorrents.ToString());
                        grid.AddRow("[dim]Error:[/]", stats.ErrorTorrents.ToString());
                        grid.AddRow("[dim]Total:[/]", stats.TotalTorrents.ToString());
                        grid.AddRow("", "");

                        grid.AddRow("[bold]Peers[/]", "");
                        grid.AddRow("[dim]Connected:[/]", stats.TotalPeersConnected.ToString());
                        grid.AddRow("[dim]Seeds:[/]", stats.TotalConnectedSeeds.ToString());
                        grid.AddRow("[dim]Uploading To:[/]", stats.UploadingPeers.ToString());
                        grid.AddRow("[dim]Downloading From:[/]", stats.DownloadingPeers.ToString());
                        grid.AddRow("", "");

                        grid.AddRow("[bold]DHT[/]", "");
                        grid.AddRow("[dim]Nodes:[/]", stats.DhtNodes.ToString());
                        grid.AddRow("[dim]Torrents:[/]", stats.DhtTorrents.ToString());
                        grid.AddRow("", "");

                        grid.AddRow("[bold]Disk[/]", "");
                        grid.AddRow("[dim]Read Queue:[/]", stats.DiskReadQueue.ToString());
                        grid.AddRow("[dim]Write Queue:[/]", stats.DiskWriteQueue.ToString());
                        grid.AddRow("[dim]Cache Hit Ratio:[/]", $"{stats.DiskCacheHitRatio:P1}");

                        AnsiConsole.Write(grid);
                        break;
                }
            }

            return 0;
        });

        return command;
    }
}
