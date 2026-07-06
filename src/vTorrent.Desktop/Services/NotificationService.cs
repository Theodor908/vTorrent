using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Options;
using Microsoft.Toolkit.Uwp.Notifications;
using SkiaSharp;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
#if WINDOWS
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
#endif

namespace vTorrent.Desktop.Services;

/// <summary>
/// Cross-platform notification service.
/// Uses runtime OS detection to select the best notification mechanism:
/// - Windows: WinRT toast notifications with explicit AUMID for unpackaged desktop apps
/// - Linux: notify-send / zenity / kdialog
/// - macOS: osascript / terminal-notifier
/// </summary>
public class NotificationService : INotificationService
{
    private const string AppAumid = "vTorrent";

    private static bool _isRegistered;
    private static bool _shortcutCreated;
    private static string? _cachedLogoPath;
    private static string? _cachedIcoPath;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "vTorrent", "notification.log");

    public bool IsEnabled { get; set; } = true;
    public bool NotifyOnDownloadComplete { get; set; } = true;
    public bool NotifyOnDownloadFailed { get; set; } = true;
    public bool NotifyOnTorrentAdded { get; set; } = false;
    public bool PlaySound { get; set; } = true;

    private readonly List<NotificationHistoryItem> _history = new();
    private readonly object _historyLock = new();
    private const int MaxHistoryItems = 50;

    private readonly IDisposable? _settingsChangeRegistration;

    public event EventHandler<bool>? SettingsChanged;
    public event EventHandler<InAppNotificationEventArgs>? InAppNotificationRequested;

    public NotificationService(IOptionsMonitor<UISettings> uiSettingsMonitor)
    {
        ApplySettings(uiSettingsMonitor.CurrentValue);

        _settingsChangeRegistration = uiSettingsMonitor.OnChange(s =>
        {
            ApplySettings(s);
            SettingsChanged?.Invoke(this, IsEnabled);
        });

        if (OperatingSystem.IsWindows())
        {
            EnsureAppRegistered();
            EnsureStartMenuShortcut();
        }

        LogDebug("NotificationService initialized");
    }

    private void ApplySettings(UISettings settings)
    {
        IsEnabled = settings.NotificationsEnabled;
        NotifyOnDownloadComplete = settings.NotifyOnDownloadComplete;
        NotifyOnDownloadFailed = settings.NotifyOnDownloadFailed;
        NotifyOnTorrentAdded = settings.NotifyOnTorrentAdded;
        PlaySound = settings.PlayNotificationSound;

        LogDebug($"ApplySettings: IsEnabled={IsEnabled}, Complete={NotifyOnDownloadComplete}, " +
                 $"Failed={NotifyOnDownloadFailed}, Added={NotifyOnTorrentAdded}");
    }

    public void ShowDebugNotification()
    {
        LogDebug("ShowDebugNotification called");

        var wasEnabled = IsEnabled;
        IsEnabled = true;

        Show("vTorrent", "Notification system is working!", NotificationType.Info);

        IsEnabled = wasEnabled;
    }

    public void Show(string title, string message, NotificationType type = NotificationType.Info)
    {
        LogDebug($"Show called: IsEnabled={IsEnabled}, title={title}, message={message}");

        lock (_historyLock)
        {
            _history.Insert(0, new NotificationHistoryItem(title, message, type, DateTime.Now));
            if (_history.Count > MaxHistoryItems)
                _history.RemoveAt(_history.Count - 1);
        }

        if (!IsEnabled)
        {
            LogDebug("Notification skipped - disabled");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                ShowWindowsToastNotification(title, message, type);
            }
            else if (OperatingSystem.IsLinux())
            {
                ShowLinuxNotification(title, message);
            }
            else if (OperatingSystem.IsMacOS())
            {
                ShowMacNotification(title, message);
            }
            else
            {
                LogDebug($"Notification (unsupported platform): [{type}] {title} - {message}");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to show notification: {ex.Message}\n{ex.StackTrace}");
        }

        // Always fire in-app notification as fallback
        InAppNotificationRequested?.Invoke(this, new InAppNotificationEventArgs(title, message, type));
    }

    public IReadOnlyList<NotificationHistoryItem> GetHistory()
    {
        lock (_historyLock)
        {
            return _history.ToList();
        }
    }

    public void NotifyDownloadComplete(string torrentName)
    {
        LogDebug($"NotifyDownloadComplete called: {torrentName}");

        if (!NotifyOnDownloadComplete) return;

        Show("Download Complete",
            $"'{torrentName}' has been downloaded successfully.",
            NotificationType.Success);
    }

    public void NotifyDownloadFailed(string torrentName, string? error = null)
    {
        LogDebug($"NotifyDownloadFailed called: {torrentName}");

        if (!NotifyOnDownloadFailed) return;

        var message = string.IsNullOrEmpty(error)
            ? $"Download failed for '{torrentName}'."
            : $"Download failed for '{torrentName}'.\n  Reason: {error}";

        Show("Download Error", message, NotificationType.Error);
    }

    public void NotifyTorrentAdded(string torrentName)
    {
        LogDebug($"NotifyTorrentAdded called: {torrentName}");

        if (!NotifyOnTorrentAdded) return;

        Show("Torrent Added",
            $"'{torrentName}' was added.",
            NotificationType.Info);
    }

    public void NotifySeedingLimitReached(string torrentName, string limitType, string action)
    {
        var actionText = action.ToLowerInvariant() switch
        {
            "pause" => "paused",
            "remove" => "removed",
            _ => action.ToLowerInvariant()
        };

        var limitText = limitType.ToLowerInvariant() switch
        {
            "ratio" => "share ratio",
            "time" => "seeding time",
            _ => limitType.ToLowerInvariant()
        };

        Show("Seeding Limit Reached",
            $"'{torrentName}' has been {actionText} after reaching its {limitText} limit.",
            NotificationType.Warning);
    }

    #region Windows Toast Notifications

    /// <summary>
    /// Shows a Windows toast notification.
    /// Builds toast content with the Toolkit builder, then shows it via WinRT
    /// with an explicit AUMID (required for unpackaged desktop apps).
    /// </summary>
    private void ShowWindowsToastNotification(string title, string message, NotificationType type)
    {
        try
        {
            EnsureAppRegistered();
            EnsureStartMenuShortcut();

            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);

            // Use Phosphor icon rendered to PNG for the body icon (type-specific)
            var iconPath = GetNotificationIconPath(type);
            if (!string.IsNullOrEmpty(iconPath))
            {
                builder.AddAppLogoOverride(new Uri(iconPath), ToastGenericAppLogoCrop.None);
            }

            if (!PlaySound)
            {
                builder.AddAudio(new ToastAudio { Silent = true });
            }

            var toastXml = builder.Content.GetContent();
            LogDebug($"Toast XML built, showing notification...");

#if WINDOWS
            ShowToastViaWinRT(toastXml);
#else
            ShowToastXmlViaPowerShell(toastXml);
#endif
        }
        catch (Exception ex)
        {
            LogDebug($"Windows toast notification failed: {ex.Message}\n{ex.StackTrace}");
            ShowWindowsBalloonFallback(title, message);
        }
    }

#if WINDOWS
    /// <summary>
    /// Shows a toast notification using WinRT APIs directly with an explicit AUMID.
    /// This is the key fix for unpackaged desktop apps: builder.Show() calls
    /// CreateToastNotifier() without AUMID, which silently fails.
    /// Using CreateToastNotifier("vTorrent") with explicit AUMID works correctly.
    /// </summary>
    private static void ShowToastViaWinRT(string toastXml)
    {
        var xml = new XmlDocument();
        xml.LoadXml(toastXml);

        var toast = new ToastNotification(xml)
        {
            Tag = AppAumid,
            Group = AppAumid
        };

        var notifier = ToastNotificationManager.CreateToastNotifier(AppAumid);
        notifier.Show(toast);

        LogDebug("Toast shown via WinRT with explicit AUMID");
    }
#else
    /// <summary>
    /// Shows a toast notification using WinRT API via PowerShell.
    /// Used when the app is compiled with the generic net10.0 TFM but running on Windows.
    /// </summary>
    private static void ShowToastXmlViaPowerShell(string toastXml)
    {
        try
        {
            var tempXmlFile = Path.Combine(Path.GetTempPath(), "vtorrent_toast.xml");
            File.WriteAllText(tempXmlFile, toastXml);

            var escapedXmlPath = tempXmlFile.Replace("'", "''");

            var script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null

$xmlStr = [IO.File]::ReadAllText('{escapedXmlPath}')
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($xmlStr)
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppAumid}')
$notifier.Show($toast)

Remove-Item -Path '{escapedXmlPath}' -Force -ErrorAction SilentlyContinue
";
            var tempScript = Path.Combine(Path.GetTempPath(), "vtorrent_notify.ps1");
            File.WriteAllText(tempScript, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);

                if (!string.IsNullOrEmpty(error))
                {
                    LogDebug($"PowerShell WinRT toast error: {error}");
                }
                else
                {
                    LogDebug("Toast shown via PowerShell WinRT");
                }
            }

            try { File.Delete(tempScript); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            LogDebug($"PowerShell WinRT toast failed: {ex.Message}");
        }
    }
#endif

    private static void EnsureAppRegistered()
    {
        if (_isRegistered) return;

        try
        {
            // Use small icon for AUMID registry (toast header ~20px) to prevent overlap
            var iconPath = GetToastHeaderIconPath();

#if WINDOWS
            RegisterAumidViaRegistry(iconPath);
#else
            RegisterAumidViaPowerShell(iconPath);
#endif

            _isRegistered = true;
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to register app for notifications: {ex.Message}");
            _isRegistered = true;
        }
    }

#if WINDOWS
    private static void RegisterAumidViaRegistry(string? iconPath)
    {
        try
        {
            using var aumidRoot = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + AppAumid);
            if (aumidRoot != null)
            {
                aumidRoot.SetValue("DisplayName", "vTorrent", RegistryValueKind.String);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    aumidRoot.SetValue("IconUri", iconPath, RegistryValueKind.ExpandString);
                    aumidRoot.SetValue("IconBackgroundColor", "FF1A1A2E", RegistryValueKind.String);
                }
                LogDebug($"Registered AUMID '{AppAumid}' with icon: {iconPath}");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to register AUMID via registry: {ex.Message}");
        }
    }
#else
    private static void RegisterAumidViaPowerShell(string? iconPath)
    {
        try
        {
            var script = $@"
$aumidPath = 'HKCU:\Software\Classes\AppUserModelId\{AppAumid}'
if (-not (Test-Path $aumidPath)) {{
    New-Item -Path $aumidPath -Force | Out-Null
}}
Set-ItemProperty -Path $aumidPath -Name 'DisplayName' -Value 'vTorrent' -Force
";
            if (!string.IsNullOrEmpty(iconPath))
            {
                script += $@"
Set-ItemProperty -Path $aumidPath -Name 'IconUri' -Value '{EscapePowerShell(iconPath)}' -Force
Set-ItemProperty -Path $aumidPath -Name 'IconBackgroundColor' -Value 'FF1A1A2E' -Force
";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{EscapePowerShell(script)}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
            LogDebug($"Registered AUMID '{AppAumid}' via PowerShell with icon: {iconPath}");
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to register AUMID via PowerShell: {ex.Message}");
        }
    }
#endif

    #endregion

    #region Start Menu Shortcut (required for unpackaged desktop app toast delivery)

    // Windows requires a Start Menu shortcut with AppUserModelID for toast
    // notifications from unpackaged desktop apps. Without it,
    // CreateToastNotifier(aumid) may silently drop the toast.

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PropertyKey pkey);
        int GetValue(ref PropertyKey key, [System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] PropVariant pv);
        int SetValue(ref PropertyKey key, PropVariant pv);
        int Commit();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private class PropVariant : IDisposable
    {
        public ushort vt;
        private readonly ushort _wReserved1;
        private readonly ushort _wReserved2;
        private readonly ushort _wReserved3;
        public IntPtr p;
        private readonly int _p2;

        public void Dispose()
        {
            if (p != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
                p = IntPtr.Zero;
            }
        }
    }

    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");

    /// <summary>
    /// Ensures a Start Menu shortcut exists with the AppUserModelID set.
    /// This is required for toast notification delivery from unpackaged desktop apps.
    /// </summary>
    private static void EnsureStartMenuShortcut()
    {
        if (_shortcutCreated) return;

        try
        {
            var startMenuPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\vTorrent.lnk");

            if (File.Exists(startMenuPath))
            {
                _shortcutCreated = true;
                return;
            }

            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            // Create the shortcut using WScript.Shell COM
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(startMenuPath);
            shortcut.TargetPath = exePath;
            shortcut.Description = "vTorrent BitTorrent Client";
            var iconPath = GetShortcutIconPath();
            if (!string.IsNullOrEmpty(iconPath))
                shortcut.IconLocation = iconPath + ",0";
            shortcut.Save();

            // Set AppUserModelID property on the shortcut via IPropertyStore COM
            SetShortcutAppUserModelId(startMenuPath, AppAumid);

            _shortcutCreated = true;
            LogDebug($"Created Start Menu shortcut with AUMID at: {startMenuPath}");
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to create Start Menu shortcut: {ex.Message}");
            _shortcutCreated = true; // Don't retry on failure
        }
    }

    private static void SetShortcutAppUserModelId(string shortcutPath, string aumid)
    {
        var shellLinkType = Type.GetTypeFromCLSID(CLSID_ShellLink)
                            ?? throw new InvalidOperationException("ShellLink CLSID not found");
        var shellLink = Activator.CreateInstance(shellLinkType)!;

        // Load existing shortcut
        ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink).Load(shortcutPath, 0);

        // Set the AppUserModelID property via IPropertyStore
        var propertyStore = (IPropertyStore)shellLink;
        var appUserModelIdKey = new PropertyKey
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5 // System.AppUserModel.ID
        };

        using var pv = new PropVariant
        {
            vt = 31, // VT_LPWSTR
            p = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(aumid)
        };

        propertyStore.SetValue(ref appUserModelIdKey, pv);
        propertyStore.Commit();

        // Save the modified shortcut
        ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink).Save(shortcutPath, true);
    }

    #endregion

    /// <summary>
    /// Last-resort balloon tip fallback if toast notification fails.
    /// </summary>
    private void ShowWindowsBalloonFallback(string title, string message)
    {
        try
        {
            LogDebug("Trying BalloonTip fallback notification");

            var script = $@"
[void] [System.Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms')
$n = New-Object System.Windows.Forms.NotifyIcon
$n.Icon = [System.Drawing.SystemIcons]::Information
$n.BalloonTipTitle = '{EscapePowerShell(title)}'
$n.BalloonTipText = '{EscapePowerShell(message)}'
$n.Visible = $true
$n.ShowBalloonTip(5000)
Start-Sleep -Milliseconds 5100
$n.Dispose()
";
            var tempScript = Path.Combine(Path.GetTempPath(), "vtorrent_notify.ps1");
            File.WriteAllText(tempScript, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);

                if (!string.IsNullOrEmpty(error))
                {
                    LogDebug($"BalloonTip fallback error: {error}");
                }
                else
                {
                    LogDebug("BalloonTip fallback notification shown");
                }
            }

            try { File.Delete(tempScript); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            LogDebug($"BalloonTip fallback failed: {ex.Message}");
        }
    }

    private static string EscapePowerShell(string input)
    {
        return input.Replace("'", "''").Replace("\"", "`\"");
    }

    #region Icon Path Resolution

    // Phosphor icon codepoints (must match Controls/ToastNotification.axaml)
    private static readonly Dictionary<NotificationType, (char glyph, uint color)> NotificationIcons = new()
    {
        [NotificationType.Info]    = ('\uE20A', 0xFF3B82F6), // InfoBlue — download icon
        [NotificationType.Success] = ('\uE40C', 0xFF10B981), // SuccessGreen — check/seal
        [NotificationType.Warning] = ('\uE4E0', 0xFFF59E0B), // WarningOrange — warning triangle
        [NotificationType.Error]   = ('\uE4E4', 0xFFEF4444), // ErrorRed — x-circle
    };

    private static readonly Dictionary<NotificationType, string> _cachedIconPaths = new();
    private static SKTypeface? _phosphorTypeface;

    /// <summary>
    /// Gets (or renders) a Phosphor icon PNG for the given notification type.
    /// Renders the glyph as a white icon on a colored circle, cached to disk.
    /// </summary>
    private static string? GetNotificationIconPath(NotificationType type)
    {
        if (_cachedIconPaths.TryGetValue(type, out var cached) && File.Exists(cached))
            return cached;

        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "vTorrent", "icons");

            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            var pngPath = Path.Combine(cacheDir, $"notify_{type.ToString().ToLowerInvariant()}.png");

            // Return cached file if already rendered
            if (File.Exists(pngPath))
            {
                _cachedIconPaths[type] = pngPath;
                return pngPath;
            }

            if (!NotificationIcons.TryGetValue(type, out var icon))
                return GetAppLogoPath(); // fallback

            // Load Phosphor font
            _phosphorTypeface ??= LoadPhosphorTypeface();
            if (_phosphorTypeface == null)
            {
                LogDebug("Phosphor typeface not found, falling back to app logo");
                return GetAppLogoPath();
            }

            RenderIconToPng(pngPath, icon.glyph, icon.color, _phosphorTypeface);
            _cachedIconPaths[type] = pngPath;
            LogDebug($"Rendered Phosphor notification icon: {pngPath}");
            return pngPath;
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to render notification icon: {ex.Message}");
            return GetAppLogoPath();
        }
    }

    private static SKTypeface? LoadPhosphorTypeface()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        // Try the font from the Assets folder (Avalonia resource path)
        var fontPaths = new[]
        {
            Path.Combine(appDir, "Assets", "Fonts", "Phosphor.ttf"),
            // Fallback: look relative to the source directory during development
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "Fonts", "Phosphor.ttf"),
        };

        foreach (var fontPath in fontPaths)
        {
            if (File.Exists(fontPath))
            {
                var tf = SKTypeface.FromFile(fontPath);
                if (tf != null)
                {
                    LogDebug($"Loaded Phosphor typeface from: {fontPath}");
                    return tf;
                }
            }
        }

        LogDebug("Phosphor.ttf not found in any expected location");
        return null;
    }

    /// <summary>
    /// Renders a Phosphor glyph as a white icon centered on a colored circle, saved as PNG.
    /// Matches the style of qBittorrent's toast body icons (e.g., blue circle + white "i").
    /// </summary>
    private static void RenderIconToPng(string outputPath, char glyph, uint circleColor, SKTypeface typeface)
    {
        const int size = 48;
        const int padding = 4;
        var center = size / 2f;
        var radius = (size - padding * 2) / 2f;

        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Transparent);

        // Draw colored circle background
        using var circlePaint = new SKPaint
        {
            Color = new SKColor(circleColor),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawCircle(center, center, radius, circlePaint);

        // Draw white Phosphor glyph centered
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Typeface = typeface,
            TextSize = 24,
            TextAlign = SKTextAlign.Center,
        };

        // Vertical centering: measure the glyph bounds
        var glyphStr = glyph.ToString();
        var textBounds = new SKRect();
        textPaint.MeasureText(glyphStr, ref textBounds);
        var textY = center - textBounds.MidY;

        canvas.DrawText(glyphStr, center, textY, textPaint);
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }

    private static string? GetAppLogoPath()
    {
        if (_cachedLogoPath != null && File.Exists(_cachedLogoPath))
        {
            return _cachedLogoPath;
        }

        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;

            // Prefer PNG for toast header icon, then larger ICO files
            var logoPaths = new[]
            {
                Path.Combine(appDir, "Assets", "Images", "dark_logo48x48.png"),
                Path.Combine(appDir, "Assets", "Images", "logo256x256.ico"),
                Path.Combine(appDir, "Assets", "Images", "dark_logo128x128.ico"),
                Path.Combine(appDir, "Assets", "Images", "dark_logo32x32.ico"),
            };

            foreach (var path in logoPaths)
            {
                if (File.Exists(path))
                {
                    LogDebug($"Found app logo at: {path}");
                    _cachedLogoPath = path;
                    return path;
                }
            }

            var extractedPath = ExtractEmbeddedIcon();
            if (extractedPath != null)
            {
                _cachedLogoPath = extractedPath;
                return extractedPath;
            }

            LogDebug("No app logo found in any expected location");
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to get app logo path: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Returns a small PNG for the AUMID registry IconUri (toast header icon).
    /// The toast header renders at ~20px. The shipped .ico files are multi-resolution
    /// containers (200+ KB each), so Windows picks the largest frame and renders it
    /// at native size, overlapping the app name text. To fix this, we render a
    /// properly-sized 20x20 PNG from the source icon using SkiaSharp.
    /// </summary>
    private static string? _cachedHeaderIconPath;

    private static string? GetToastHeaderIconPath()
    {
        if (_cachedHeaderIconPath != null && File.Exists(_cachedHeaderIconPath))
            return _cachedHeaderIconPath;

        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "vTorrent", "icons");

            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            var pngPath = Path.Combine(cacheDir, "toast_header_20x20.png");

            if (File.Exists(pngPath))
            {
                _cachedHeaderIconPath = pngPath;
                return pngPath;
            }

            // Find any source icon to resize
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var sourcePaths = new[]
            {
                Path.Combine(appDir, "Assets", "Images", "dark_logo48x48.png"),
                Path.Combine(appDir, "Assets", "Images", "dark_logo128x128.ico"),
                Path.Combine(appDir, "Assets", "Images", "dark_logo32x32.ico"),
                Path.Combine(appDir, "Assets", "Images", "logo256x256.ico"),
            };

            string? sourcePath = null;
            foreach (var path in sourcePaths)
            {
                if (File.Exists(path))
                {
                    sourcePath = path;
                    break;
                }
            }

            if (sourcePath == null)
            {
                LogDebug("No source icon found for toast header resize");
                return null;
            }

            // Decode source icon and resize to 20x20 PNG
            using var sourceStream = File.OpenRead(sourcePath);
            using var codec = SKCodec.Create(sourceStream);
            if (codec == null)
            {
                LogDebug($"Failed to decode source icon: {sourcePath}");
                return null;
            }

            using var sourceBitmap = SKBitmap.Decode(codec);
            if (sourceBitmap == null)
            {
                LogDebug($"Failed to decode bitmap from: {sourcePath}");
                return null;
            }

            const int headerSize = 20;
            using var resized = sourceBitmap.Resize(
                new SKImageInfo(headerSize, headerSize, SKColorType.Rgba8888, SKAlphaType.Premul),
                SKFilterQuality.High);

            if (resized == null)
            {
                LogDebug("Failed to resize icon for toast header");
                return null;
            }

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var outStream = File.OpenWrite(pngPath);
            data.SaveTo(outStream);

            _cachedHeaderIconPath = pngPath;
            LogDebug($"Rendered 20x20 toast header icon: {pngPath}");
            return pngPath;
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to render toast header icon: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Returns an .ico file path for the Windows Start Menu shortcut.
    /// Prefers larger icons since shortcuts display at higher resolution.
    /// </summary>
    private static string? GetShortcutIconPath()
    {
        if (_cachedIcoPath != null && File.Exists(_cachedIcoPath))
            return _cachedIcoPath;

        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var icoPaths = new[]
            {
                Path.Combine(appDir, "Assets", "Images", "logo256x256.ico"),
                Path.Combine(appDir, "Assets", "Images", "dark_logo128x128.ico"),
                Path.Combine(appDir, "Assets", "Images", "dark_logo32x32.ico"),
            };

            foreach (var path in icoPaths)
            {
                if (File.Exists(path))
                {
                    LogDebug($"Found shortcut icon at: {path}");
                    _cachedIcoPath = path;
                    return path;
                }
            }

            var extractedPath = ExtractEmbeddedIcon();
            if (extractedPath != null)
            {
                _cachedIcoPath = extractedPath;
                return extractedPath;
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to get shortcut icon path: {ex.Message}");
        }

        return null;
    }

    private static string? ExtractEmbeddedIcon()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceNames = new[]
            {
                "vTorrent.Assets.Images.logo256x256.ico",
                "vTorrent.Assets.Images.dark_logo128x128.ico",
                "vTorrent.Assets.Images.dark_logo32x32.ico"
            };

            foreach (var resourceName in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    var tempPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "vTorrent", "notification_icon.ico");

                    var dir = Path.GetDirectoryName(tempPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using var fileStream = File.Create(tempPath);
                    stream.CopyTo(fileStream);
                    LogDebug($"Extracted embedded icon to: {tempPath}");
                    return tempPath;
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to extract embedded icon: {ex.Message}");
        }

        return null;
    }

    #endregion

    #region Linux Notifications

    private void ShowLinuxNotification(string title, string message)
    {
        try
        {
            var icon = title.Contains("Complete", StringComparison.OrdinalIgnoreCase) ? "dialog-information" :
                       title.Contains("Failed", StringComparison.OrdinalIgnoreCase) ? "dialog-error" :
                       title.Contains("Added", StringComparison.OrdinalIgnoreCase) ? "list-add" :
                       "dialog-information";

            var psi = new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList = { title, message, "--app-name=vTorrent", "--icon=" + icon },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(5000);
                LogDebug("Linux notification sent via notify-send");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Linux notify-send failed: {ex.Message}");
            ShowLinuxFallbackNotification(title, message);
        }
    }

    private void ShowLinuxFallbackNotification(string title, string message)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "zenity",
                ArgumentList = { "--notification", "--text=" + $"{title}\n{message}" },
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(5000);
                LogDebug("Linux notification sent via zenity");
                return;
            }
        }
        catch
        {
            // zenity not available, try kdialog
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "kdialog",
                ArgumentList = { "--passivepopup", message, "5", "--title", title },
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
            LogDebug("Linux notification sent via kdialog");
        }
        catch (Exception ex)
        {
            LogDebug($"Linux fallback notification failed: {ex.Message}");
        }
    }

    #endregion

    #region macOS Notifications

    private void ShowMacNotification(string title, string message)
    {
        try
        {
            var escapedTitle = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var escapedMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"");

            var script = PlaySound
                ? $"display notification \"{escapedMessage}\" with title \"{escapedTitle}\" sound name \"default\""
                : $"display notification \"{escapedMessage}\" with title \"{escapedTitle}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", script },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (!string.IsNullOrEmpty(error))
                    LogDebug($"macOS notification error: {error}");
                else
                    LogDebug("macOS notification sent via osascript");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"macOS notification failed: {ex.Message}");
            ShowMacFallbackNotification(title, message);
        }
    }

    private void ShowMacFallbackNotification(string title, string message)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "terminal-notifier",
                ArgumentList = { "-title", title, "-message", message, "-appIcon", "vTorrent" },
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!PlaySound)
            {
                psi.ArgumentList.Add("-sound");
                psi.ArgumentList.Add("default");
            }

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
            LogDebug("macOS notification sent via terminal-notifier");
        }
        catch (Exception ex)
        {
            LogDebug($"macOS fallback notification failed: {ex.Message}");
        }
    }

    #endregion

    #region Helpers

    private static void LogDebug(string message)
    {
        try
        {
            var logDir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";
            File.AppendAllText(LogPath, logMessage);
            Debug.WriteLine($"[NotificationService] {message}");
        }
        catch
        {
            // Ignore logging failures
        }
    }

    #endregion
}
