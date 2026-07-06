using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core;
using vTorrent.Core.Orchestration;
using vTorrent.Core.Persistence;
using vTorrent.Core.Registration;
using vTorrent.Storage;
using vTorrent.Desktop.ViewModels;
using vTorrent.Desktop.ViewModels.Dialogs;
using vTorrent.Desktop.Services;
using vTorrent.Desktop.Views;

namespace vTorrent.Desktop;

public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private ServiceProvider? _serviceProvider;
    private SessionPersistence? _persistence;
    private TorrentOrchestrator? _orchestrator;
    private TorrentManagerService? _torrentManager;
    private CommandLineResult? _startupItems;
    private ServerHostService? _serverHost;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            DisableAvaloniaDataAnnotationValidation();

            // Parse command-line arguments early
            _startupItems = CommandLineHandler.Parse(Program.StartupArguments);

            // Create the main window immediately with null torrent manager
            // The actual initialization happens asynchronously
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Initialize services asynchronously
            _ = InitializeServicesAsync(mainWindow);

            // Platform-specific tray icon setup
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: Avalonia's NativeMenu renders poorly on Win11,
                // so we use our own Win32 Shell_NotifyIcon + custom popup
                SetupWin32TrayIcon();
            }
            else
            {
                // Linux/macOS: Avalonia's TrayIcon + NativeMenu delegates to
                // the OS native menu system and renders correctly
                SetupAvaloniaTrayIcon();
            }

            // Handle shutdown
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeServicesAsync(MainWindow mainWindow)
    {
        try
        {
            // Setup data directory
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "vTorrent");
            Directory.CreateDirectory(dataDirectory);

            // Setup logging - store as field so it doesn't get disposed
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                // Suppress noisy categories to Debug — only shows in debugger, not console
                builder.AddFilter("vTorrent.Core.DHT", LogLevel.Warning);
                builder.AddFilter("vTorrent.Core.PeerCommunication", LogLevel.Warning);
                builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
                builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
                // Keep server lifecycle and settings visible
                builder.AddFilter("vTorrent.Desktop.Services.ServerHostService", LogLevel.Debug);
                builder.AddConsole();  // Output to console
                builder.AddDebug();    // Output to VS/Rider debug window
            });

            // Build DI container
            var services = new ServiceCollection();
            services.AddVTorrentStorage(dataDirectory);
            services.AddVTorrentCore(_loggerFactory);
            services.AddVTorrentPersistence(dataDirectory);
            services.AddVTorrentDesktop(this);
            _serviceProvider = services.BuildServiceProvider();

            // Resolve and initialize persistence (requires async init)
            _persistence = _serviceProvider.GetRequiredService<SessionPersistence>();
            await _persistence.InitializeAsync();

            // Wire settings monitors to SettingsManager for live change notification
            if (_persistence.SettingsManager != null)
            {
                _persistence.SettingsManager.SetMonitors(_serviceProvider);
            }

            // Set default save path if not configured
            if (string.IsNullOrEmpty(_persistence.Settings.Disk.DefaultSavePath))
            {
                _persistence.Settings.Disk.DefaultSavePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
            }

            // Set persistence on MainWindow for window state
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindow.SetPersistence(_persistence);
                mainWindow.SetServiceProvider(_serviceProvider);
            });

            // Restore window state (async)
            await mainWindow.RestoreWindowStateAsync();

            // Resolve orchestrator (DI wires ResourceAllocator, AlertManager, etc.)
            _orchestrator = _serviceProvider.GetRequiredService<TorrentOrchestrator>();

            // Resolve and initialize torrent manager service
            _torrentManager = (TorrentManagerService)_serviceProvider.GetRequiredService<ITorrentManagerService>();
            await _torrentManager.InitializeAsync();

            // Initialize notification service (factory in ServiceRegistration handles Initialize)
            var notificationService = _serviceProvider.GetRequiredService<INotificationService>();
            _torrentManager.SetNotificationService(notificationService);

            // Initialize theme service (SettingsManager injected via factory in ServiceRegistration)
            var themeService = _serviceProvider.GetRequiredService<IThemeService>();
            themeService.Initialize();
            _torrentManager.SetThemeService(themeService);

            // Start embedded web server if enabled
            try
            {
                _serverHost = _serviceProvider.GetRequiredService<ServerHostService>();
                await _serverHost.InitializeAsync();
            }
            catch (Exception serverEx)
            {
                Console.WriteLine($"[ServerHost] Failed to initialize: {serverEx}");
                // Non-fatal — app continues without embedded server
            }

            // Load view state for the view model
            var viewState = await _persistence.LoadViewStateAsync();

            // Create the ViewModel with the initialized service and view state
            var viewModel = new MainWindowViewModel(_torrentManager, _persistence, viewState);

            // Inject ProfileManager into the ViewModel
            var profileManager = _serviceProvider?.GetService<Core.Settings.ProfileManager>();
            if (profileManager != null)
            {
                viewModel.SetProfileManager(profileManager);
            }

            // Set the DataContext and torrent manager on the UI thread
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindow.SetTorrentManager(_torrentManager);
                mainWindow.DataContext = viewModel;
            });

            // Load initial profile state (name, color, drift)
            await viewModel.LoadProfileStateAsync();

            // Process any startup items (torrent files or magnet links passed via command line)
            if (_startupItems != null && _startupItems.HasValidItems)
            {
                await ProcessStartupItemsAsync(mainWindow);
            }

            // Listen for args forwarded from subsequent instances (file associations, magnet links)
            if (Program.InstanceGuard != null)
            {
                Program.InstanceGuard.ArgumentsReceived += args =>
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        // Always bring the window to front
                        ShowMainWindow();

                        // Process any forwarded torrent files or magnet links
                        if (args.Length > 0)
                        {
                            var parsed = CommandLineHandler.Parse(args);
                            if (parsed.HasValidItems)
                            {
                                foreach (var item in parsed.Items)
                                {
                                    try
                                    {
                                        await mainWindow.ProcessStartupItemAsync(item);
                                    }
                                    catch (Exception ex2)
                                    {
                                        Console.WriteLine($"Failed to process forwarded item {item.Value}: {ex2.Message}");
                                    }
                                }
                            }
                        }
                    });
                };
            }

            // Start minimized to tray if configured
            if (_persistence.Settings.UI.StartMinimizedToTray)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    mainWindow.Hide();
                });
            }
        }
        catch (Exception ex)
        {
            // Log error and continue with sample data
            Console.WriteLine($"Failed to initialize torrent services: {ex.Message}");

            // Fallback to sample data mode
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindow.DataContext = new MainWindowViewModel(null);
            });
        }
    }

    /// <summary>
    /// Process startup items (torrent files and magnet links) passed via command line.
    /// </summary>
    private async Task ProcessStartupItemsAsync(MainWindow mainWindow)
    {
        if (_startupItems == null || !_startupItems.HasValidItems)
            return;

        // Small delay to ensure the window is fully visible and ready
        await Task.Delay(500);

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // Process all startup items
            foreach (var item in _startupItems.Items)
            {
                try
                {
                    await mainWindow.ProcessStartupItemAsync(item);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to process startup item {item.Value}: {ex.Message}");
                }
            }

            // Show errors for invalid items
            foreach (var item in _startupItems.InvalidItems)
            {
                var errorTitle = item.Type == StartupItemType.TorrentFile
                    ? "Invalid Torrent File"
                    : "Invalid Magnet Link";
                mainWindow.ShowToast(errorTitle, item.ErrorMessage ?? "Unknown error", Controls.ToastType.Error);
            }
        });
    }

    private bool _isShuttingDown;

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // Prevent re-entrancy
        if (_isShuttingDown)
            return;

        // Cancel Avalonia's synchronous shutdown — we'll handle it async
        e.Cancel = true;
        _isShuttingDown = true;

        // Phase 1: Hide window and clean up tray icon immediately
        var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        desktop?.MainWindow?.Hide();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _win32TrayIcon?.Dispose();
            _win32TrayIcon = null;
        }

        // Phase 2: Background cleanup with safety timeout
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await BackgroundShutdownAsync().WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Safety timeout hit — force exit
            }
            catch (Exception)
            {
                // Best effort — don't block shutdown
            }
            finally
            {
                // Dispatch actual shutdown to UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    try { desktop?.Shutdown(); }
                    catch { Environment.Exit(0); }
                });
            }
        });
    }

    private async Task BackgroundShutdownAsync()
    {
        // Clean shutdown of services — saves all torrent state
        if (_torrentManager != null)
        {
            await _torrentManager.DisposeAsync();
        }

        if (_serverHost != null)
        {
            await _serverHost.StopAsync();
        }

        // Dispose DI container and logger
        if (_serviceProvider != null)
            await _serviceProvider.DisposeAsync();
        _loggerFactory?.Dispose();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    #region Tray Icon Handlers

    private bool _isSessionPaused;

    // ── Avalonia TrayIcon handlers (Linux/macOS — NativeMenu from XAML) ──

    private void TrayIcon_Clicked(object? sender, EventArgs e) => ShowMainWindow();
    private void TrayMenu_Show(object? sender, EventArgs e) => ShowMainWindow();

    private void TrayMenu_AddTorrent(object? sender, EventArgs e)
    {
        var mw = GetMainWindow();
        if (mw != null) { ShowMainWindow(); mw.OpenAddTorrentDialog(); }
    }

    private void TrayMenu_AddMagnet(object? sender, EventArgs e)
    {
        var mw = GetMainWindow();
        if (mw != null) { ShowMainWindow(); mw.OpenAddMagnetDialog(); }
    }

    private void TrayMenu_SpeedLimits(object? sender, EventArgs e)
    {
        if (_persistence?.SettingsManager == null || _torrentManager == null) return;
        Dispatcher.UIThread.Post(async () =>
        {
            var vm = new SpeedLimitsDialogViewModel(_persistence.SettingsManager, _torrentManager);
            var dialog = new SpeedLimitsDialog { DataContext = vm };
            var mainWindow = GetMainWindow();
            if (mainWindow != null && mainWindow.IsVisible)
                await dialog.ShowDialog(mainWindow);
            else
                dialog.Show();
        });
    }

    private void TrayMenu_PauseResume(object? sender, EventArgs e)
    {
        if (_torrentManager == null) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (_isSessionPaused)
                await _torrentManager.Service.ResumeAllAsync();
            else
                await _torrentManager.Service.PauseAllAsync();
            _isSessionPaused = !_isSessionPaused;

            if (sender is NativeMenuItem menuItem)
                menuItem.Header = _isSessionPaused ? "Resume Session" : "Pause Session";
        });
    }

    private void TrayMenu_Quit(object? sender, EventArgs e)
    {
        GetMainWindow()?.RequestClose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void SetupAvaloniaTrayIcon()
    {
        var icon = new TrayIcon
        {
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://vTorrent/Assets/Images/logo256x256.ico"))),
            ToolTipText = "vTorrent"
        };
        icon.Clicked += TrayIcon_Clicked;

        var menu = new NativeMenu();
        var showItem = new NativeMenuItem { Header = "Show vTorrent" };
        showItem.Click += TrayMenu_Show;
        menu.Items.Add(showItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        var addTorrent = new NativeMenuItem { Header = "Add Torrent File..." };
        addTorrent.Click += TrayMenu_AddTorrent;
        menu.Items.Add(addTorrent);

        var addMagnet = new NativeMenuItem { Header = "Add Magnet Link..." };
        addMagnet.Click += TrayMenu_AddMagnet;
        menu.Items.Add(addMagnet);
        menu.Items.Add(new NativeMenuItemSeparator());

        var speedLimits = new NativeMenuItem { Header = "Set Global Speed Limits..." };
        speedLimits.Click += TrayMenu_SpeedLimits;
        menu.Items.Add(speedLimits);

        var pauseResume = new NativeMenuItem { Header = "Pause Session" };
        pauseResume.Click += TrayMenu_PauseResume;
        menu.Items.Add(pauseResume);
        menu.Items.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem { Header = "Quit" };
        quit.Click += TrayMenu_Quit;
        menu.Items.Add(quit);

        icon.Menu = menu;

        var icons = new TrayIcons { icon };
        TrayIcon.SetIcons(this, icons);
    }

    // ── Win32 tray icon (Windows only — custom popup) ──

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private Win32TrayIcon? _win32TrayIcon;
    private TrayMenuPopup? _trayPopup;

    private void SetupWin32TrayIcon()
    {
        try
        {
            // Extract icon from Avalonia resources to a temp file for Win32 LoadImage
            var tempIconPath = Path.Combine(Path.GetTempPath(), "vtorrent_tray.ico");
            using (var stream = Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://vTorrent/Assets/Images/logo256x256.ico")))
            using (var fs = File.Create(tempIconPath))
                stream.CopyTo(fs);

            _win32TrayIcon = new Win32TrayIcon(tempIconPath, "vTorrent");
            _win32TrayIcon.LeftClicked += () =>
                Dispatcher.UIThread.Post(ShowMainWindow);
            _win32TrayIcon.RightClicked += () =>
                Dispatcher.UIThread.Post(ShowTrayMenuPopup);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create Win32 tray icon: {ex.Message}");
        }
    }

    private void ShowTrayMenuPopup()
    {
        try { _trayPopup?.Close(); } catch { /* already closed */ }

        GetCursorPos(out var pt);

        var popup = new TrayMenuPopup();
        popup.SetPauseResumeText(_isSessionPaused);

        // No Width/Height on Window — SizeToContent wraps the Border (which has Width=200)

        popup.ShowMainWindowRequested += () => ShowMainWindow();
        popup.AddTorrentRequested += () =>
        {
            var mw = GetMainWindow();
            if (mw != null) { ShowMainWindow(); mw.OpenAddTorrentDialog(); }
        };
        popup.AddMagnetRequested += () =>
        {
            var mw = GetMainWindow();
            if (mw != null) { ShowMainWindow(); mw.OpenAddMagnetDialog(); }
        };
        popup.SpeedLimitsRequested += () =>
        {
            if (_persistence?.SettingsManager == null || _torrentManager == null) return;
            var vm = new SpeedLimitsDialogViewModel(_persistence.SettingsManager, _torrentManager);
            var dialog = new SpeedLimitsDialog { DataContext = vm };
            var mainWindow = GetMainWindow();
            if (mainWindow != null && mainWindow.IsVisible)
                _ = dialog.ShowDialog(mainWindow);
            else
                dialog.Show();
        };
        popup.PauseResumeRequested += () =>
        {
            if (_torrentManager == null) return;
            _ = Task.Run(async () =>
            {
                if (_isSessionPaused)
                    await _torrentManager.Service.ResumeAllAsync().ConfigureAwait(false);
                else
                    await _torrentManager.Service.PauseAllAsync().ConfigureAwait(false);
                _isSessionPaused = !_isSessionPaused;
            });
        };
        popup.QuitRequested += () =>
        {
            GetMainWindow()?.RequestClose();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };

        // Pure Win32: get monitor working area at cursor position
        var monInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var hMon = MonitorFromPoint(pt, 0x02 /* MONITOR_DEFAULTTONEAREST */);
        GetMonitorInfoW(hMon, ref monInfo);
        var work = monInfo.rcWork;

        // Subscribe BEFORE Show() — Opened fires during Show()
        popup.Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var handle = popup.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (handle == IntPtr.Zero) return;

                double dpi = GetDpiForWindow(handle) / 96.0;

                // Get the BORDER's actual size (not the 800x600 window)
                var border = popup.Content as Avalonia.Controls.Border;
                int bw = (int)((border?.Bounds.Width ?? 200) * dpi);
                int bh = (int)((border?.Bounds.Height ?? 195) * dpi);
                int gap = (int)(12 * dpi);

                // Position window so border's bottom-right is above taskbar near cursor
                int x = pt.X - bw;
                int y = work.Bottom - bh - gap;

                if (x < work.Left + gap) x = work.Left + gap;
                if (x + bw > work.Right - gap) x = work.Right - bw - gap;
                if (y < work.Top + gap) y = work.Top + gap;

                // Move window — SWP_NOSIZE keeps the 800x600 (transparent areas are invisible)
                const uint SWP_NOSIZE = 0x0001;
                SetWindowPos(handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE);

#if DEBUG
                var dbg = $"cursor=({pt.X},{pt.Y}) work=({work.Left},{work.Top},{work.Right},{work.Bottom}) dpi={dpi:F2} border=({bw}x{bh}) pos=({x},{y})";
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "vtorrent_traymenu_debug.txt"), dbg);
#endif
            });
        };

        popup.Show();
        popup.Activate();
        _trayPopup = popup;
    }

    private void ShowMainWindow()
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null) return;

        mainWindow.Show();
        mainWindow.Activate();

        if (mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
            mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
    }

    private MainWindow? GetMainWindow()
    {
        return (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;
    }

    #endregion
}
