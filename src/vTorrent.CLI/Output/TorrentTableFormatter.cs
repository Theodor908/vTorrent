// src/vTorrent.CLI/Output/TorrentTableFormatter.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;

namespace vTorrent.Cli.Output;

public static class TorrentTableFormatter
{
    public static Table FormatList(IReadOnlyList<TorrentSnapshot> torrents)
    {
        // Build set of prefixes that collide — those torrents show full hash
        var collidingPrefixes = torrents
            .GroupBy(t => ShortHash(t.InfoHash))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var table = new Table()
            .Border(TableBorder.None)
            .AddColumn(new TableColumn("Hash").NoWrap())
            .AddColumn(new TableColumn("Name"))
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn("Done").RightAligned())
            .AddColumn(new TableColumn("Down").RightAligned())
            .AddColumn(new TableColumn("Up").RightAligned())
            .AddColumn(new TableColumn("Ratio").RightAligned())
            .AddColumn(new TableColumn("Status"));

        foreach (var t in torrents)
        {
            var displayHash = collidingPrefixes.Contains(ShortHash(t.InfoHash))
                ? t.InfoHash
                : ShortHash(t.InfoHash);

            var (statusText, statusColor) = GetStatusDisplay(
                t.Status, t.PayloadDownloadRate, t.PayloadUploadRate, t.ConnectedPeers);

            table.AddRow(
                displayHash,
                Markup.Escape(t.Name),
                HumanUnits.FormatBytes(t.TotalSize),
                HumanUnits.FormatProgress(t.VerifiedProgress),
                HumanUnits.FormatSpeed(t.PayloadDownloadRate),
                HumanUnits.FormatSpeed(t.PayloadUploadRate),
                HumanUnits.FormatRatio(t.TotalUploaded > 0 && t.TotalSize > 0
                    ? (double)t.TotalUploaded / t.TotalSize : 0.0),
                $"[{statusColor}]{Markup.Escape(statusText)}[/]"
            );
        }

        return table;
    }

    public static string ShortHash(string hash)
        => hash.Length > 8 ? hash[..8] : hash;

    public static string FormatListSummary(IReadOnlyList<TorrentSnapshot> torrents)
    {
        int dl = 0, seed = 0, paused = 0, errored = 0, other = 0;
        foreach (var t in torrents)
        {
            if (t.Status.Error != null || t.Status.MissingFiles)
                errored++;
            else if (t.Status.Intent == UserIntent.Paused)
                paused++;
            else if (t.Status.Phase == TransferPhase.Downloading)
                dl++;
            else if (t.Status.Phase == TransferPhase.Seeding)
                seed++;
            else
                other++;
        }

        var parts = new List<string>();
        if (dl > 0) parts.Add($"{dl} downloading");
        if (seed > 0) parts.Add($"{seed} seeding");
        if (paused > 0) parts.Add($"{paused} paused");
        if (errored > 0) parts.Add($"{errored} errored");
        if (other > 0) parts.Add($"{other} other");

        return $"{torrents.Count} torrents ({string.Join(", ", parts)})";
    }

    private static (string text, string color) GetStatusDisplay(
        TorrentStatus status, int downloadRate, int uploadRate, int connectedPeers)
    {
        // Priority 1 — Error states
        if (status.Error != null) return ("Error", "red");
        if (status.MissingFiles) return ("Missing Files", "red");

        // Priority 2 — User intent
        if (status.Intent == UserIntent.Paused) return ("Paused", "dim");
        if (status.Intent == UserIntent.Queued) return ("Queued", "yellow");

        // Priority 3 — File operations
        if (status.FileOp == FileOperation.Moving) return ("Moving", "yellow");
        if (status.FileOp == FileOperation.Rechecking) return ("Rechecking", "yellow");

        // Priority 4 — Phase + health (uses live metrics, not state-machine fields)
        return status.Phase switch
        {
            TransferPhase.Downloading when downloadRate == 0 && connectedPeers == 0 => ("Stalled", "yellow"),
            TransferPhase.Downloading => ("Downloading", "green"),
            TransferPhase.Seeding when uploadRate == 0 && connectedPeers == 0 => ("Seeding (Stalled)", "yellow"),
            TransferPhase.Seeding => ("Seeding", "blue"),
            TransferPhase.Connecting => ("Connecting", "yellow"),
            TransferPhase.CheckingFiles or TransferPhase.CheckingResumeData => ("Checking", "yellow"),
            TransferPhase.Allocating => ("Allocating", "yellow"),
            TransferPhase.FetchingMetadata => ("Fetching Metadata", "yellow"),
            TransferPhase.Stopping => ("Stopping", "dim"),
            TransferPhase.Idle => ("Stopped", "dim"),
            _ => ("Unknown", "white")
        };
    }
}
