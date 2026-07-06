using System;
using System.CommandLine;
using System.Threading.Tasks;
using vTorrent.Bench.Bench;
using vTorrent.Bench.Config;

namespace vTorrent.Bench;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var scenarioOption = new Option<string?>("--scenario") { Description = "Load scenario from JSON file" };
        var presetOption = new Option<string?>("--preset") { Description = "Use built-in preset (HomeDSL, Seedbox, MobileHotspot, SeederSwarm, LeecherHeavy)", DefaultValueFactory = _ => "HomeDSL" };
        var peersOption = new Option<int?>("--peers") { Description = "Override peer count" };
        var pieceCountOption = new Option<int?>("--piece-count") { Description = "Override piece count" };
        var pieceSizeOption = new Option<int?>("--piece-size") { Description = "Override piece size in bytes" };
        var torrentOption = new Option<string?>("--torrent") { Description = "Use real .torrent file" };
        var dataPathOption = new Option<string?>("--data-path") { Description = "Path to real file data (required with --torrent)" };
        var exportCsvOption = new Option<string?>("--export-csv") { Description = "Auto-export time-series CSV on exit" };

        var rootCommand = new RootCommand("vTorrent Engine Bench — synthetic test bench for performance tuning");
        rootCommand.Options.Add(scenarioOption);
        rootCommand.Options.Add(presetOption);
        rootCommand.Options.Add(peersOption);
        rootCommand.Options.Add(pieceCountOption);
        rootCommand.Options.Add(pieceSizeOption);
        rootCommand.Options.Add(torrentOption);
        rootCommand.Options.Add(dataPathOption);
        rootCommand.Options.Add(exportCsvOption);

        rootCommand.SetAction(async (parseResult, ct) =>
        {
            var scenario = parseResult.GetValue(scenarioOption);
            var preset = parseResult.GetValue(presetOption);
            var peers = parseResult.GetValue(peersOption);
            var pieceCount = parseResult.GetValue(pieceCountOption);
            var pieceSize = parseResult.GetValue(pieceSizeOption);
            var torrent = parseResult.GetValue(torrentOption);
            var dataPath = parseResult.GetValue(dataPathOption);
            var exportCsv = parseResult.GetValue(exportCsvOption);

            var config = ScenarioLoader.Load(scenario, preset, peers, pieceCount, pieceSize, torrent, dataPath);

            // Derive a human-readable scenario name
            var scenarioName = scenario != null
                ? System.IO.Path.GetFileNameWithoutExtension(scenario)
                : preset ?? "HomeDSL";

            var session = new BenchSession(config, scenarioName, exportCsv);
            await session.RunAsync(ct);
        });

        return await rootCommand.Parse(args).InvokeAsync();
    }
}
