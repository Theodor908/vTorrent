using System;
using Avalonia;
using vTorrent.Core;

namespace vTorrent.Desktop;

public class Program
{
    /// <summary>
    /// The command-line arguments passed to the application.
    /// Used for file associations and magnet link handling.
    /// </summary>
    public static string[] StartupArguments { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Single-instance guard shared with App for receiving forwarded args.
    /// </summary>
    public static SingleInstanceGuard? InstanceGuard { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Store arguments for later processing by the App
        StartupArguments = args ?? Array.Empty<string>();

        // Single-instance check: if another instance is running,
        // forward our args to it via named pipe and exit.
        InstanceGuard = new SingleInstanceGuard();
        if (!InstanceGuard.TryAcquire(StartupArguments))
        {
            // Args sent to existing instance — exit quietly
            InstanceGuard.Dispose();
            return;
        }

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            InstanceGuard.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
