// src/vTorrent.CLI/Output/LiveTorrentDisplay.cs
using System;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Rendering;
using vTorrent.Cli.Client;

namespace vTorrent.Cli.Output;

public static class LiveTorrentDisplay
{
    /// <summary>
    /// Run a live display that refreshes on a timer and on SignalR events.
    /// Takes over the terminal until cancellation (Ctrl+C).
    /// </summary>
    public static void Run(
        Func<IRenderable> buildDisplay,
        VTorrentRealtimeClient? realtimeClient,
        int refreshMs = 1500)
    {
        using var cts = new CancellationTokenSource();

        // Ctrl+C cancels the live display, not the process
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += cancelHandler;

        // Interlocked flag for cross-thread signaling from SignalR
        var signalRefresh = 0;
        Action? onDataChanged = null;

        if (realtimeClient != null)
        {
            onDataChanged = () => Interlocked.Exchange(ref signalRefresh, 1);
            realtimeClient.DataChanged += onDataChanged;
        }

        try
        {
            var initial = buildDisplay();

            AnsiConsole.Live(initial)
                .AutoClear(true)
                .Overflow(VerticalOverflow.Ellipsis)
                .Start(ctx =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var display = buildDisplay();
                            ctx.UpdateTarget(display);
                            ctx.Refresh();
                        }
                        catch
                        {
                            // data fetch failed, keep showing last state
                        }

                        // Wait for next tick or early signal from SignalR
                        var waited = 0;
                        while (waited < refreshMs && !cts.IsCancellationRequested
                               && Interlocked.CompareExchange(ref signalRefresh, 0, 0) == 0)
                        {
                            Thread.Sleep(100);
                            waited += 100;
                        }
                        Interlocked.Exchange(ref signalRefresh, 0);
                    }
                });
        }
        catch (OperationCanceledException) { }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;

            if (realtimeClient != null && onDataChanged != null)
                realtimeClient.DataChanged -= onDataChanged;
        }
    }
}
