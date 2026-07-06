// src/vTorrent.CLI/Commands/Serve/ServeCommand.cs
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Orchestration;
using vTorrent.Core.Persistence;
using vTorrent.Core.Registration;
using vTorrent.Storage;

namespace vTorrent.Cli.Commands.Serve;

public static class ServeCommand
{
    public static Command Create()
    {
        var dataDirOption = new Option<string?>("--data-dir")
        {
            Description = "Data directory (default: platform-specific)"
        };

        var portOption = new Option<int?>("--port")
        {
            Description = "Override server listen port"
        };

        var addressOption = new Option<string?>("--address")
        {
            Description = "Override bind address"
        };

        var noHttpsOption = new Option<bool>("--no-https")
        {
            Description = "Disable HTTPS"
        };

        var logLevelOption = new Option<string>("--log-level")
        {
            Description = "Console log level (Trace, Debug, Information, Warning, Error, Critical)",
            DefaultValueFactory = _ => "Information"
        };

        var command = new Command("serve", "Start the vTorrent daemon (engine + API server)");
        command.Options.Add(dataDirOption);
        command.Options.Add(portOption);
        command.Options.Add(addressOption);
        command.Options.Add(noHttpsOption);
        command.Options.Add(logLevelOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var dataDir = parseResult.GetValue(dataDirOption);
            var port = parseResult.GetValue(portOption);
            var address = parseResult.GetValue(addressOption);
            var noHttps = parseResult.GetValue(noHttpsOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            await RunAsync(dataDir, port, address, noHttps, logLevel);
        });

        return command;
    }

    private static async Task RunAsync(
        string? dataDir,
        int? port,
        string? address,
        bool noHttps,
        string logLevelStr)
    {
        // --- Signal handling ---
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        // --- Parse log level ---
        if (!Enum.TryParse<LogLevel>(logLevelStr, ignoreCase: true, out var logLevel))
            logLevel = LogLevel.Information;

        // --- Data directory ---
        var dataDirectory = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "vTorrent");
        Directory.CreateDirectory(dataDirectory);

        // --- Logging ---
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(logLevel);
            builder.AddConsole();
        });

        var logger = loggerFactory.CreateLogger("vTorrent.Daemon");
        logger.LogInformation("vTorrent daemon starting");
        logger.LogInformation("Data directory: {DataDirectory}", dataDirectory);

        ServiceProvider? serviceProvider = null;
        SqliteConnection? serverConnection = null;

        try
        {
            // --- Build DI container ---
            var services = new ServiceCollection();
            services.AddVTorrentStorage(dataDirectory);
            services.AddVTorrentCore(loggerFactory);
            services.AddVTorrentPersistence(dataDirectory);
            serviceProvider = services.BuildServiceProvider();

            // --- Initialize persistence ---
            var persistence = serviceProvider.GetRequiredService<SessionPersistence>();
            await persistence.InitializeAsync(cts.Token);

            // --- Wire settings monitors ---
            persistence.SettingsManager?.SetMonitors(serviceProvider);

            // --- Set default save path if empty ---
            if (string.IsNullOrEmpty(persistence.Settings.Disk.DefaultSavePath))
            {
                persistence.Settings.Disk.DefaultSavePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
            }

            // --- Apply CLI overrides ---
            var settings = persistence.Settings;
            if (port.HasValue)
                settings.Server.ListenPort = port.Value;
            if (address != null)
                settings.Server.ListenAddress = address;
            if (noHttps)
                settings.Server.EnableHttps = false;

            // --- Resolve and initialize orchestrator ---
            var orchestrator = serviceProvider.GetRequiredService<TorrentOrchestrator>();
            await orchestrator.InitializeAsync();
            logger.LogInformation("Orchestrator initialized — {Count} torrents restored",
                orchestrator.TorrentsInternal.Count);

            // --- Resolve torrent service ---
            var torrentService = serviceProvider.GetRequiredService<ITorrentService>();

            // --- Create a separate SQLite connection for the server (auth tokens) ---
            // SessionPersistence uses "torrents.db", not "vtorrent.db"
            var serverDbPath = Path.Combine(dataDirectory, "torrents.db");
            serverConnection = new SqliteConnection($"Data Source={serverDbPath}");
            await serverConnection.OpenAsync(cts.Token);

            // --- Start server ---
            logger.LogInformation("Starting API server...");

            await vTorrent.Server.Program.StartAsync(
                serverConnection,
                torrentService,
                persistence.SettingsManager!,
                persistence.SettingsManager!.Current.Server,
                persistence.SettingsManager!.Current.Connection,
                serviceProvider.GetRequiredService<IOptionsMonitor<ServerSettings>>(),
                loggerFactory,
                profileManager: null,
                profileScheduler: null,
                webRootPath: null,
                cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("vTorrent daemon shutting down...");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "vTorrent daemon failed");
        }
        finally
        {
            // --- Graceful shutdown ---
            logger.LogInformation("Cleaning up...");

            if (serverConnection != null)
            {
                await serverConnection.CloseAsync();
                serverConnection.Dispose();
            }

            if (serviceProvider != null)
                await serviceProvider.DisposeAsync();

            loggerFactory.Dispose();
        }
    }
}
