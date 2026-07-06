// src/vTorrent.CLI/Commands/Profile/ShowProfileCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Commands.Profile;

public static class ShowProfileCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to show"
        };

        var command = new Command("show", "Show all settings for a performance profile");
        command.Arguments.Add(nameArgument);

        command.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameArgument);
            var (client, formatter, error) = CommandHelper.CreateClientAndFormatter(parseResult);
            if (client == null) { formatter.WriteError(error!); return 1; }

            using (client)
            {
                var profilesResult = client.GetProfilesAsync().GetAwaiter().GetResult();
                if (!profilesResult.IsSuccess) return CommandHelper.WriteApiError(profilesResult, formatter);

                var match = profilesResult.Data!.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    formatter.WriteError($"Profile \"{name}\" not found. Run 'vtorrent profile list' to see available profiles.");
                    return 1;
                }

                var exportResult = client.ExportProfileAsync(match.Name).GetAwaiter().GetResult();
                if (!exportResult.IsSuccess) return CommandHelper.WriteApiError(exportResult, formatter);

                var jsonDoc = JsonDocument.Parse(exportResult.Data!);
                var root = jsonDoc.RootElement;

                if (formatter.Mode == OutputMode.Json)
                {
                    if (root.TryGetProperty("settings", out var settings))
                        formatter.WriteJson(JsonSerializer.Deserialize<JsonObject>(settings.GetRawText()));
                    else
                        formatter.WriteJson(JsonSerializer.Deserialize<JsonObject>(root.GetRawText()));
                    return 0;
                }

                if (formatter.Mode == OutputMode.Quiet)
                {
                    formatter.WriteQuiet(match.Name);
                    return 0;
                }

                AnsiConsole.MarkupLine($"  [bold]Profile:[/] {Markup.Escape(match.Name)}");
                AnsiConsole.MarkupLine($"  [bold]Color:[/]   [{match.Color}]██[/] {match.Color}");
                AnsiConsole.MarkupLine($"  [bold]Scope:[/]   {Markup.Escape(match.Scope)}");
                AnsiConsole.WriteLine();

                if (!root.TryGetProperty("settings", out var s))
                {
                    formatter.WriteError("Profile export missing settings.");
                    return 1;
                }

                PrintGroup("Bandwidth", s, new (string, string, Func<JsonElement, string>)[]
                {
                    ("globalDownloadLimit", "Global Download Limit", FormatLimit),
                    ("globalUploadLimit", "Global Upload Limit", FormatLimit),
                    ("perTorrentDownloadLimit", "Per-Torrent Download", FormatLimit),
                    ("perTorrentUploadLimit", "Per-Torrent Upload", FormatLimit),
                    ("mixedModeAlgorithm", "Mixed Mode", FormatEnum),
                });

                PrintGroup("Connection", s, new (string, string, Func<JsonElement, string>)[]
                {
                    ("maxGlobalConnections", "Max Global Connections", FormatInt),
                    ("maxConnectionsPerTorrent", "Max Per-Torrent", FormatInt),
                    ("maxUploadsPerTorrent", "Max Uploads Per-Torrent", FormatInt),
                    ("maxHalfOpenConnections", "Max Half-Open", FormatInt),
                    ("connectionSpeed", "Connection Speed", FormatInt),
                });

                PrintGroup("Queue", s, new (string, string, Func<JsonElement, string>)[]
                {
                    ("maxActiveDownloads", "Max Active Downloads", FormatInt),
                    ("maxActiveSeeds", "Max Active Seeds", FormatUnlimitedInt),
                    ("maxActiveTorrents", "Max Active Torrents", FormatInt),
                    ("dontCountSlowTorrents", "Don't Count Slow", FormatBool),
                    ("connectSeedEveryNDownload", "Seed Every N Downloads", FormatInt),
                });

                PrintGroup("Choking", s, new (string, string, Func<JsonElement, string>)[]
                {
                    ("chokingAlgorithm", "Algorithm", FormatEnum),
                    ("seedChokingAlgorithm", "Seed Algorithm", FormatEnum),
                    ("unchokeSlots", "Unchoke Slots", FormatInt),
                    ("unchokeInterval", "Unchoke Interval", FormatSeconds),
                    ("optimisticUnchokeInterval", "Optimistic Interval", FormatSeconds),
                    ("numOptimisticUnchokeSlots", "Optimistic Slots", FormatAutoInt),
                });

                PrintGroup("Peer", s, new (string, string, Func<JsonElement, string>)[]
                {
                    ("peerTurnover", "Turnover", FormatPercent),
                    ("peerTurnoverCutoff", "Turnover Cutoff", FormatPercent),
                    ("peerTurnoverInterval", "Turnover Interval", FormatSeconds),
                    ("maxPendingBlocksPerPeer", "Max Pending Blocks", FormatInt),
                });

                PrintGroup("Disk", s, new (string, string, Func<JsonElement, string>)[]
                {
                    ("backendType", "Backend", FormatEnum),
                    ("cacheSize", "Cache Size", FormatBytes),
                    ("maxOutstandingDiskRequests", "Max Outstanding Requests", FormatInt),
                    ("hashThreads", "Hash Threads", FormatInt),
                });

                PrintGroup("Seeding", s, new (string, string, Func<JsonElement, string>)[]
                {
                    ("seedRatioLimit", "Ratio Limit", FormatUnlimitedFloat),
                    ("seedTimeLimit", "Time Limit", FormatUnlimitedMinutes),
                    ("pauseOnSeedComplete", "Pause on Complete", FormatBool),
                    ("removeOnSeedComplete", "Remove on Complete", FormatBool),
                });

                PrintGroup("Picker", s, new (string, string, Func<JsonElement, string>)[]
                {
                    ("initialPickerThreshold", "Initial Threshold", FormatInt),
                    ("wholePiecesThreshold", "Whole Pieces Threshold", FormatInt),
                });
            }

            return 0;
        });

        return command;
    }

    private static void PrintGroup(string title, JsonElement settings,
        (string key, string label, Func<JsonElement, string> format)[] fields)
    {
        AnsiConsole.MarkupLine($"  [bold underline]{title}[/]");
        foreach (var (key, label, format) in fields)
        {
            var value = settings.TryGetProperty(key, out var el) ? format(el) : "?";
            AnsiConsole.MarkupLine($"    {label,-28} {Markup.Escape(value)}");
        }
        AnsiConsole.WriteLine();
    }

    private static string FormatInt(JsonElement el) => el.GetInt32().ToString();
    private static string FormatBool(JsonElement el) => el.GetBoolean() ? "yes" : "no";
    private static string FormatEnum(JsonElement el) => el.GetString() ?? "?";
    private static string FormatSeconds(JsonElement el) => $"{el.GetInt32()}s";
    private static string FormatPercent(JsonElement el) => $"{el.GetInt32()}%";

    private static string FormatLimit(JsonElement el)
    {
        var v = el.GetInt32();
        return v == 0 ? "unlimited" : HumanUnits.FormatSpeed(v);
    }

    private static string FormatUnlimitedInt(JsonElement el)
    {
        var v = el.GetInt32();
        return v == -1 ? "unlimited" : v.ToString();
    }

    private static string FormatAutoInt(JsonElement el)
    {
        var v = el.GetInt32();
        return v == 0 ? "auto" : v.ToString();
    }

    private static string FormatBytes(JsonElement el)
    {
        var v = el.GetInt64();
        return HumanUnits.FormatBytes(v);
    }

    private static string FormatUnlimitedFloat(JsonElement el)
    {
        var v = el.GetSingle();
        return v == 0f ? "unlimited" : $"{v:F1}x";
    }

    private static string FormatUnlimitedMinutes(JsonElement el)
    {
        var v = el.GetInt32();
        return v == 0 ? "unlimited" : $"{v} min";
    }
}
